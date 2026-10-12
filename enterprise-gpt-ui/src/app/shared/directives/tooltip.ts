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
 * Labels icon-only controls via `aria-label`, not `aria-describedby`, because the text is the
 * name; only when the host has none, visible text included (SC 2.5.3). Shown on hover and visible
 * focus, and Escape dismisses it from anywhere (SC 1.4.13).
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

        // An empty tip is no tip: `Menu`'s absent `hint` would otherwise flash an empty box and
        // name an unnamed host with the empty string.
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
      // A second phase, as the flyout cannot be measured before it exists; same pass, so it never
      // paints at the origin. It reads every signal the write phase reads, so any re-render also
      // re-measures: `flyout` is a plain field and cannot wake this phase.
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
   * Gated on `:focus-visible`: a pointer- or script-focused control was never hovered, so only a
   * click elsewhere would dismiss it. Hiding stays ungated.
   */
  protected showOnVisibleFocus(): void {
    // Every host is a button or a link with no focusable descendants, so it is the node
    // `focusin` bubbled from — `:focus-visible` never matches an ancestor.
    if (this.host.nativeElement.matches(':focus-visible')) {
      this.show();
    }
  }

  protected show(): void {
    // Checked here too for cost: almost no `Menu` passes a hint, and each kebab hover would
    // otherwise cost a change-detection pass and a document listener.
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

    // Re-placed, not dismissed, on resize (a phone's URL bar or keyboard). Scroll is not wired
    // as in `Menu`: its capture-phase listener would fire for the streaming transcript, and
    // dismissing on an unrelated scroll fails SC 1.4.13 (Persistent).
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
   * `position: fixed`, clamped to the viewport: hosts often sit inside an `overflow: hidden`
   * ancestor that would clip an absolutely positioned flyout.
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

    // `documentElement`, the box a fixed element resolves against; `innerWidth`/`innerHeight`
    // include the scrollbar strips, and clamping to those puts the flyout under one.
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
