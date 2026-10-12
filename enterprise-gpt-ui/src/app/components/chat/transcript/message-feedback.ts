import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MESSAGE_FEEDBACK_RATING, MessageFeedbackRating } from '@domain/api/conversation';
import { Icon } from '@shared/icon/icon';

/**
 * Stateless: the rating lives on `TurnStore`'s entry, which survives a reload. Both thumbs stay
 * mounted, and pressing the pressed one is read by the store as a withdrawal.
 */
@Component({
  selector: 'app-message-feedback',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './message-feedback.html',
  styleUrl: './message-feedback.scss',
  // A rated turn keeps its hover-revealed footer up, so the rating is visible at rest.
  host: { '[class.message-feedback--rated]': 'rating() !== null' },
})
export class MessageFeedback {
  readonly rating = input<MessageFeedbackRating | null>(null);

  /** Disables both thumbs, which also stops a second click racing the first. */
  readonly pending = input<boolean>(false);

  /** The thumb pressed, not the resulting state: only the store knows what is stored. */
  readonly rated = output<MessageFeedbackRating>();

  protected readonly ratings = MESSAGE_FEEDBACK_RATING;

  protected readonly isUp = computed(() => this.rating() === MESSAGE_FEEDBACK_RATING.up);
  protected readonly isDown = computed(() => this.rating() === MESSAGE_FEEDBACK_RATING.down);

  /** `aria-disabled` keeps it clickable, so the press is refused here as well as in `TurnStore`. */
  protected press(rating: MessageFeedbackRating): void {
    if (!this.pending()) {
      this.rated.emit(rating);
    }
  }
}
