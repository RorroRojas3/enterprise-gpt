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
 * An edge-anchored panel — the admin detail drawer of frame `5c`.
 *
 * Mechanically identical to {@link Modal}: a native `<dialog>` in the top layer, with
 * `margin` pinning it to one edge instead of centring it. Sharing the mechanism keeps
 * one set of focus and dismissal semantics rather than two.
 */
@Component({
  selector: 'app-offcanvas',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './offcanvas.html',
  styleUrl: './offcanvas.scss',
})
export class Offcanvas {
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialogRef = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  readonly open = model<boolean>(false);
  readonly heading = input.required<string>();
  readonly side = input<'start' | 'end'>('end');
  readonly width = input<string>('420px');
  readonly dismissible = input<boolean>(true);
  readonly busy = input<boolean>(false);
  readonly closed = output<void>();

  protected readonly headingId = `offcanvas-heading-${nextId++}`;
  protected readonly canDismiss = computed(() => this.dismissible() && !this.busy());

  private restoreFocus: (() => void) | null = null;

  constructor() {
    afterRenderEffect({
      write: () => {
        const dialog = this.dialogRef().nativeElement;
        const shouldBeOpen = this.open();

        if (shouldBeOpen && !dialog.open) {
          this.restoreFocus = captureFocusOrigin(dialog.ownerDocument);
          dialog.showModal();
          const target =
            dialog.querySelector<HTMLElement>('[data-modal-autofocus]') ??
            tabbableWithin(dialog)[0] ??
            dialog.querySelector<HTMLElement>('h2');
          target?.focus();
        } else if (!shouldBeOpen && dialog.open) {
          dialog.close();
        }
      },
    });

    this.destroyRef.onDestroy(() => {
      // As in Modal: close() queues its event, so by the time it fires the (close)
      // binding is gone and focus would be dropped to <body>.
      const dialog = this.dialogRef().nativeElement;
      if (dialog.open) {
        dialog.close();
      }
      this.restoreFocus?.();
      this.restoreFocus = null;
    });
  }

  protected onCancel(event: Event): void {
    if (!this.canDismiss()) {
      event.preventDefault();
    }
  }

  protected onClose(): void {
    this.open.set(false);
    this.restoreFocus?.();
    this.restoreFocus = null;
    this.closed.emit();
  }

  protected onBackdropClick(event: MouseEvent): void {
    if (event.target === this.dialogRef().nativeElement && this.canDismiss()) {
      this.open.set(false);
    }
  }
}
