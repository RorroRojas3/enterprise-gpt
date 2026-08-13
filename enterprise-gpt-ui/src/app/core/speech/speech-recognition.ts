import { InjectionToken } from '@angular/core';

/**
 * The Web Speech API's recognition object, as much of it as US-413 uses.
 *
 * Declared here because TypeScript 5.9's `lib.dom.d.ts` ships
 * `SpeechRecognitionResult`, `SpeechRecognitionResultList`, and
 * `SpeechRecognitionAlternative` but **not** `SpeechRecognition` itself, its
 * event types, or the `webkit`-prefixed constructor — the interface is still
 * behind a vendor prefix everywhere it exists. A local declaration keeps that
 * gap to one file rather than adding a dependency for four members.
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
 * The `error` event. `not-allowed` and `service-not-allowed` are the two the
 * spec defines for a refused microphone; everything else is a failure the user
 * can do nothing about.
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
 * A factory for speech recognition sessions, or **null** where the browser has
 * none — Firefox today, and any non-secure origin.
 *
 * Null rather than a throwing stub, because US-413's criterion is that the
 * control is *absent* in an unsupported browser rather than present and
 * disabled: the composer asks this token whether to render the control at all.
 *
 * A DI token for the same reason as `STREAM_FETCH`: specs substitute a fake
 * through a provider override instead of assigning to `window`.
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
