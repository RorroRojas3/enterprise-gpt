import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { Icon } from '@shared/icon/icon';

/**
 * Frame `1h`'s detached "Stopped — not saved to this conversation" card
 * (US-407). The dashed border and the left inset are the design's statement
 * that this text is **not** part of the transcript: the server transcribed
 * neither the answer nor the prompt, and the card vanishes on route change,
 * reopen, and reload because the state behind it does.
 *
 * Retry restores the prompt and its turn settings into the composer — it
 * deliberately does not re-send.
 */
@Component({
  selector: 'app-stopped-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './stopped-card.html',
  styleUrl: './stopped-card.scss',
})
export class StoppedCard {
  readonly text = input.required<string>();
  readonly retried = output<void>();

  /** Two seconds of "Copied", the kit's confirmation span; the focused button's label change announces it. */
  protected readonly copied = signal(false);

  private _copiedTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // A route change inside the confirmation window must not fire a write
    // into a destroyed component.
    inject(DestroyRef).onDestroy(() => this._clearTimer());
  }

  protected async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.text());
    } catch {
      // Denied clipboard permission leaves the button as it was; there is
      // nothing actionable to tell the user beyond "it did not happen".
      return;
    }

    this.copied.set(true);
    // Re-copying restarts the window rather than letting the first timer cut
    // the second confirmation short.
    this._clearTimer();
    this._copiedTimer = setTimeout(() => this.copied.set(false), 2000);
  }

  private _clearTimer(): void {
    if (this._copiedTimer !== null) {
      clearTimeout(this._copiedTimer);
      this._copiedTimer = null;
    }
  }
}
