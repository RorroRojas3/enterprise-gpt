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
  /** `warn` is frame `1h`'s --warn-bg panel; `surface` its plain-card variant. */
  readonly tone: 'warn' | 'surface';
  /**
   * `stack` is the frame's icon-over-body-over-action card. `row` is its
   * one-line form — icon, message, and the action on the far right — which
   * frame `1h` draws only for the busy warning, where there is nothing to say
   * beyond the single sentence.
   */
  readonly layout: 'stack' | 'row';
  readonly icon: 'bi-exclamation-triangle-fill' | 'bi-plug' | 'bi-hourglass-split';
  /** Colour only — the title's weight follows {@link NoticeView.layout}. */
  readonly titleTone: 'warn' | 'fail' | 'body';
  readonly title: string;
  readonly body: string | null;
  readonly trace: string | null;
}

/**
 * Frame `1h`'s turn-edge notices: the cut-off warning (US-406) and the
 * pre-stream failure cards — tool-server-unavailable (US-403/US-406) and
 * conversation-busy (US-408). Every other arm falls back to the generic
 * warning, which is deliberate: an arm without a designed frame gets the
 * honest generic treatment rather than invented copy.
 *
 * Whether a Retry appears is the caller's decision — {@link retryable} — not
 * this card's, because only the caller knows whether it holds a re-sendable
 * turn. `canRetry` then decides per arm, so an arm the store kept a retry
 * snapshot for can still render without an action.
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

  private readonly retryButton = viewChild<ElementRef<HTMLButtonElement>>('retry');

  /**
   * Puts focus back on Retry.
   *
   * Retrying destroys this card — the notice it renders is cleared by the send
   * — so a failure that raises a fresh one has to hand the control back, or a
   * keyboard user re-tabs to it on every attempt. A method rather than the
   * caller reaching into this markup, mirroring `ErrorPanel.focusHeading`.
   */
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
      // US-408. The copy is the frame's, not `userMessage`'s: a card next to
      // the prompt that caused it can say "another response", where the toast
      // that may fire from anywhere has to name the conversation. No spinner
      // and no trace — nothing is being awaited, and a second tab holding the
      // lock is not a defect worth a correlation id.
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
