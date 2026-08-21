import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  DOCUMENT,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import {
  COPIED_LABEL,
  COPY_CONFIRMATION_MS,
  COPY_LABEL,
  copyText,
} from '@core/clipboard/copy-text';
import { MessageFeedbackRating } from '@domain/api/conversation';
import { canRetry, userMessage } from '@core/errors/error-message';
import { BrandLogo } from '@shared/brand-logo/brand-logo';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { Ridgeline } from '@shared/ridgeline/ridgeline';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { TurnErrorNotice, TurnStore } from '../turn-store';
import { AssistantTurn } from './assistant-turn';
import { MessageCopy } from './message-copy';
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
 * announcing every re-render (US-406). US-1402 made it release on the folded
 * `Finished` and gave the streaming answer its own polite live region beneath
 * it; the in-flight announcements a reader actually hears are `Chat`'s, because
 * anything inside this host is deferred along with the answer.
 *
 * The host also carries **one** click listener for every code block's Copy
 * control (US-603). The controls themselves are part of the rendered markdown,
 * which is written as `innerHTML` and has no template position to bind to; one
 * delegated listener here is the alternative to a listener per `<pre>`, and it
 * survives the tail's re-render on every flush because it is not attached to
 * anything the renderer replaces.
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
  // Reading the stored messages counts as busy too (US-410): the skeleton is
  // aria-hidden, so without this the region is silently empty and then
  // silently full.
  //
  // `answerComplete` rather than `inFlight` alone (US-1402): the criterion says
  // the region settles when `Finished` *arrives*, and the fold sets that a step
  // before the settle clears `phase`. Reading the frame is also what leaves the
  // live turn mounted at the moment its deferred live region is released.
  host: {
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

  /**
   * Its own live region rather than a second string in {@link announcement}:
   * that one is emptied for the length of a turn so an abnormal ending always
   * transitions, and a copy can happen mid-turn.
   */
  protected readonly copyAnnouncement = signal('');

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

  /**
   * US-1103's two bindings, narrowing the entry's nullable id for the template.
   *
   * `strictTemplates` will not let a `string | null` reach a `string` parameter, and the
   * alternative is a non-null assertion in the template — which would be a claim the
   * template cannot back, since the same entry type carries a null id for every turn that
   * never became a persisted message. The controls are not rendered in that case, so
   * neither of these is reachable with null; both refuse anyway rather than assert.
   */
  protected isRatingPending(messageId: string | null): boolean {
    return messageId !== null && this.turn.isRowPending(messageId);
  }

  protected rateMessage(messageId: string | null, rating: MessageFeedbackRating): void {
    if (messageId !== null) {
      this.turn.rateMessage(messageId, rating);
    }
  }

  /**
   * The delegated half of US-603: one listener for every code block's Copy
   * control. Anything else clicked in the transcript — including
   * `StoppedCard`'s own Copy, which is a real component with its own handler —
   * does not match and falls straight through.
   */
  protected onClick(event: MouseEvent): void {
    const target = event.target;
    const button = target instanceof Element ? target.closest('.md-code__copy') : null;
    if (button instanceof HTMLButtonElement) {
      void this.copyCodeBlock(button);
    }
  }

  private async copyCodeBlock(button: HTMLButtonElement): Promise<void> {
    // The block's own text, read back off the DOM. Prism's highlighting adds
    // elements but no characters, so `textContent` is the source marked was
    // given — less the newline marked appends to close the block.
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

    // Two channels, because neither serves both audiences. The visible label
    // swap is written straight to the DOM, since the button lives inside
    // rendered `innerHTML` where Angular has nothing to bind — and it is *not*
    // the accessible name, which the renderer fixes with an `aria-label` so six
    // code blocks do not offer six identical "Copy" buttons. A name that no
    // longer changes announces nothing, so the confirmation goes through a live
    // region instead; that also survives the streaming tail's re-render, which
    // replaces the button and its label within a flush.
    //
    // Copying a second block ends the first one's confirmation rather than
    // leaving two "Copied" labels claiming the clipboard at once.
    this.endConfirmation();
    this._copiedName = button.getAttribute('aria-label');
    button.textContent = COPIED_LABEL;
    // The accessible name follows the visible one, or SC 2.5.3 fails for the
    // two seconds it differs and a speech-input user saying "click Copied" has
    // nothing to activate. Announcing is the live region's job now, so a name
    // that also changes is at worst a duplicate rather than the only channel.
    button.setAttribute('aria-label', COPIED_LABEL);
    button.classList.add('is-copied');
    this._copiedButton = button;
    this._copiedTimer = setTimeout(() => this.endConfirmation(), COPY_CONFIRMATION_MS);

    // Deferred one render on purpose. `endConfirmation` has just cleared the
    // region, but change detection is scheduled rather than synchronous, so
    // setting the message in the same task would leave the binding's previous
    // value untouched — and a live region that never mutates announces nothing.
    // Without this, copying a second block inside the first's window is silent.
    afterNextRender(
      { write: () => this.copyAnnouncement.set(COPIED_ANNOUNCEMENT) },
      {
        injector: this._injector,
      },
    );
  }

  /** Restores the confirming button, if there is one, and cancels its timer. */
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

const COPIED_ANNOUNCEMENT = 'Code block copied.';
