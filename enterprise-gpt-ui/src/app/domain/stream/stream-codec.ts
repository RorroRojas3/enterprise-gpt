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
