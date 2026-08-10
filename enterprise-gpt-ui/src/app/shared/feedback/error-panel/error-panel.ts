import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { AppError } from '@core/errors/app-error';
import { traceLine, userMessage } from '@core/errors/error-message';
import { Icon } from '@shared/icon/icon';

/**
 * A surface that failed to load — frames `4k` and `6c`.
 *
 * Distinct from {@link UnavailablePanel} by design, and the difference is not
 * cosmetic: this one says something went wrong that might not go wrong next time, so
 * it names the `traceId` and offers Retry.
 *
 * Copy comes from `core/errors/error-message`, never from the call site, so a panel
 * and a toast raised by the same failure say the same thing.
 */
@Component({
  selector: 'app-error-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './error-panel.html',
  styleUrl: './error-panel.scss',
})
export class ErrorPanel {
  readonly error = input.required<AppError>();

  /** What failed to load, in the user's words: "Documents couldn't load". */
  readonly heading = input.required<string>();

  readonly canRetry = input<boolean>(true);
  readonly retryLabel = input<string>('Retry');

  readonly retried = output<void>();

  protected readonly message = computed(() => userMessage(this.error()));
  protected readonly trace = computed(() => traceLine(this.error()));
}
