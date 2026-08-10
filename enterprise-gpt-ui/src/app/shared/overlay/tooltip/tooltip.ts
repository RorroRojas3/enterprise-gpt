import {
  DOCUMENT,
  DestroyRef,
  Directive,
  ElementRef,
  Renderer2,
  afterRenderEffect,
  inject,
  input,
  signal,
} from '@angular/core';

/**
 * The flyout label for an icon-only control — the collapsed sidebar strip of frame
 * `3b`, and every icon button that has no visible text.
 *
 * It sets `aria-label` on the host rather than `aria-describedby`. This is a
 * deliberate correction of the prototype: the controls it labels have no accessible
 * name at all, so the tooltip text *is* the name. Wiring it as a description would
 * leave the button nameless and have the text announced twice. It only does this when
 * the host has no name of its own — including from its own text content, since
 * overriding a visible label would break WCAG SC 2.5.3 (Label in Name).
 *
 * Shown on focus as well as hover, and dismissed by Escape from anywhere, as WCAG SC
 * 1.4.13 (Dismissible) requires. A host-scoped Escape listener would not do: a tooltip
 * shown by hover while focus is elsewhere would be undismissable, which is precisely
 * the case the criterion exists for.
 */
@Directive({
  selector: '[appTooltip]',
  host: {
    class: 'app-tooltip-host',
    '(mouseenter)': 'show()',
    '(mouseleave)': 'hide()',
    '(focusin)': 'show()',
    '(focusout)': 'hide()',
  },
})
export class Tooltip {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly renderer = inject(Renderer2);
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);

  readonly appTooltip = input.required<string>();
  readonly appTooltipPlacement = input<'right' | 'top'>('right');

  private readonly visible = signal(false);
  private flyout: HTMLElement | null = null;
  private escapeController: AbortController | null = null;

  constructor() {
    // The write phase: this only mutates the DOM. The `position: relative` the flyout
    // needs is set from `_kit.scss` on the host class, so there is no layout read.
    afterRenderEffect({
      write: () => {
        const element = this.host.nativeElement;
        const text = this.appTooltip();

        if (!this.hasOwnName(element)) {
          this.renderer.setAttribute(element, 'aria-label', text);
        }

        if (this.visible()) {
          this.render(text);
        } else {
          this.remove();
        }
      },
    });

    this.destroyRef.onDestroy(() => {
      this.remove();
      this.escapeController?.abort();
    });
  }

  protected show(): void {
    if (this.visible()) {
      return;
    }
    this.visible.set(true);

    // Document-scoped, and only while shown. Escape must dismiss without the user
    // first having to move hover or focus onto the tooltip's host.
    this.escapeController?.abort();
    this.escapeController = new AbortController();
    this.document.addEventListener(
      'keydown',
      (event) => {
        if ((event as KeyboardEvent).key === 'Escape') {
          this.hide();
        }
      },
      { signal: this.escapeController.signal },
    );
  }

  protected hide(): void {
    this.visible.set(false);
    this.escapeController?.abort();
    this.escapeController = null;
  }

  /** A visible text label counts as a name, not only an explicit ARIA one. */
  private hasOwnName(element: HTMLElement): boolean {
    return (
      element.hasAttribute('aria-label') ||
      element.hasAttribute('aria-labelledby') ||
      (element.textContent?.trim().length ?? 0) > 0
    );
  }

  private render(text: string): void {
    if (!this.flyout) {
      this.flyout = this.renderer.createElement('span') as HTMLElement;
      // Decoration: the text is already on the host as its accessible name.
      this.renderer.setAttribute(this.flyout, 'aria-hidden', 'true');
      this.renderer.addClass(this.flyout, 'app-tooltip');
      this.renderer.appendChild(this.host.nativeElement, this.flyout);
    }

    this.renderer.setProperty(this.flyout, 'textContent', text);
    this.renderer.setAttribute(this.flyout, 'data-placement', this.appTooltipPlacement());
  }

  private remove(): void {
    if (this.flyout) {
      this.renderer.removeChild(this.host.nativeElement, this.flyout);
      this.flyout = null;
    }
  }
}
