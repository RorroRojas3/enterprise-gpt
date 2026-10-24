import { AssistantUiEvent, AssistantUiEventKind } from '@shared/models/andes/assistant-ui.contract';
import { StreamCodec } from './stream-codec';

/** Exhaustive via `satisfies`, so a contract upgrade that adds a kind fails to compile here. */
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
 * Returns null rather than throwing: the stream has no error surface after the first frame, and
 * an unknown `kind` is the contract's forward-compatibility path.
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
  // A known kind is trusted for its other fields: the contract omits nulls and the fold's
  // defaults absorb them.
  return parsed as AssistantUiEvent;
}

/**
 * Decodes `data: {json}\n\n` frames (docs/conversations/streaming.md). A chunk can end mid-frame
 * or mid-character, so both the decoder and a frame buffer carry state across reads.
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
      buffer += decoder.decode(chunk, { stream: true });
      return drain();
    },

    flush(): AssistantUiEvent[] {
      buffer += decoder.decode();
      const events = drain();
      // A final frame missing only its blank line is still complete; a tail cut mid-JSON is
      // dropped, the "body just stops" ending the contract says clients must tolerate.
      const salvaged = parseFrame(buffer);
      buffer = '';
      if (salvaged !== null) {
        events.push(salvaged);
      }
      return events;
    },
  };
}
