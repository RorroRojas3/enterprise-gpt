import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import {
  COPIED_LABEL,
  COPY_CONFIRMATION_MS,
  COPY_LABEL,
  copyText,
} from '@core/clipboard/copy-text';
import { Icon } from '@shared/icon/icon';

/** What the control copies, for the part of its accessible name that tells it apart. */
export type CopySubject = 'prompt' | 'response';

/**
 * Frame `1g`'s Copy control, on a user bubble or on a settled answer (US-607).
 *
 * It copies the **source** it is handed rather than anything read back off the
 * page: for an answer that is the accumulated markdown, which is what a reader
 * wants to paste elsewhere and what the rendered HTML is only one view of.
 *
 * Unlike the code-block control (US-603) this is a real template-bound button,
 * so it needs no live region and none is added. That one is minted inside
 * rendered `innerHTML`, where Angular has nothing to bind and every block offers
 * an identically-named "Copy", and it is replaced by the streaming tail within a
 * flush; this one exists only on a settled turn, carries a distinct name per
 * subject, and changes that name on the element the user just activated — which
 * is `StoppedCard`'s existing reasoning for the same confirmation.
 */
@Component({
  selector: 'app-message-copy',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './message-copy.html',
  styleUrl: './message-copy.scss',
  // The footer is revealed on hover and focus, and a confirmation the reader
  // cannot see is no confirmation — so a copied control asks its row to stay up
  // for the length of the window, whatever the pointer does next.
  host: { '[class.message-copy--copied]': 'copied()' },
})
export class MessageCopy {
  readonly text = input.required<string>();
  readonly subject = input.required<CopySubject>();

  protected readonly copied = signal(false);

  protected readonly visibleLabel = computed(() => (this.copied() ? COPIED_LABEL : COPY_LABEL));

  /**
   * The accessible name follows the visible label into "Copied" and back, or
   * SC 2.5.3 fails for the two seconds they differ. It names the subject the
   * rest of the time because a transcript offers one of these per message and
   * "Copy" alone tells a rotor nothing about which.
   *
   * Derived from the same constants the label is, so the two cannot drift apart.
   */
  protected readonly label = computed(() =>
    this.copied() ? COPIED_LABEL : `${COPY_LABEL} ${this.subject()}`,
  );

  private _copiedTimer: ReturnType<typeof setTimeout> | null = null;
  private _destroyed = false;

  constructor() {
    // A route change inside the confirmation window must not fire a write into a
    // destroyed component — including the window between the click and the
    // clipboard settling, which has no timer to cancel yet.
    inject(DestroyRef).onDestroy(() => {
      this._destroyed = true;
      this._clearTimer();
    });
  }

  protected async copy(): Promise<void> {
    // A refused clipboard leaves the button as it was; there is nothing
    // actionable to tell the user beyond "it did not happen".
    if (!(await copyText(this.text())) || this._destroyed) {
      return;
    }

    this.copied.set(true);
    // Re-copying restarts the window rather than letting the first timer cut
    // the second confirmation short.
    this._clearTimer();
    this._copiedTimer = setTimeout(() => this.copied.set(false), COPY_CONFIRMATION_MS);
  }

  private _clearTimer(): void {
    if (this._copiedTimer !== null) {
      clearTimeout(this._copiedTimer);
      this._copiedTimer = null;
    }
  }
}
