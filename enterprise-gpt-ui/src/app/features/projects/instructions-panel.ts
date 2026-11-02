import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  linkedSignal,
  signal,
} from '@angular/core';
import { FormField, form, maxLength, readonly } from '@angular/forms/signals';
import { PROJECT_FIELD_LENGTHS } from '@core/api/project.model';
import { canRetry } from '@core/errors/error-message';
import { ErrorPanel } from '@core/errors/error-panel';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { Modal } from '@shared/dialog/modal';
import { Skeleton } from '@shared/loading/skeleton';
import { ProjectStore } from './project-store';
import { ConfirmsDiscard } from './unsaved-instructions-guard';

/**
 * Not optimistic: instructions reach the model on every turn, so a value the server rejected would
 * silently change how the project answers. Saves go through `toProjectBody`, which echoes the name
 * and description this panel never shows.
 */
@Component({
  selector: 'app-instructions-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ErrorPanel, FormField, Modal, Skeleton],
  host: { class: 'd-block' },
  templateUrl: './instructions-panel.html',
})
export class InstructionsPanel implements ConfirmsDiscard {
  protected readonly project = inject(ProjectStore);
  protected readonly actions = inject(ProjectActionsStore);
  protected readonly maxLength = PROJECT_FIELD_LENGTHS.instructions;
  protected readonly canRetry = canRetry;

  /** By value: a favourite or rename replaces the project object and must keep an unsaved edit. */
  private readonly _seed = computed(
    () => {
      const project = this.project.project();

      return { id: project?.id ?? null, instructions: project?.instructions ?? '' };
    },
    { equal: (a, b) => a.id === b.id && a.instructions === b.instructions },
  );

  /** Re-seeds when the server confirms a save or the reader moves to another project. */
  private readonly _draft = linkedSignal(() => ({ instructions: this._seed().instructions }));

  /** Read-only rather than disabled while saving, so the field keeps focus. */
  protected readonly f = form(this._draft, (path) => {
    maxLength(path.instructions, this.maxLength);
    readonly(path.instructions, () => this.saving());
  });

  /** The discard confirmation is open, and something is waiting on its answer. */
  protected readonly confirmOpen = signal(false);
  private resolveDiscard: ((discard: boolean) => void) | null = null;

  private readonly _text = computed(() => this.f.instructions().value());
  private readonly _saved = computed(() => this._seed().instructions);

  /** Trimmed, as the save is: a whitespace-only edit leaves nothing for the server to reseed. */
  protected readonly isDirty = computed(() => this._text().trim() !== this._saved().trim());
  protected readonly charCount = computed(() => this._text().length);

  /**
   * Any write to this project, not only this panel's: `ProjectActionsStore` runs writes through one
   * `exhaustMap`, so a rename begun from the header would drop a Save pressed under it.
   */
  protected readonly saving = computed(() => {
    const id = this.project.project()?.id;

    return id !== undefined && this.actions.isRowPending(id) && this.actions.formBusy();
  });

  /** `formBusy`, not `saving`: the store drops a Save while any other project's write runs. */
  protected readonly canSave = computed(
    () => this.isDirty() && !this.actions.formBusy() && !this.f().invalid(),
  );

  protected readonly canRevert = computed(() => this.isDirty() && !this.saving());

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

  protected save(): void {
    const project = this.project.project();
    if (project === null || !this.canSave()) {
      return;
    }

    this.actions.saveInstructions(project, this._text());
  }

  /** Cancel: throws the edit away without leaving the panel. */
  protected revert(): void {
    if (this.canRevert()) {
      this._reseed();
    }
  }

  protected discard(): void {
    this.confirmOpen.set(false);
    // Reset first, so the guard does not fire again against the edit just discarded.
    this._reseed();
    this.settleDiscard(true);
  }

  protected keepEditing(): void {
    this.confirmOpen.set(false);
    this.settleDiscard(false);
  }

  private _reseed(): void {
    this._draft.set({ instructions: this._saved() });
  }

  /** Idempotent: the modal's `(closed)` fires after `discard` answered, and must not flip it. */
  private settleDiscard(discard: boolean): void {
    this.resolveDiscard?.(discard);
    this.resolveDiscard = null;
  }
}
