import {
  SpeechRecognitionErrorEventLike,
  SpeechRecognitionLike,
  SpeechRecognitionResultEventLike,
} from '../app/core/speech/speech-recognition';

/** A `SpeechRecognitionResultList` shaped as the engine hands one over. */
function resultsOf(phrases: { text: string; isFinal: boolean }[]): SpeechRecognitionResultList {
  const results = phrases.map(({ text, isFinal }) => {
    const alternative = { transcript: text, confidence: 1 };

    return { isFinal, length: 1, 0: alternative, item: () => alternative };
  });

  return Object.assign(results, {
    item: (index: number) => results[index],
  }) as unknown as SpeechRecognitionResultList;
}

/**
 * A scriptable stand-in for the Web Speech API, provided through
 * `SPEECH_RECOGNITION` (US-413).
 *
 * The real interface exists in no test environment — jsdom has none, and it is
 * vendor-prefixed even where it does exist — so dictation is only testable
 * against a fake. Its counters are what let a spec assert that a session was
 * ended rather than abandoned.
 */
export class FakeRecognition implements SpeechRecognitionLike {
  continuous = false;
  interimResults = true;
  lang = '';
  starts = 0;
  stops = 0;
  aborts = 0;
  onresult: ((event: SpeechRecognitionResultEventLike) => void) | null = null;
  onerror: ((event: SpeechRecognitionErrorEventLike) => void) | null = null;
  onend: (() => void) | null = null;

  start(): void {
    this.starts += 1;
  }

  stop(): void {
    this.stops += 1;
    this.onend?.();
  }

  abort(): void {
    this.aborts += 1;
    this.onend?.();
  }

  /** Delivers a result event; `isFinal: false` is an interim guess. */
  say(...phrases: { text: string; isFinal: boolean }[]): void {
    this.onresult?.({ resultIndex: 0, results: resultsOf(phrases) });
  }

  /** Delivers an error, then the `end` the engine always follows it with. */
  fail(error: string): void {
    this.onerror?.({ error });
    this.onend?.();
  }
}
