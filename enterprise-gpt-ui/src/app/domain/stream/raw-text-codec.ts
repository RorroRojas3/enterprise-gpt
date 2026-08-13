import { AssistantUiEvent } from './andes/assistant-ui.contract';
import { StreamCodec, textDeltaEvent } from './stream-codec';

/**
 * The `features.rawStreamCodec` fallback for a deployment still running a
 * server that predates the framed contract: the body is unframed answer text.
 * Each chunk becomes one synthesized `TextDelta`, so every consumer downstream
 * of the transport — the fold, the turn state, the transcript — is identical
 * for both codecs; only the byte-level decoding differs.
 */
export function createRawTextCodec(): StreamCodec {
  const decoder = new TextDecoder('utf-8');

  return {
    decode(chunk: Uint8Array): AssistantUiEvent[] {
      const text = decoder.decode(chunk, { stream: true });
      return text === '' ? [] : [textDeltaEvent(text)];
    },

    flush(): AssistantUiEvent[] {
      const tail = decoder.decode();
      return tail === '' ? [] : [textDeltaEvent(tail)];
    },
  };
}
