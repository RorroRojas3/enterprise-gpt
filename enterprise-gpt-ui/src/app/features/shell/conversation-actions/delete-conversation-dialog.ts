import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  ElementRef,
  computed,
  inject,
} from '@angular/core';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { Modal } from '@shared/overlay/modal/modal';

/**
 * The delete-conversation confirmation (US-306, frame `3d`).
 *
 * Mounted once in the shell beside the rename dialog and driven by
 * `ConversationActionsStore`, for the same reason: its invokers live in features
 * that must not import each other. Destructive actions always confirm via modal,
 * naming the thing — and red lives only here and on bulk delete.
 *
 * Confirming removes the invoking row, so the modal's own focus return has nothing
 * to restore to. When that happens — focus fell to `<body>`, or is stranded inside
 * this closed dialog — it is moved to `#main-content`, the one always-present
 * programmatic focus target (it already serves the skip link).
 */
@Component({
  selector: 'app-delete-conversation-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Modal],
  templateUrl: './delete-conversation-dialog.html',
  styleUrl: './delete-conversation-dialog.scss',
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
    // A no-op after confirm (the target is already null); syncs the store on
    // Escape and backdrop closes.
    this.actions.cancelDelete();

    if (!this.confirming) {
      return;
    }
    this.confirming = false;

    // Checked only when focus actually fell through, so a user who clicked
    // elsewhere mid-flight is not yanked back.
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
