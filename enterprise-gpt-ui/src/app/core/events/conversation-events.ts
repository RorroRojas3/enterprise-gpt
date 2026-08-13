import { type } from '@ngrx/signals';
import { eventGroup } from '@ngrx/signals/events';
import { ConversationDto } from '@domain/api/conversation';

/**
 * Events describing server-confirmed changes to a conversation.
 *
 * The Events plugin is the deliberate choice here for the same reachability reason as
 * `sessionEvents`: the store that performs renames and deletes
 * (`ConversationActionsStore`) is root-scoped, while the store showing the open
 * conversation (`ConversationStore`) is scoped to the chat route — a root store cannot
 * reach a route-scoped one by method, and registering route stores with a root service
 * would invert the dependency. Anything with a single observer stays on plain store
 * methods; these events exist because the sidebar list and the open conversation must
 * both react without knowing each other.
 *
 * Both events carry **server-confirmed** facts. Optimistic patches and their rollbacks
 * stay inside `ConversationListStore`, so an event observer never has to undo one.
 */
export const conversationEvents = eventGroup({
  source: 'Conversations',
  events: {
    /**
     * The server confirmed an update; the payload is the DTO it returned — or, for a
     * favourite (US-305), whose 204 carries no body, the target DTO with the accepted
     * flag applied. That synthesis claims nothing the server did not: the favourite
     * request is an idempotent SET of `isFavorite` and touches no other field.
     * Dispatched on rename (US-304) and favourite (US-305); project moves (US-307)
     * follow.
     */
    updated: type<ConversationDto>(),
    /** The server confirmed a deletion (US-306). The payload is the conversation id. */
    deleted: type<string>(),
  },
});
