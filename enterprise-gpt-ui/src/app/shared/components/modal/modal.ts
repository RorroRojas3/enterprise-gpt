import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  input,
  model,
  output,
  viewChild,
} from '@angular/core';
import { captureFocusOrigin } from '@shared/utils/a11y/focus-return';
import { focusInitialTarget } from '@shared/utils/a11y/tabbable';

let nextId = 0;

/**
 * Bootstrap's modal CSS on a native `<dialog>` + `showModal()`, not its JS: the top layer truly
 * inerts the page and supplies `aria-modal` and a cancellable Escape. The kit owns the heading so
 * `aria-labelledby` cannot be forgotten.
 */
@Component({
  selector: 'app-modal',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './modal.html',
})
export class Modal {
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialogRef = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  readonly open = model<boolean>(false);
  readonly heading = input.required<string>();

  /** Bootstrap's `.modal-sm`, default and `.modal-lg` widths. */
  readonly size = input<'sm' | 'md' | 'lg'>('md');

  /** When false, neither Escape nor a backdrop click closes it. */
  readonly dismissible = input<boolean>(true);

  /** A save is in flight: the dialog stays put even though it is dismissible. */
  readonly busy = input<boolean>(false);

  readonly closed = output<void>();

  protected readonly headingId = `modal-heading-${nextId++}`;
  protected readonly canDismiss = computed(() => this.dismissible() && !this.busy());

  private restoreFocus: (() => void) | null = null;

  constructor() {
    afterRenderEffect({
      // The write phase only: this mutates the DOM and reads nothing from layout.
      write: () => {
        const dialog = this.dialogRef().nativeElement;
        const shouldBeOpen = this.open();

        if (shouldBeOpen && !dialog.open) {
          this.restoreFocus = captureFocusOrigin(dialog.ownerDocument);
          dialog.showModal();
          focusInitialTarget(dialog);
        } else if (!shouldBeOpen && dialog.open) {
          dialog.close();
        }
      },
    });

    this.destroyRef.onDestroy(() => {
      // A dialog left open on destroy keeps the page inert. Focus is restored here because
      // close() queues its event, which fires after the view and its listener are gone.
      const dialog = this.dialogRef().nativeElement;
      if (dialog.open) {
        dialog.close();
      }
      this.restoreFocus?.();
      this.restoreFocus = null;
    });
  }

  /** The native `cancel` event — Escape. Cancellable, which is the point. */
  protected onCancel(event: Event): void {
    if (!this.canDismiss()) {
      event.preventDefault();
      return;
    }
    // Let the default close run; `onClose` mirrors it back into state.
  }

  /** Fires for Escape, `close()`, and a form submit with `method="dialog"`. */
  protected onClose(): void {
    this.open.set(false);
    this.restoreFocus?.();
    this.restoreFocus = null;
    this.closed.emit();
  }

  /** A click whose target is the dialog itself landed on the backdrop. */
  protected onBackdropClick(event: MouseEvent): void {
    if (event.target === this.dialogRef().nativeElement && this.canDismiss()) {
      this.open.set(false);
    }
  }
}
