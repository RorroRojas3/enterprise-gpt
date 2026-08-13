import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { canRetry, userMessage } from '@core/errors/error-message';
import { BrandLogo } from '@shared/brand-logo/brand-logo';
import { Ridgeline } from '@shared/ridgeline/ridgeline';
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
  imports: [AssistantTurn, BrandLogo, Ridgeline, StoppedCard, TurnNoticeCard],
  templateUrl: './transcript.html',
  styleUrl: './transcript.scss',
  host: { '[attr.aria-busy]': "turn.inFlight() ? 'true' : null" },
})
export class Transcript {
  protected readonly turn = inject(TurnStore);

  protected readonly cutOffNotice: TurnNotice = { kind: 'cut-off' };

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
