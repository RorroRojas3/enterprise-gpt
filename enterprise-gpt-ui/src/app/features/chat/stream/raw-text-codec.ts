import { AssistantUiEvent } from '@core/api/assistant-ui-contract';
import { StreamCodec, textDeltaEvent } from './stream-codec';

/**
 * The `features.rawStreamCodec` fallback for servers that predate the framed contract. Each chunk
 * becomes one synthesized `TextDelta`, so everything downstream is identical for both codecs.
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
