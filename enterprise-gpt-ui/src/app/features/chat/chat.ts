import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { canRetry } from '@core/errors/error-message';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { ChatEmptyState } from './chat-empty-state';
import { ConversationStore } from './conversation-store';

/**
 * The chat screen, served for both `/chat` and `/chat/{conversationId}` by the single
 * `chatMatcher` route — so moving between them changes a parameter rather than
 * remounting the page.
 *
 * The transcript and the composer arrive with EP-4 and EP-6. Until then an open
 * conversation shows its 52px header (frame `1b`) over the empty state; the header is
 * **absent** rather than empty when no conversation is open, which is frame `1a`.
 * Its favourite star and kebab belong to US-308.
 */
@Component({
  selector: 'app-chat',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChatEmptyState, ErrorPanel, Skeleton],
  providers: [ConversationStore],
  templateUrl: './chat.html',
  styleUrl: './chat.scss',
})
export class Chat {
  /**
   * Bound from the route by `withComponentInputBinding()`, and set back to `undefined`
   * when the parameter disappears — which is how navigating from `/chat/{id}` to
   * `/chat` closes the conversation without a remount.
   */
  readonly conversationId = input<string>();

  protected readonly conversation = inject(ConversationStore);

  /**
   * A 404 here means the conversation does not exist *or* belongs to someone else, and
   * neither starts being true on a second attempt — so the panel offers no Retry.
   */
  protected readonly canRetry = canRetry;

  constructor() {
    // The constructor is an injection context, which a signal-valued reactive method
    // requires: it binds the subscription to this component rather than to the root
    // injector, where it would outlive the screen.
    this.conversation.open(this.conversationId);
  }
}
