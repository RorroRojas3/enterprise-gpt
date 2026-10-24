import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  linkedSignal,
  signal,
} from '@angular/core';
import { PROJECT_FIELD_LENGTHS } from '@shared/models/api/project.model';
import { canRetry } from '@core/errors/error-message';
import { ProjectActionsStore } from '@state/project-actions-store';
import { ProjectStore } from '@state/projects/project-store';
import { ErrorPanel } from '@shared/components/error-panel/error-panel';
import { Modal } from '@shared/components/modal/modal';
import { Skeleton } from '@shared/components/skeleton/skeleton';
import { ConfirmsDiscard } from './unsaved-instructions.guard';

/**
 * Not optimistic: instructions reach the model on every turn, so a value the server rejected would
 * silently change how the project answers. Saves go through `toProjectBody`, which echoes the name
 * and description this panel never shows.
 */
@Component({
  selector: 'app-instructions-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ErrorPanel, Modal, Skeleton],
  host: { class: 'd-block' },
  templateUrl: './instructions-panel.html',
})
export class InstructionsPanel implements ConfirmsDiscard {
  protected readonly project = inject(ProjectStore);
  protected readonly actions = inject(ProjectActionsStore);
  protected readonly maxLength = PROJECT_FIELD_LENGTHS.instructions;
  protected readonly canRetry = canRetry;

  /** Re-seeds when the server confirms a save or the reader moves to another project. */
  protected readonly draft = linkedSignal(() => this.project.project()?.instructions ?? '');

  /** The discard confirmation is open, and something is waiting on its answer. */
  protected readonly confirmOpen = signal(false);
  private resolveDiscard: ((discard: boolean) => void) | null = null;

  protected readonly saved = computed(() => this.project.project()?.instructions ?? '');
  protected readonly isDirty = computed(() => this.draft() !== this.saved());
  protected readonly charCount = computed(() => this.draft().length);

  /**
   * Any write to this project, not only this panel's: `ProjectActionsStore` runs writes through one
   * `exhaustMap`, so a rename begun from the header would drop a Save pressed under it.
   */
  protected readonly saving = computed(() => {
    const id = this.project.project()?.id;

    return id !== undefined && this.actions.isRowPending(id) && this.actions.formBusy();
  });

  protected readonly canSave = computed(
    () => this.isDirty() && !this.saving() && this.draft().length <= this.maxLength,
  );

  /**
   * An in-flight save counts as safe to leave: the root actions store owns the request, which
   * outlives this panel.
   */
  confirmDiscard(): Promise<boolean> {
    // Refusing would trap the reader on a deleted project: the edit would 404, and the
    // `deleted` latch never re-runs the navigation.
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

  /** Cancel: throws the edit away without leaving the panel. */
  protected revert(): void {
    this.draft.set(this.saved());
  }

  protected discard(): void {
    this.confirmOpen.set(false);
    // Reset first, so the guard does not fire again against the edit just discarded.
    this.draft.set(this.saved());
    this.settleDiscard(true);
  }

  protected keepEditing(): void {
    this.confirmOpen.set(false);
    this.settleDiscard(false);
  }

  /** Idempotent: the modal's `(closed)` fires after `discard` answered, and must not flip it. */
  private settleDiscard(discard: boolean): void {
    this.resolveDiscard?.(discard);
    this.resolveDiscard = null;
  }
}
