import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  ElementRef,
  computed,
  inject,
} from '@angular/core';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { Modal } from '@shared/overlay/modal/modal';

/**
 * The delete-project confirmation (US-903, frame `4c`'s kebab).
 *
 * Mounted once in `ProjectsLayout` beside the form dialog and driven by
 * `ProjectActionsStore`, so the grid's cards and a project's own header share one
 * instance. Destructive actions always confirm via modal, naming the thing — and red
 * lives only on a delete confirmation.
 *
 * **The body says what the API actually does.** `DeactivateProjectAsync` soft-deletes
 * the project and its documents, but sets `ProjectId` to null on every conversation it
 * held — they survive as standalone chats keeping their own transcripts and their own
 * uploads. A confirmation that said "and its conversations" would be asking the user to
 * agree to something that will not happen.
 *
 * Confirming removes the invoking card, so the modal's own focus return has nothing to
 * restore to. When that happens — focus fell to `<body>`, or is stranded inside this
 * closed dialog — it moves to `#main-content`, the one always-present programmatic
 * focus target.
 */
@Component({
  selector: 'app-delete-project-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Modal],
  templateUrl: './delete-project-dialog.html',
  styleUrl: './delete-project-dialog.scss',
})
export class DeleteProjectDialog {
  protected readonly actions = inject(ProjectActionsStore);
  private readonly document = inject(DOCUMENT);
  private readonly hostRef = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly open = computed(() => this.actions.deleteTarget() !== null);

  /** Set on confirm, so only that close path runs the focus fallback. */
  private confirming = false;

  protected confirm(): void {
    this.confirming = true;
    this.actions.confirmDelete();
  }

  protected onClosed(): void {
    // A no-op after confirm (the target is already null); syncs the store on Escape and
    // backdrop closes.
    this.actions.cancelDelete();

    if (!this.confirming) {
      return;
    }
    this.confirming = false;

    // Checked only when focus actually fell through, so a user who clicked elsewhere
    // mid-flight is not yanked back.
    const active = this.document.activeElement;
    const fellThrough =
      active === null ||
      active === this.document.body ||
      !active.isConnected ||
      this.hostRef.nativeElement.contains(active);

    if (fellThrough) {
      this.document.getElementById('main-content')?.focus();
    }
  }
}
