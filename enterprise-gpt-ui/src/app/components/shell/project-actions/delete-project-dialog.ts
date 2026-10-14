import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  ElementRef,
  computed,
  inject,
} from '@angular/core';
import { ProjectActionsStore } from '@state/project-actions-store';
import { Modal } from '@shared/components/modal/modal';

/**
 * Mounted once in `Shell`, shared by every project invoker. The copy keeps conversations because
 * the API releases them to standalone chats rather than deleting them.
 */
@Component({
  selector: 'app-delete-project-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Modal],
  templateUrl: './delete-project-dialog.html',
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

    // The invoking card is gone, so the modal's focus return has no target. Only when focus
    // actually fell through, so a user who clicked elsewhere is not yanked back.
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
