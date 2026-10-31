import { type } from '@ngrx/signals';
import { eventGroup } from '@ngrx/signals/events';

export interface TurnCompleted {
  readonly conversationId: string;
  /** The server names a conversation only after its first completed turn. */
  readonly wasFirstTurn: boolean;
}

export interface TurnInterrupted {
  readonly conversationId: string;
}

/** Events, because root stores cannot see a chat session's `TurnStore`. */
export const turnEvents = eventGroup({
  source: 'Turn',
  events: {
    /** Only on `Finished`: the server saves nothing for a stopped, cut-off or failed turn. */
    completed: type<TurnCompleted>(),
    /** A cut-off or a failure, never a Stop. */
    interrupted: type<TurnInterrupted>(),
  },
});
