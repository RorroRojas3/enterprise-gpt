import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  linkedSignal,
  signal,
} from '@angular/core';
import { PROJECT_FIELD_LENGTHS } from '@domain/api/project';
import { canRetry } from '@core/errors/error-message';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { Modal } from '@shared/overlay/modal/modal';
import { ProjectStore } from '../project-store';
import { ConfirmsDiscard } from './unsaved-instructions.guard';

/**
 * A project's standing instructions (US-904, frame `4e`).
 *
 * **Saving is not optimistic.** Save shows a pending state and the field only settles
 * into its saved value once the server's `ProjectDto` comes back — the criterion says
 * so, and the reason is that instructions reach the model on every turn of every
 * conversation in the project: a value the client believed and the server rejected
 * would silently change how the whole project answers.
 *
 * The request itself belongs to `ProjectActionsStore`, not here. It is the same
 * `PUT api/projects/{id}` the edit dialog issues, built through the same
 * `toProjectBody` — which is what stops this panel, which shows neither the name nor
 * the description, from clearing both on a full-representation PUT.
 */
@Component({
  selector: 'app-instructions-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ErrorPanel, Modal, Skeleton],
  templateUrl: './instructions-panel.html',
  styleUrl: './instructions-panel.scss',
})
export class InstructionsPanel implements ConfirmsDiscard {
  protected readonly project = inject(ProjectStore);
  protected readonly actions = inject(ProjectActionsStore);
  protected readonly maxLength = PROJECT_FIELD_LENGTHS.instructions;
  protected readonly canRetry = canRetry;

  /**
   * The edited text.
   *
   * `linkedSignal` over the saved value, so the field re-seeds when the server confirms
   * a save and when the reader navigates to another project — and so a plain signal
   * plus an effect, which would have to decide what "the user has typed since" means,
   * is not needed. A `form()` would buy nothing here: one field, one rule, and the cap
   * is a native `maxlength` the textarea enforces itself.
   */
  protected readonly draft = linkedSignal(() => this.project.project()?.instructions ?? '');

  /** The discard confirmation is open, and something is waiting on its answer. */
  protected readonly confirmOpen = signal(false);
  private resolveDiscard: ((discard: boolean) => void) | null = null;

  protected readonly saved = computed(() => this.project.project()?.instructions ?? '');
  protected readonly isDirty = computed(() => this.draft() !== this.saved());
  protected readonly charCount = computed(() => this.draft().length);

  /**
   * A write to *this project* is in flight.
   *
   * Deliberately not narrowed to saves this panel started: `ProjectActionsStore` runs
   * create and update through one `exhaustMap`, so a rename begun from the header would
   * drop a Save pressed under it. Disabling the field for that round trip is the honest
   * reading — both writers PUT the same full representation, and letting them race
   * would mean one silently overwriting the other's echo.
   */
  protected readonly saving = computed(() => {
    const id = this.project.project()?.id;

    return id !== undefined && this.actions.isRowPending(id) && this.actions.formBusy();
  });

  protected readonly canSave = computed(
    () => this.isDirty() && !this.saving() && this.draft().length <= this.maxLength,
  );

  /**
   * Answers the route guard.
   *
   * Resolves immediately when nothing is unsaved, which is the common case and must not
   * cost the reader a modal. A save already in flight counts as safe to leave: the
   * request belongs to the root actions store and outlives this panel, and the value
   * the reader typed is the one the server is being given.
   */
  confirmDiscard(): Promise<boolean> {
    // A deleted project is the one case where refusing would trap the reader: the
    // screen is navigating away precisely because the project is gone, the PUT this
    // edit would become is a 404, and cancelling the navigation leaves them on a URL
    // that 404s on the next refresh — with no second chance, because the store's
    // `deleted` flag is a latch and the effect that navigates will not re-run.
    if (this.project.deleted() || !this.isDirty() || this.saving()) {
      return Promise.resolve(true);
    }

    this.confirmOpen.set(true);

    return new Promise<boolean>((resolve) => {
      this.resolveDiscard = resolve;
    });
  }

  protected onDraftInput(event: Event): void {
    this.draft.set((event.target as HTMLTextAreaElement).value);
  }

  protected save(): void {
    const project = this.project.project();
    if (project === null || !this.canSave()) {
      return;
    }

    this.actions.saveInstructions(project, this.draft());
  }

  /** Frame `4e`'s Cancel: throws the edit away without leaving the panel. */
  protected revert(): void {
    this.draft.set(this.saved());
  }

  protected discard(): void {
    this.confirmOpen.set(false);
    // Reset before releasing the navigation, so the guard does not fire a second time
    // against the edit the reader just agreed to lose.
    this.draft.set(this.saved());
    this.settleDiscard(true);
  }

  protected keepEditing(): void {
    this.confirmOpen.set(false);
    this.settleDiscard(false);
  }

  /**
   * Releases the guard, once.
   *
   * Called from both close paths and from `discard`, so it has to be idempotent: the
   * modal's `(closed)` fires *after* `discard` has already answered true, and letting
   * that second call through would resolve the same promise with the opposite value.
   */
  private settleDiscard(discard: boolean): void {
    this.resolveDiscard?.(discard);
    this.resolveDiscard = null;
  }
}
