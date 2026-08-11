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

  /**
   * Exposes the heading line at this level in the document outline.
   *
   * Null — the default — leaves it as plain emphasised text, which is right when the
   * panel sits inside a screen that already has its own heading (frame `4k`). A
   * full-page state where this *is* the page's heading passes a level (frame `6c`
   * passes 1). Expressed as `role`/`aria-level` rather than by switching the element,
   * so one component does not fan out into six template branches.
   */
  readonly headingLevel = input<1 | 2 | 3 | null>(null);

  readonly canRetry = input<boolean>(true);
  readonly retryLabel = input<string>('Retry');

  readonly retried = output<void>();

  protected readonly message = computed(() => userMessage(this.error()));
  protected readonly trace = computed(() => traceLine(this.error()));

  private readonly _headingText = viewChild<ElementRef<HTMLElement>>('headingText');

  /**
   * Moves focus to the heading line, for a full-page state where this panel *is* the
   * page. Only meaningful once {@link headingLevel} is set, which is what makes the
   * line focusable; a no-op otherwise.
   *
   * Exposed as a method so the page does not have to query into markup this component
   * owns.
   */
  focusHeading(): void {
    this._headingText()?.nativeElement.focus();
  }
}
