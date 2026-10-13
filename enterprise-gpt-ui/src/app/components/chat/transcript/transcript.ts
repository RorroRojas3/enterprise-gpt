import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  DestroyRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MessageFeedbackRating } from '@shared/models/api/conversation.model';
import {
  COPIED_LABEL,
  COPY_CONFIRMATION_MS,
  COPY_LABEL,
  copyText,
} from '@shared/utils/clipboard/copy-text';
import { canRetry, userMessage } from '@core/errors/error-message';
import { TurnErrorNotice, TurnStore } from '@state/chat/turn-store';
import { BrandLogo } from '@shared/components/brand-logo/brand-logo';
import { ErrorPanel } from '@shared/components/error-panel/error-panel';
import { Ridgeline } from '@shared/components/ridgeline/ridgeline';
import { Skeleton } from '@shared/components/skeleton/skeleton';
import { AssistantTurn } from './assistant-turn';
import { MessageCopy } from './message-copy';
import { StoppedCard } from './stopped-card';
import { TurnNotice, TurnNoticeCard } from './turn-notice-card';

/**
 * `aria-busy` on the host defers everything inside it for the turn, so the in-flight commentary
 * a reader hears is `Chat`'s. One delegated host listener serves every code block's Copy, which
 * lives in `innerHTML` and is replaced on every flush.
 */
@Component({
  selector: 'app-transcript',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AssistantTurn,
    BrandLogo,
    ErrorPanel,
    MessageCopy,
    Ridgeline,
    Skeleton,
    StoppedCard,
    TurnNoticeCard,
  ],
  templateUrl: './transcript.html',
  styleUrl: './transcript.scss',
  // Busy while history loads, since the skeleton is aria-hidden. `answerComplete` releases it
  // when `Finished` arrives, a step before the settle, so the live turn is still mounted.
  host: {
    class: 'd-flex flex-column gap-3 py-3',
    '[attr.aria-busy]':
      "turn.historyPending() || (turn.inFlight() && !turn.answerComplete()) ? 'true' : null",
    '(click)': 'onClick($event)',
  },
})
export class Transcript {
  protected readonly turn = inject(TurnStore);

  private readonly errorCard = viewChild<TurnNoticeCard>('errorCard');

  private readonly _injector = inject(Injector);

  private _copiedButton: HTMLButtonElement | null = null;
  /** The confirming button's own accessible name, to put back afterwards. */
  private _copiedName: string | null = null;
  private _copiedTimer: ReturnType<typeof setTimeout> | null = null;

  /** Its own region: {@link announcement} is emptied for the turn, and copies happen mid-turn. */
  protected readonly copyAnnouncement = signal('');

  protected readonly cutOffNotice: TurnNotice = { kind: 'cut-off' };

  /** A 403 or a 404 on the stored messages answers the same however often it is asked. */
  protected readonly canRetry = canRetry;

  protected readonly errorNotice = computed<TurnNotice | null>(() => {
    const notice = this.turn.turnError();

    return notice === null ? null : { kind: 'error', error: notice.error };
  });

  /** The store's offer (null for a create failure), gated by the error's own retryability. */
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
   * These narrow the entry's nullable id instead of a non-null assertion in the template. The
   * controls never render for a null id, but both refuse it anyway.
   */
  protected isRatingPending(messageId: string | null): boolean {
    return messageId !== null && this.turn.isRowPending(messageId);
  }

  protected rateMessage(messageId: string | null, rating: MessageFeedbackRating): void {
    if (messageId !== null) {
      this.turn.rateMessage(messageId, rating);
    }
  }

  /** Anything but a code block's Copy, `StoppedCard`'s included, falls straight through. */
  protected onClick(event: MouseEvent): void {
    const target = event.target;
    const button = target instanceof Element ? target.closest('.md-code__copy') : null;
    if (button instanceof HTMLButtonElement) {
      void this.copyCodeBlock(button);
    }
  }

  private async copyCodeBlock(button: HTMLButtonElement): Promise<void> {
    // Prism adds elements but no characters, so `textContent` is the source, less the
    // newline marked appends to close the block.
    const source = button
      .closest('.md-code')
      ?.querySelector('pre > code')
      ?.textContent?.replace(/\n$/, '');
    if (source === undefined) {
      return;
    }

    // A refused clipboard leaves the button as it was; there is nothing
    // actionable to tell the user beyond "it did not happen".
    if (!(await copyText(source))) {
      return;
    }

    // The label is written to the DOM, since `innerHTML` has nothing to bind, and the live
    // region announces it because the tail can replace the button mid-flush. A second copy
    // ends the first confirmation.
    this.endConfirmation();
    this._copiedName = button.getAttribute('aria-label');
    button.textContent = COPIED_LABEL;
    // The name follows the visible label (SC 2.5.3), so "click Copied" has a target.
    button.setAttribute('aria-label', COPIED_LABEL);
    button.classList.add('is-copied');
    this._copiedButton = button;
    this._copiedTimer = setTimeout(() => this.endConfirmation(), COPY_CONFIRMATION_MS);

    // Deferred a render: `endConfirmation` just cleared the region, and setting it in the
    // same task would never mutate it, so a second copy inside the window would be silent.
    afterNextRender(
      { write: () => this.copyAnnouncement.set(COPIED_ANNOUNCEMENT) },
      {
        injector: this._injector,
      },
    );
  }

  private endConfirmation(): void {
    this.copyAnnouncement.set('');

    if (this._copiedTimer !== null) {
      clearTimeout(this._copiedTimer);
      this._copiedTimer = null;
    }

    if (this._copiedButton !== null) {
      this._copiedButton.textContent = COPY_LABEL;
      if (this._copiedName !== null) {
        this._copiedButton.setAttribute('aria-label', this._copiedName);
      }
      this._copiedButton.classList.remove('is-copied');
      this._copiedButton = null;
      this._copiedName = null;
    }
  }

  constructor() {
    const document = inject(DOCUMENT);

    // A route change inside the confirmation window must not fire a write into
    // a detached button.
    inject(DestroyRef).onDestroy(() => this.endConfirmation());

    // Retrying destroys the pressed button, so when the turn fails again the new Retry takes
    // focus back. Only while focus is nowhere: a notice never steals it from somewhere real.
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
   * Abnormal endings, announced from a persistent region: the cards are not live regions, since
   * one created together with its content is not reliably announced.
   */
  protected readonly announcement = computed(() => {
    // Empty while in flight, so a retry that cuts off again still changes the content.
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

const COPIED_ANNOUNCEMENT = 'Code block copied.';
