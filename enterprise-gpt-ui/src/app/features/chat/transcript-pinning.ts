import {
  DestroyRef,
  Directive,
  ElementRef,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { TurnStore } from './turn-store';

/**
 * Keeps the transcript pinned to the newest content while the reader is at the
 * bottom, and gets out of the way the moment they are not (US-606).
 *
 * Applied to the scroll container itself, so the host element is both the thing
 * scrolled and the observer's root. The zero-height sentinel it watches is passed
 * in rather than queried: it lives after the body's `@if` chain as an
 * unconditional last child, which is what keeps one element — and therefore one
 * observation — alive across the error, transcript and empty-state swaps.
 *
 * Bottom detection is an `IntersectionObserver` rather than a scroll listener
 * because a scroll handler runs on the main thread for every frame of a flick and
 * would still have to measure to answer the only question asked here. The 80px
 * bottom margin is the tolerance: a reader a line or two short of the end is
 * treated as still following.
 *
 * Following is driven by a `ResizeObserver` on the content rather than by the
 * turn's signals, because the answer is not in the DOM when those signals settle:
 * the markdown renderer writes its output a microtask after change detection
 * returns, so a scroll taken during the render phase would measure the height the
 * page had *before* the text it is meant to follow. Watching the content instead
 * means the scroll happens when the geometry actually changes, whatever produced
 * it — and it cannot loop, because moving `scrollTop` changes no element's size.
 */
@Directive({
  selector: '[appTranscriptPinning]',
  exportAs: 'appTranscriptPinning',
})
export class TranscriptPinning {
  /** The zero-height element that marks the end of the scrollable content. */
  readonly sentinel = input.required<HTMLElement>({ alias: 'appTranscriptPinning' });

  /** The element wrapping everything scrollable, watched for height changes. */
  readonly pinningContent = input.required<HTMLElement>();

  private readonly _host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly _turn = inject(TurnStore);
  private readonly _destroyRef = inject(DestroyRef);

  /**
   * Starts true so the first paint pins and no jump control is shown before the
   * observer has said anything. A real observer's first record is asynchronous,
   * and treating "not yet reported" as "scrolled up" would flash the control on
   * every mount.
   */
  private readonly _atBottom = signal(true);

  /**
   * Shown whenever the reader is away from the bottom and there is a transcript
   * to return to — including after the turn ends, because `Finished` deliberately
   * does not scroll and hiding the control then would strand them.
   */
  readonly showJump = computed(() => !this._atBottom() && this._turn.hasContent());

  /** The ring pulse is the streaming signal, so it stops when the turn does. */
  readonly pulsing = computed(() => this._turn.inFlight());

  constructor() {
    afterNextRender(() => {
      const observer = new IntersectionObserver(
        (entries) => {
          const last = entries[entries.length - 1];
          if (last !== undefined) {
            this._atBottom.set(last.isIntersecting);
          }
        },
        { root: this._host, rootMargin: '0px 0px 80px 0px' },
      );

      observer.observe(this.sentinel());
      this._destroyRef.onDestroy(() => observer.disconnect());

      // Fires whenever the answer, an activity card or a notice changes the
      // height — including the markdown renderer's own deferred write, which no
      // Angular lifecycle hook is late enough to catch.
      const resize = new ResizeObserver(() => {
        if (this._atBottom()) {
          this._host.scrollTop = this._host.scrollHeight;
        }
      });

      resize.observe(this.pinningContent());
      this._destroyRef.onDestroy(() => resize.disconnect());
    });
  }

  /**
   * Returns to the newest content and resumes following it.
   *
   * The flag is set here rather than left to the observer so the control
   * disappears on the same change detection as the scroll, instead of a frame
   * later when the sentinel is reported visible again.
   */
  jumpToLatest(): void {
    this._atBottom.set(true);
    this._host.scrollTop = this._host.scrollHeight;
    // The control removes itself on this same change detection, and focus would
    // fall to <body> — losing a keyboard or screen-reader user their place on the
    // page. The transcript is where the control just took them, and it holds
    // focus programmatically for exactly this.
    this._host.focus({ preventScroll: true });
  }
}
