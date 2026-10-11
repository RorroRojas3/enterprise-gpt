import { type } from '@ngrx/signals';
import { eventGroup } from '@ngrx/signals/events';
import { ConversationDto } from '@domain/api/conversation';

/**
 * Server-confirmed conversation changes. Events, because the root `ConversationActionsStore`
 * cannot reach the route-scoped `ConversationStore`; optimistic rollbacks stay in the list store.
 */
export const conversationEvents = eventGroup({
  source: 'Conversations',
  events: {
    /**
     * The DTO the server returned. A favourite's 204 has no body, so it carries the target DTO
     * with the flag applied: that request is an idempotent SET of `isFavorite` alone.
     */
    updated: type<ConversationDto>(),
    /** The server confirmed a deletion. The payload is the conversation id. */
    deleted: type<string>(),
  },
});
