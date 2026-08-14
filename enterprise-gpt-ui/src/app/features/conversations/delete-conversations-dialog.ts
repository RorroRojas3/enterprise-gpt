import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  ElementRef,
  computed,
  inject,
  input,
  model,
  output,
} from '@angular/core';
import { Modal } from '@shared/overlay/modal/modal';

/**
 * The bulk-delete confirmation for the conversations library (US-704, frame `4a`).
 *
 * Destructive actions always confirm via modal naming what is going — the board's own
 * note for this frame — and red lives only here and on the single-row delete.
 *
 * Unlike `DeleteConversationDialog` this takes inputs rather than a store. It has one
 * invoker rather than two features that must not import each other, and the same
 * "delete N selected rows" shape is what a project's conversations tab (US-908) will
 * want; a route-scoped store baked in here would have to be swapped out for that.
 *
 * Confirming removes every selected row, so the bulk bar that opened this — and the
 * button inside it that the modal would return focus to — is gone by the time it
 * closes. When focus has fallen through, it goes to `#main-content`, the one
 * always-present programmatic target.
 */
@Component({
  selector: 'app-delete-conversations-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Modal],
  templateUrl: './delete-conversations-dialog.html',
})
export class DeleteConversationsDialog {
  readonly open = model.required<boolean>();

  /** How many rows are going. Zero never renders — the bar it lives under is gone. */
  readonly count = input.required<number>();

  readonly confirmed = output<void>();

  private readonly document = inject(DOCUMENT);
  private readonly hostRef = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly heading = computed(() =>
    this.count() === 1 ? 'Delete conversation?' : `Delete ${this.count()} conversations?`,
  );

  protected readonly body = computed(() =>
    this.count() === 1
      ? 'This conversation and its messages will be permanently deleted. Files it uploaded stay in your documents.'
      : `These ${this.count()} conversations and their messages will be permanently deleted. Files they uploaded stay in your documents.`,
  );

  /** Set on confirm, so only that close path runs the focus fallback. */
  private confirming = false;

  protected confirm(): void {
    this.confirming = true;
    this.open.set(false);
    this.confirmed.emit();
  }

  protected onClosed(): void {
    this.open.set(false);

    if (!this.confirming) {
      return;
    }
    this.confirming = false;

    // Checked only when focus actually fell through, so a reader who clicked
    // elsewhere while the request settled is not yanked back.
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
