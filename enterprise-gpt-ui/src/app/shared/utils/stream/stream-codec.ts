import { AssistantUiEvent } from '@shared/models/andes/assistant-ui.contract';

/** Stateful across reads (partial frames, split characters), so use one instance per request. */
export interface StreamCodec {
  /** Decodes one body chunk and returns the events it completed, in arrival order. */
  decode(chunk: Uint8Array): AssistantUiEvent[];

  /** Ends the body and returns events still recoverable from the buffer. Call once, last. */
  flush(): AssistantUiEvent[];
}

/**
 * Stamped on client-synthesized events: .NET's default `DateTimeOffset`, recognisably unset and
 * deterministic in tests. The contract forbids ordering by timestamp.
 */
export const DEFAULT_EVENT_TIMESTAMP = '0001-01-01T00:00:00+00:00';

/**
 * The `TextDelta` synthesized for raw-text chunks and replayed messages, shared so a replayed
 * answer and a streamed one are indistinguishable downstream.
 */
export function textDeltaEvent(text: string): AssistantUiEvent {
  return {
    kind: 'TextDelta',
    text,
    depth: 0,
    toolKind: 'Unknown',
    timestamp: DEFAULT_EVENT_TIMESTAMP,
  };
}
