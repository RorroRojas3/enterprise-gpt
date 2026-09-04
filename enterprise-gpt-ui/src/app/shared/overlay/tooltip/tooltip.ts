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

/** The gap between the flyout and the control it labels. */
const OFFSET = 8;
/** The gap kept between the flyout and the edge of the viewport. */
const GUTTER = 12;

type Placement = 'right' | 'top' | 'bottom';

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
 * Shown on keyboard-visible focus as well as hover, and dismissed by Escape from
 * anywhere, as WCAG SC 1.4.13 (Dismissible) requires. A host-scoped Escape listener would
 * not do: a tooltip shown by hover while focus is elsewhere would be undismissable, which
 * is precisely the case the criterion exists for.
 */
@Directive({
  selector: '[appTooltip]',
  host: {
    '(mouseenter)': 'show()',
    '(mouseleave)': 'hide()',
    '(focusin)': 'showOnVisibleFocus()',
    '(focusout)': 'hide()',
  },
})
export class Tooltip {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly renderer = inject(Renderer2);
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);

  readonly appTooltip = input.required<string>();
  readonly appTooltipPlacement = input<Placement>('right');

  private readonly visible = signal(false);
  /** Bumped by whatever invalidates the geometry without changing what is shown. */
  private readonly placementTick = signal(0);
  private flyout: HTMLElement | null = null;
  private dismissController: AbortController | null = null;

  constructor() {
    afterRenderEffect({
      write: () => {
        const element = this.host.nativeElement;
        const text = this.appTooltip();

        // An empty tip is no tip. A consumer binding one conditionally — `Menu`'s
        // `hint`, which is null on every menu that has no reason to give — would
        // otherwise flash an empty box on hover and, on an unnamed host, name it
        // with the empty string.
        if (text === '') {
          this.remove();

          return;
        }

        if (!this.hasOwnName(element)) {
          this.renderer.setAttribute(element, 'aria-label', text);
        }

        if (this.visible()) {
          this.render(text);
        } else {
          this.remove();
        }
      },
      // A flyout cannot be measured before it exists, so placing it is a second phase
      // rather than part of the write above. Both run inside one render pass, so it is
      // never painted at the origin it was created at.
      //
      // Every signal the write phase reads is read here too, and that is the invariant to
      // keep: a change that re-renders the box must also re-measure it. `flyout` is a
      // plain field, so this phase can never be woken by the box appearing — it only ever
      // rides the same signal change. Reading the text is what re-places a flyout whose
      // sentence changed under it, not just an early-out.
      mixedReadWrite: () => {
        const placement = this.appTooltipPlacement();
        this.placementTick();
        if (this.appTooltip() === '' || !this.visible() || !this.flyout) {
          return;
        }
        this.place(this.flyout, placement);
      },
    });

    this.destroyRef.onDestroy(() => {
      this.remove();
      this.dismissController?.abort();
    });
  }

  /**
   * A control focused by pointer — or refocused by script after a click destroyed it, as
   * the sidebar chevron is — was never hovered, so nothing but a click elsewhere would ever
   * take the flyout back. Hiding stays ungated, which is what dismisses a hover-shown tip
   * when focus moves away.
   */
  protected showOnVisibleFocus(): void {
    // Every host is a button or a link with no focusable descendants, so it is the node
    // `focusin` bubbled from — `:focus-visible` never matches an ancestor.
    if (this.host.nativeElement.matches(':focus-visible')) {
      this.show();
    }
  }

  protected show(): void {
    // The empty check is here as well as in the write phase, and this is the one that
    // matters for cost: `Menu` binds this directive on every trigger in the application
    // and passes a hint on almost none of them, so without it every kebab hover would
    // write a signal — a change-detection pass, in a zoneless app — and register a
    // document-level listener for a flyout that can never render.
    if (this.visible() || this.appTooltip() === '') {
      return;
    }
    this.visible.set(true);

    this.dismissController?.abort();
    this.dismissController = new AbortController();
    const { signal } = this.dismissController;

    // Document-scoped, and only while shown. Escape must dismiss without the user
    // first having to move hover or focus onto the tooltip's host.
    this.document.addEventListener(
      'keydown',
      (event) => {
        if ((event as KeyboardEvent).key === 'Escape') {
          this.hide();
        }
      },
      { signal },
    );

    // Re-placed rather than dismissed: a resize invalidates the geometry, not the
    // tooltip, and on a phone it fires when the URL bar collapses or the keyboard opens.
    //
    // Scroll is deliberately not wired the way `Menu` wires it. `onDismiss` listens on
    // the document in the capture phase, so it fires for any scroller — including the
    // transcript, which scrolls continuously while a turn streams, which is exactly when
    // the header's hint is on screen. Dismissing on it would also fail WCAG SC 1.4.13
    // (Persistent), which an unrelated scroll does not satisfy. No tooltip host today
    // sits inside a scroller; when one does, the answer is another `place()`, not a hide.
    this.document.defaultView?.addEventListener(
      'resize',
      () => this.placementTick.update((tick) => tick + 1),
      { signal },
    );
  }

  protected hide(): void {
    this.visible.set(false);
    this.dismissController?.abort();
    this.dismissController = null;
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
    // Nothing styles this — `place` writes the geometry. It records the placement that
    // was *asked* for, which a clamp against a viewport edge can then contradict.
    this.renderer.setAttribute(this.flyout, 'data-placement', this.appTooltipPlacement());
  }

  /**
   * Places the flyout in viewport coordinates, clamped so a host near an edge cannot
   * push it off screen.
   *
   * `position: fixed` rather than absolute within the host: these controls sit routinely
   * inside an `overflow: hidden` ancestor — the shell's main column, a scroller — which
   * clips an absolutely-positioned flyout whatever its z-index. The two surfaces that
   * hold the same control disagree about which way it would overflow, so no static
   * offset serves both.
   */
  private place(flyout: HTMLElement, placement: Placement): void {
    const anchor = this.host.nativeElement.getBoundingClientRect();
    const box = flyout.getBoundingClientRect();

    const left =
      placement === 'right'
        ? anchor.right + OFFSET
        : anchor.left + anchor.width / 2 - box.width / 2;
    const top =
      placement === 'top'
        ? anchor.top - box.height - OFFSET
        : placement === 'bottom'
          ? anchor.bottom + OFFSET
          : anchor.top + anchor.height / 2 - box.height / 2;

    // Both axes off `documentElement`, which is the box a fixed element resolves
    // against — `innerWidth`/`innerHeight` include the strips the classic scrollbars sit
    // in, and clamping to those puts the flyout under one.
    const viewportWidth = this.document.documentElement.clientWidth;
    const viewportHeight = this.document.documentElement.clientHeight;

    this.renderer.setStyle(flyout, 'left', `${clampToViewport(left, box.width, viewportWidth)}px`);
    this.renderer.setStyle(flyout, 'top', `${clampToViewport(top, box.height, viewportHeight)}px`);
  }

  private remove(): void {
    if (this.flyout) {
      this.renderer.removeChild(this.host.nativeElement, this.flyout);
      this.flyout = null;
    }
  }
}

/** The inner `max` matters only for a flyout bigger than the viewport, where the bounds cross. */
function clampToViewport(value: number, size: number, extent: number): number {
  return Math.min(Math.max(value, GUTTER), Math.max(extent - size - GUTTER, GUTTER));
}
