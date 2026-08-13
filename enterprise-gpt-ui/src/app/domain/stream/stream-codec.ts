import { AssistantUiEvent } from './andes/assistant-ui.contract';

/**
 * An incremental decoder from stream body bytes to {@link AssistantUiEvent}s.
 *
 * One instance per request: both codecs carry state across reads (a partial
 * frame, or an incomplete multi-byte character), so an instance must never be
 * shared between streams. Synchronous by design — byte-to-event decoding has
 * no async work in it, and a pure push API keeps chunk-boundary fixtures
 * trivial to assert against ("after chunk 1, nothing; after chunk 2, one
 * event").
 */
export interface StreamCodec {
  /** Decodes one body chunk and returns the events it completed, in arrival order. */
  decode(chunk: Uint8Array): AssistantUiEvent[];

  /**
   * Signals end-of-body and returns any events still recoverable from buffered
   * input. Call exactly once, after the last chunk.
   */
  flush(): AssistantUiEvent[];
}

/**
 * The timestamp stamped onto events this client synthesizes itself (the
 * raw-text fallback has no wire timestamps to carry). The .NET default
 * `DateTimeOffset` — recognizably "unset", and deterministic under test where
 * a clock read would not be. The contract forbids ordering by timestamp, so no
 * consumer may attach meaning to it.
 */
export const DEFAULT_EVENT_TIMESTAMP = '0001-01-01T00:00:00+00:00';

/**
 * Builds the answer-text event this client synthesizes when text arrives
 * outside the framed stream — the raw-text codec's chunks, and US-410's
 * replayed transcript messages.
 *
 * Shared so both paths produce a byte-identical event: everything downstream
 * of the transport folds it with the same vendored reducers, which is what
 * keeps a replayed answer and a streamed one indistinguishable to the
 * transcript.
 *
 * @param text The answer text the event carries.
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
