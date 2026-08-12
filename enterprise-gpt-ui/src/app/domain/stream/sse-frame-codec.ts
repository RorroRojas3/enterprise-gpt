import { AssistantUiEvent, AssistantUiEventKind } from './andes/assistant-ui.contract';
import { StreamCodec } from './stream-codec';

/**
 * The eight kinds this client understands. `satisfies` makes the table
 * exhaustive in both directions: a contract upgrade that adds a ninth kind
 * fails compilation here, forcing a decision instead of silently dropping the
 * new events in production.
 */
const KNOWN_EVENT_KINDS = {
  Status: true,
  ActivityStarted: true,
  ActivityProgress: true,
  ActivityCompleted: true,
  ActivityFailed: true,
  TextDelta: true,
  ReasoningDelta: true,
  Finished: true,
} as const satisfies Record<AssistantUiEventKind, true>;

const DATA_PREFIX = 'data: ';
const FRAME_SEPARATOR = '\n\n';

function isKnownKind(kind: unknown): kind is AssistantUiEventKind {
  return typeof kind === 'string' && Object.hasOwn(KNOWN_EVENT_KINDS, kind);
}

/**
 * Parses one `data: {json}` frame, or returns null for anything that is not a
 * well-formed known event. Skipping rather than throwing is deliberate: the
 * stream has no error surface after the first frame, so throwing would destroy
 * the rest of a live turn's output over a single defective frame — and an
 * unknown `kind` is the documented forward-compatibility path, not an error.
 */
function parseFrame(frame: string): AssistantUiEvent | null {
  if (!frame.startsWith(DATA_PREFIX)) {
    return null;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(frame.slice(DATA_PREFIX.length)) as unknown;
  } catch {
    return null;
  }

  if (typeof parsed !== 'object' || parsed === null) {
    return null;
  }
  if (!isKnownKind((parsed as { kind?: unknown }).kind)) {
    return null;
  }
  // A known kind is trusted for the rest of its fields, matching the
  // contract's "nulls omitted" serialization — the fold's own `?? ''`
  // defaults absorb anything absent.
  return parsed as AssistantUiEvent;
}

/**
 * Decodes the framed chat stream: `data: {json}\n\n`, one event per frame, no
 * `event:`/`id:` lines (see docs/conversations/streaming-contract.md §3.2). A
 * chunk can end anywhere — mid-frame, mid-JSON, mid-character — so both the
 * text decoder and a partial-frame buffer carry state across reads.
 */
export function createSseFrameCodec(): StreamCodec {
  const decoder = new TextDecoder('utf-8');
  let buffer = '';

  function drain(): AssistantUiEvent[] {
    const events: AssistantUiEvent[] = [];
    let separatorAt = buffer.indexOf(FRAME_SEPARATOR);
    while (separatorAt !== -1) {
      const frame = buffer.slice(0, separatorAt);
      buffer = buffer.slice(separatorAt + FRAME_SEPARATOR.length);
      const event = parseFrame(frame);
      if (event !== null) {
        events.push(event);
      }
      separatorAt = buffer.indexOf(FRAME_SEPARATOR);
    }
    return events;
  }

  return {
    decode(chunk: Uint8Array): AssistantUiEvent[] {
      // `stream: true` holds back an incomplete multi-byte sequence at the end
      // of the chunk instead of emitting U+FFFD for it.
      buffer += decoder.decode(chunk, { stream: true });
      return drain();
    },

    flush(): AssistantUiEvent[] {
      buffer += decoder.decode();
      const events = drain();
      // A final frame that lost only its terminating blank line to truncation
      // is still a complete event; a tail cut mid-JSON parses as nothing and
      // is dropped — that is precisely the "body just stops" ending the
      // contract says a client must tolerate.
      const salvaged = parseFrame(buffer);
      buffer = '';
      if (salvaged !== null) {
        events.push(salvaged);
      }
      return events;
    },
  };
}
