import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  ElementRef,
  computed,
  inject,
} from '@angular/core';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { Modal } from '@shared/dialog/modal';

/**
 * Mounted once in the shell, driven by `ConversationActionsStore`: its invokers live in features
 * that must not import each other. Confirming removes the invoking row, so focus that falls
 * through moves to `#main-content`, the skip link's always-present target.
 */
@Component({
  selector: 'app-delete-conversation-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Modal],
  templateUrl: './delete-conversation-dialog.html',
})
export class DeleteConversationDialog {
  protected readonly actions = inject(ConversationActionsStore);
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
    // A no-op after confirm; syncs the store on Escape and backdrop closes.
    this.actions.cancelDelete();

    if (!this.confirming) {
      return;
    }
    this.confirming = false;

    // Only when focus fell through, so a user who clicked elsewhere mid-flight is not yanked back.
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
