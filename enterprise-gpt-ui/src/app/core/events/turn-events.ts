import { type } from '@ngrx/signals';
import { eventGroup } from '@ngrx/signals/events';

export interface TurnCompleted {
  readonly conversationId: string;
  /** The server names a conversation only after its first completed turn. */
  readonly wasFirstTurn: boolean;
}

/**
 * Published only on a `Finished` frame: the server saves no answer or name for a stopped,
 * cut-off or failed turn. Events, because the root list store cannot see the route's `TurnStore`.
 */
export const turnEvents = eventGroup({
  source: 'Turn',
  events: {
    completed: type<TurnCompleted>(),
  },
});
