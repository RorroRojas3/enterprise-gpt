import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { AppError } from '@core/errors/app-error';
import { traceLine, userMessage } from '@core/errors/error-message';
import { Icon } from '@shared/icon/icon';

/** What the card explains: a turn that ended early, or one that never started. */
export type TurnNotice =
  { readonly kind: 'cut-off' } | { readonly kind: 'error'; readonly error: AppError };

interface NoticeView {
  /** `warn` is frame `1h`'s --warn-bg panel; `surface` its plain-card variant. */
  readonly tone: 'warn' | 'surface';
  readonly icon: 'bi-exclamation-triangle-fill' | 'bi-plug' | 'bi-hourglass-split';
  readonly titleTone: 'warn' | 'fail' | 'body';
  readonly title: string;
  readonly body: string | null;
  readonly trace: string | null;
}

/**
 * Frame `1h`'s turn-edge notices: the cut-off warning (US-406) and the
 * pre-stream failure cards — of which only the tool-server-unavailable
 * variant is fully designed today (US-403's deferred criterion, retired
 * here). Everything else renders the generic warning until US-408 and
 * US-412 bring their own variants.
 *
 * Session-only by construction: the store state these render from vanishes on
 * route change, sign-out, and reload alike.
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
  /** Whether a Retry is offered — the caller's knowledge, not this card's. */
  readonly retryable = input<boolean>(false);
  readonly retried = output<void>();

  protected readonly view = computed<NoticeView>(() => {
    const notice = this.notice();
    if (notice.kind === 'cut-off') {
      return {
        tone: 'warn',
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
      // Frame 1h draws this as a single hourglass row — no spinner, because
      // nothing is being awaited; US-408 owns the full treatment.
      return {
        tone: 'warn',
        icon: 'bi-hourglass-split',
        titleTone: 'body',
        title: userMessage(error),
        body: null,
        trace: null,
      };
    }

    return {
      tone: 'warn',
      icon: 'bi-exclamation-triangle-fill',
      titleTone: 'warn',
      title: userMessage(error),
      body: null,
      trace: traceLine(error),
    };
  });
}
