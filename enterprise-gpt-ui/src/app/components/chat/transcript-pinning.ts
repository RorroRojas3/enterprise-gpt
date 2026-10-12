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
 * Follows new content while the reader is within 80px of the bottom. A `ResizeObserver` on the
 * content drives it, not turn signals, because markdown renders after change detection; moving
 * `scrollTop` resizes nothing, so it cannot loop.
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

  /** True until told otherwise, so the jump control does not flash before the first report. */
  private readonly _atBottom = signal(true);

  /** Outlives the turn: `Finished` does not scroll, so hiding it then would strand the reader. */
  readonly showJump = computed(() => !this._atBottom() && this._turn.hasContent());

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

      const resize = new ResizeObserver(() => {
        if (this._atBottom()) {
          this._host.scrollTop = this._host.scrollHeight;
        }
      });

      resize.observe(this.pinningContent());
      this._destroyRef.onDestroy(() => resize.disconnect());
    });
  }

  /** Sets the flag itself so the control vanishes with the scroll, not a frame later. */
  jumpToLatest(): void {
    this._atBottom.set(true);
    this._host.scrollTop = this._host.scrollHeight;
    // The control removes itself; catch focus before it falls to <body>.
    this._host.focus({ preventScroll: true });
  }
}
