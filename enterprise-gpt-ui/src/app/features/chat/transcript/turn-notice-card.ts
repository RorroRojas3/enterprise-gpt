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

/** What the card explains: a turn that ended early, or one that never started. */
export type TurnNotice =
  { readonly kind: 'cut-off' } | { readonly kind: 'error'; readonly error: AppError };

interface NoticeView {
  readonly tone: 'warn' | 'surface';
  /** `row` is the one-line form, for the busy warning, which is a single sentence. */
  readonly layout: 'stack' | 'row';
  readonly icon: 'bi-exclamation-triangle-fill' | 'bi-plug' | 'bi-hourglass-split';
  /** Colour only — the title's weight follows {@link NoticeView.layout}. */
  readonly titleTone: 'warn' | 'fail' | 'body';
  readonly title: string;
  readonly body: string | null;
  readonly trace: string | null;
}

/**
 * Arms without designed copy fall back to the generic warning rather than invented text. Retry
 * is the caller's decision, since only it knows whether it holds a re-sendable turn.
 */
@Component({
  selector: 'app-turn-notice-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './turn-notice-card.html',
  styleUrl: './turn-notice-card.scss',
})
export class TurnNoticeCard {
  readonly notice = input.required<TurnNotice>();
  readonly retryable = input<boolean>(false);
  readonly retried = output<void>();

  private readonly retryButton = viewChild<ElementRef<HTMLButtonElement>>('retry');

  /** Retrying destroys this card, so a fresh failure's card takes focus back through this. */
  focusRetry(): void {
    this.retryButton()?.nativeElement.focus();
  }

  protected readonly view = computed<NoticeView>(() => {
    const notice = this.notice();
    if (notice.kind === 'cut-off') {
      return {
        tone: 'warn',
        layout: 'stack',
        icon: 'bi-exclamation-triangle-fill',
        titleTone: 'warn',
        title: 'This response was cut off',
        body: 'The turn ended without a completion signal — the answer below may be incomplete.',
        trace: null,
      };
    }

    const error = notice.error;
    if (error.kind === 'mcp-server-unavailable') {
      return {
        tone: 'surface',
        layout: 'stack',
        icon: 'bi-plug',
        titleTone: 'fail',
        title: error.serverName
          ? `${error.serverName} is unavailable`
          : 'A tool server is unavailable',
        body: 'The tool server could not be reached. This turn produced no answer.',
        trace: traceLine(error),
      };
    }

    if (error.kind === 'conversation-busy') {
      // Not `userMessage`'s copy: beside its prompt the card can say "another response". No
      // trace: a second tab holding the lock is not a defect worth a correlation id.
      return {
        tone: 'warn',
        layout: 'row',
        icon: 'bi-hourglass-split',
        titleTone: 'body',
        title: 'Another response is still running in this conversation.',
        body: null,
        trace: null,
      };
    }

    return {
      tone: 'warn',
      layout: 'stack',
      icon: 'bi-exclamation-triangle-fill',
      titleTone: 'warn',
      title: userMessage(error),
      body: null,
      trace: traceLine(error),
    };
  });
}
