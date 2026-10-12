import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  input,
  output,
  viewChild,
} from '@angular/core';
import { AppError } from '@core/errors/app-error';
import { traceLine, userMessage } from '@core/errors/error-message';
import { Icon } from '@shared/components/icon/icon';

/**
 * A failure that might not recur, so unlike {@link UnavailablePanel} it names the `traceId` and
 * offers Retry. Copy comes from `core/errors/error-message`, so it matches the toast.
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

  /**
   * Null keeps the heading plain text, for a panel inside a screen with its own heading; a
   * full-page state passes a level. `role`/`aria-level` avoids a template branch per element.
   */
  readonly headingLevel = input<1 | 2 | 3 | null>(null);

  readonly canRetry = input<boolean>(true);
  readonly retryLabel = input<string>('Retry');

  readonly retried = output<void>();

  protected readonly message = computed(() => userMessage(this.error()));
  protected readonly trace = computed(() => traceLine(this.error()));

  private readonly _headingText = viewChild<ElementRef<HTMLElement>>('headingText');

  /** For a full-page state; the heading is focusable only once {@link headingLevel} is set. */
  focusHeading(): void {
    this._headingText()?.nativeElement.focus();
  }
}
