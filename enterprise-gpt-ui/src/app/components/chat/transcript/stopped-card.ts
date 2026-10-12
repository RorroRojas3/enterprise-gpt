import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { COPY_CONFIRMATION_MS, copyText } from '@shared/utils/clipboard/copy-text';
import { Icon } from '@shared/components/icon/icon';

/**
 * Detached from the transcript because the server saved neither prompt nor answer. Retry
 * restores the prompt and its settings into the composer; it deliberately does not re-send.
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

  /** The focused button's label change is what announces the confirmation. */
  protected readonly copied = signal(false);

  private _copiedTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // A route change inside the confirmation window must not fire a write
    // into a destroyed component.
    inject(DestroyRef).onDestroy(() => this._clearTimer());
  }

  protected async copy(): Promise<void> {
    // A refused clipboard leaves the button as it was; there is nothing
    // actionable to tell the user beyond "it did not happen".
    if (!(await copyText(this.text()))) {
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
