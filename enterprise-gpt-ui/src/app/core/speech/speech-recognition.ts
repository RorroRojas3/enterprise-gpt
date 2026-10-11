import { InjectionToken } from '@angular/core';

/**
 * Declared locally: `lib.dom.d.ts` ships the result types but not `SpeechRecognition`, its events
 * or the `webkit`-prefixed constructor.
 */
export interface SpeechRecognitionLike {
  continuous: boolean;
  interimResults: boolean;
  lang: string;
  start(): void;
  stop(): void;
  abort(): void;
  onresult: ((event: SpeechRecognitionResultEventLike) => void) | null;
  onerror: ((event: SpeechRecognitionErrorEventLike) => void) | null;
  onend: (() => void) | null;
}

/** The `result` event, whose `results` are the standard lib.dom types. */
export interface SpeechRecognitionResultEventLike {
  readonly resultIndex: number;
  readonly results: SpeechRecognitionResultList;
}

/**
 * `not-allowed` and `service-not-allowed` mean a refused microphone; anything else is a failure
 * the user can do nothing about.
 */
export interface SpeechRecognitionErrorEventLike {
  readonly error: string;
}

/** Constructs a recognition session. One per dictation, never reused. */
export type SpeechRecognitionFactory = () => SpeechRecognitionLike;

interface SpeechRecognitionWindow {
  SpeechRecognition?: new () => SpeechRecognitionLike;
  webkitSpeechRecognition?: new () => SpeechRecognitionLike;
}

/**
 * Null where the browser has no recognition (Firefox, non-secure origins), so the composer omits
 * the control rather than disabling it.
 */
export const SPEECH_RECOGNITION = new InjectionToken<SpeechRecognitionFactory | null>(
  'SPEECH_RECOGNITION',
  {
    providedIn: 'root',
    factory: () => {
      const scope = globalThis as unknown as SpeechRecognitionWindow;
      const Recognition = scope.SpeechRecognition ?? scope.webkitSpeechRecognition;

      return Recognition === undefined ? null : () => new Recognition();
    },
  },
);
