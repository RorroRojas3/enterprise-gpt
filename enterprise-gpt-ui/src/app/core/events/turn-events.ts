import { type } from '@ngrx/signals';
import { eventGroup } from '@ngrx/signals/events';

/** What a completed turn tells the rest of the app. */
export interface TurnCompleted {
  readonly conversationId: string;
  /**
   * The turn was the conversation's first to complete — the only one after
   * which the server generates a name, so the only one worth refetching for.
   */
  readonly wasFirstTurn: boolean;
}

/**
 * Events describing turns the server actually finished.
 *
 * The Events plugin for the same reachability reason as `conversationEvents`:
 * `TurnStore` is scoped to the chat route and knows nothing about the sidebar
 * list, while `ConversationListStore` is root-scoped and cannot see a
 * route-scoped store to be called by it.
 *
 * Only completion is published, and only on a `Finished` frame. A stopped,
 * cut-off, or failed turn produces nothing here: the server transcribed no
 * answer for it and generated no name, so an observer refetching on one would
 * read back the placeholder it already has.
 */
export const turnEvents = eventGroup({
  source: 'Turn',
  events: {
    completed: type<TurnCompleted>(),
  },
});
