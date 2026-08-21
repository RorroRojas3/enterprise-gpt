import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MESSAGE_FEEDBACK_RATING, MessageFeedbackRating } from '@domain/api/conversation';
import { Icon } from '@shared/icon/icon';

/**
 * Frame `1g`'s thumbs, beside the Copy control on a settled answer (US-1103).
 *
 * Pure inputs and one output: the rating lives on the transcript entry in `TurnStore`,
 * which is what makes it survive a reload — the store rebuilds the whole replayed block
 * from the server's own `feedback`, so nothing here has to remember anything.
 *
 * Both controls stay mounted whatever the rating is, so the pressed state is a property
 * of a control the reader can find rather than a control that appears and disappears.
 * Clicking the pressed one is not inert: the store reads that as a withdrawal.
 */
@Component({
  selector: 'app-message-feedback',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './message-feedback.html',
  styleUrl: './message-feedback.scss',
  // The footer is revealed on hover and on focus, and a rating nobody can see at rest is
  // not a state that survived anything — so a rated turn asks its row to stay up.
  host: { '[class.message-feedback--rated]': 'rating() !== null' },
})
export class MessageFeedback {
  /** The rating already recorded, or null when this answer has never been rated. */
  readonly rating = input<MessageFeedbackRating | null>(null);

  /**
   * Whether this message's own rating is in flight. Both controls are disabled for its
   * length, which is also what stops a second click racing the first.
   */
  readonly pending = input<boolean>(false);

  /**
   * The thumb that was pressed — not the resulting state. Whether pressing the one
   * already selected sets it again or clears it is the store's decision, because only
   * the store knows what is currently stored.
   */
  readonly rated = output<MessageFeedbackRating>();

  protected readonly ratings = MESSAGE_FEEDBACK_RATING;

  protected readonly isUp = computed(() => this.rating() === MESSAGE_FEEDBACK_RATING.up);
  protected readonly isDown = computed(() => this.rating() === MESSAGE_FEEDBACK_RATING.down);

  /**
   * The guard that `aria-disabled` deliberately does not provide: the button stays
   * focusable and clickable, so refusing the press has to happen here. `TurnStore`
   * refuses re-entry on the pending id as well, which makes this the near half of a
   * guard that holds either way.
   */
  protected press(rating: MessageFeedbackRating): void {
    if (!this.pending()) {
      this.rated.emit(rating);
    }
  }
}
