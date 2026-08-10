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
import { captureFocusOrigin } from '@shared/a11y/focus-return';
import { tabbableWithin } from '@shared/a11y/tabbable';

let nextId = 0;

/**
 * A modal dialog.
 *
 * Built on native `<dialog>` + `showModal()` rather than Bootstrap's modal. That is
 * 17.5 kB of JavaScript and 5.8 kB of CSS saved, but the reason is correctness: the
 * top layer inerts everything behind the dialog for real, and supplies `aria-modal`
 * and a cancellable Escape, none of which a `focusin`-cycling trap can match.
 *
 * The kit owns the heading so `aria-labelledby` cannot be forgotten. The body is the
 * default slot; the action row projects into `[modalFooter]`.
 */
@Component({
  selector: 'app-modal',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './modal.html',
  styleUrl: './modal.scss',
})
export class Modal {
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialogRef = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  readonly open = model<boolean>(false);
  readonly heading = input.required<string>();

  /** 420 / 460 / 520px — the three widths the boards use. */
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
          this.focusInitialTarget(dialog);
        } else if (!shouldBeOpen && dialog.open) {
          dialog.close();
        }
      },
    });

    this.destroyRef.onDestroy(() => {
      // A dialog left open while its host is destroyed keeps the page inert. Focus is
      // restored here rather than left to the (close) binding: close() queues its
      // event, and by the time that task runs the view — and the listener — is gone,
      // so a modal closed by a route change would drop focus to <body>.
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

  private focusInitialTarget(dialog: HTMLDialogElement): void {
    // A data attribute rather than `autofocus`: the native attribute means "focus this
    // when the document loads", which is disorienting and is why lint bans it. This
    // one means "focus this when the dialog opens", which is a different and correct
    // thing to want.
    const explicit = dialog.querySelector<HTMLElement>('[data-modal-autofocus]');
    // The heading carries tabindex="-1" precisely so it can be the last resort — a
    // dialog whose focus lands on <body> is one the screen reader never announces.
    const target = explicit ?? tabbableWithin(dialog)[0] ?? dialog.querySelector<HTMLElement>('h2');
    target?.focus();
  }
}
