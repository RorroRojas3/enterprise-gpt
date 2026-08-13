import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  computed,
  effect,
  inject,
  viewChild,
} from '@angular/core';
import { canRetry, userMessage } from '@core/errors/error-message';
import { BrandLogo } from '@shared/brand-logo/brand-logo';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { Ridgeline } from '@shared/ridgeline/ridgeline';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { TurnErrorNotice, TurnStore } from '../turn-store';
import { AssistantTurn } from './assistant-turn';
import { StoppedCard } from './stopped-card';
import { TurnNoticeCard, TurnNotice } from './turn-notice-card';

/**
 * The conversation column (US-406): settled entries first, then whichever of
 * the live regions applies — the thinking ridgeline, the streaming turn, a
 * failure notice, or the stopped card. All of it is session state on
 * `TurnStore`; history replay is US-410.
 *
 * `aria-busy` rides the host — the transcript container — for the length of a
 * turn, so assistive tech treats the region as settling rather than
 * announcing every re-render (US-406; the live-region work proper is US-1402).
 */
@Component({
  selector: 'app-transcript',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AssistantTurn, BrandLogo, ErrorPanel, Ridgeline, Skeleton, StoppedCard, TurnNoticeCard],
  templateUrl: './transcript.html',
  styleUrl: './transcript.scss',
  // Reading the stored messages counts as busy too (US-410): the skeleton is
  // aria-hidden, so without this the region is silently empty and then
  // silently full.
  host: { '[attr.aria-busy]': "turn.inFlight() || turn.historyPending() ? 'true' : null" },
})
export class Transcript {
  protected readonly turn = inject(TurnStore);

  private readonly errorCard = viewChild<TurnNoticeCard>('errorCard');

  protected readonly cutOffNotice: TurnNotice = { kind: 'cut-off' };

  /** A 403 or a 404 on the stored messages answers the same however often it is asked. */
  protected readonly canRetry = canRetry;

  protected readonly errorNotice = computed<TurnNotice | null>(() => {
    const notice = this.turn.turnError();

    return notice === null ? null : { kind: 'error', error: notice.error };
  });

  /** Retry is the store's offer (null for a create failure) gated by the arm's own retryability. */
  protected readonly errorRetryable = computed(() => {
    const notice = this.turn.turnError();

    return notice !== null && notice.retry !== null && canRetry(notice.error);
  });

  protected retryNotice(): void {
    const notice: TurnErrorNotice | null = this.turn.turnError();
    if (notice?.retry != null) {
      this.turn.retryTurn(notice.retry);
    }
  }

  constructor() {
    const document = inject(DOCUMENT);

    // Retrying destroys the button that was pressed — the send clears the
    // notice — so focus falls to <body> for the length of the turn. When the
    // turn fails again, which is US-408's whole loop while the other tab holds
    // the conversation, the replacement Retry takes the focus back rather than
    // making a keyboard user re-traverse the page on every attempt.
    //
    // The condition is "focus is nowhere", not "the user retried": a notice
    // raised while the user is somewhere real never steals from them, and the
    // composer stands down for the same reason (see its own fixup).
    effect(() => {
      const card = this.errorCard();
      if (card === undefined || !this.errorRetryable()) {
        return;
      }

      const active = document.activeElement;
      if (active === null || active === document.body || !active.isConnected) {
        card.focusRetry();
      }
    });
  }

  /**
   * The persistent live region's content — the cards themselves are not live
   * regions, because a region created together with its content is not
   * reliably announced (the composer's note documents the same rule). Only
   * abnormal endings are announced here; the full streaming-answer live
   * region treatment is US-1402's.
   */
  protected readonly announcement = computed(() => {
    // Empty for the whole of a turn, so every settle transitions the region's
    // content — without this, a retry that cuts off again would repeat the
    // previous string and announce nothing the second time.
    if (this.turn.inFlight()) {
      return '';
    }

    if (this.turn.stoppedTurn() !== null) {
      return 'Response stopped — not saved to this conversation.';
    }

    const notice = this.turn.turnError();
    if (notice !== null) {
      return userMessage(notice.error);
    }

    const entries = this.turn.entries();
    if (entries[entries.length - 1]?.kind === 'cutOff') {
      return 'This response was cut off.';
    }

    return '';
  });
}
