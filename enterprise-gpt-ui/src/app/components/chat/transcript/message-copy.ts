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
 * Copies the source it is handed (an answer's markdown), not the rendered page. Unlike the
 * code-block control it needs no live region: its name changes on the element just activated.
 */
@Component({
  selector: 'app-message-copy',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './message-copy.html',
  styleUrl: './message-copy.scss',
  // Keeps the hover-revealed footer up for the confirmation window.
  host: { '[class.message-copy--copied]': 'copied()' },
})
export class MessageCopy {
  readonly text = input.required<string>();
  readonly subject = input.required<CopySubject>();

  protected readonly copied = signal(false);

  protected readonly visibleLabel = computed(() => (this.copied() ? COPIED_LABEL : COPY_LABEL));

  /**
   * Follows the visible label into "Copied" (SC 2.5.3); otherwise names the subject, since a
   * rotor listing one "Copy" per message tells the reader nothing.
   */
  protected readonly label = computed(() =>
    this.copied() ? COPIED_LABEL : `${COPY_LABEL} ${this.subject()}`,
  );

  private _copiedTimer: ReturnType<typeof setTimeout> | null = null;
  private _destroyed = false;

  constructor() {
    // The flag covers the gap between the click and the clipboard settling, which has no
    // timer to cancel yet.
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
