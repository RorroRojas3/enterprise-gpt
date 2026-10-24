import { computed, inject } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import {
  SPEECH_RECOGNITION,
  SpeechRecognitionLike,
  SpeechRecognitionResultEventLike,
} from './speech-recognition-token';

interface DictationState {
  readonly recording: boolean;
  readonly elapsedSeconds: number;
  /**
   * Held until the next attempt: a blocked browser will not prompt again, yet a user who allows it
   * in settings must not keep being told they did not.
   */
  readonly permissionDenied: boolean;
  /** A fresh object per phrase, so the same words twice still propagate. */
  readonly spoken: { readonly text: string } | null;
}

const initialState: DictationState = {
  recording: false,
  elapsedSeconds: 0,
  permissionDenied: false,
  spoken: null,
};

/** Collects the finalized text out of a result event, ignoring interim guesses. */
function finalizedText(event: SpeechRecognitionResultEventLike): string {
  let text = '';

  for (let index = event.resultIndex; index < event.results.length; index++) {
    const result = event.results[index];
    if (result?.isFinal) {
      text += result[0]?.transcript ?? '';
    }
  }

  return text.trim();
}

/**
 * Provided by `Composer`, so tearing the screen down ends the session. `interimResults` stays off:
 * guesses rewrite themselves word by word, in a textarea the user may also be typing in.
 */
export const DictationStore = signalStore(
  withState(initialState),
  withProps(() => ({
    _create: inject(SPEECH_RECOGNITION),
    /** Props, not state: neither renders, and the teardown hook must reach both. */
    _session: {
      current: null as SpeechRecognitionLike | null,
      timer: null as ReturnType<typeof setInterval> | null,
    },
  })),
  withComputed((store) => ({
    /** Whether this browser can dictate at all. False means the control is absent. */
    supported: computed(() => store._create !== null),

    /** `m:ss`, counting the session rather than the words. */
    elapsedLabel: computed(() => {
      const seconds = store.elapsedSeconds();

      return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
    }),
  })),
  withMethods((store) => {
    function clearTimer(): void {
      if (store._session.timer !== null) {
        clearInterval(store._session.timer);
        store._session.timer = null;
      }
    }

    /** Ends the session and returns the control to its idle state. */
    function finish(): void {
      clearTimer();
      store._session.current = null;
      patchState(store, { recording: false, elapsedSeconds: 0 });
    }

    function stop(): void {
      // `stop`, not `abort`, so the engine emits what it has recognized; `onend` then tears down.
      store._session.current?.stop();
    }

    function start(): void {
      if (store._create === null || store.recording()) {
        return;
      }

      const recognition = store._create();
      recognition.continuous = true;
      recognition.interimResults = false;
      recognition.lang = navigator.language;

      recognition.onresult = (event) => {
        const text = finalizedText(event);
        if (text !== '') {
          patchState(store, { spoken: { text } });
        }
      };

      recognition.onerror = (event) => {
        if (event.error === 'not-allowed' || event.error === 'service-not-allowed') {
          patchState(store, { permissionDenied: true });
        }
        // Every other error ends the session silently: the user can still type, and a message
        // about `audio-capture` explains nothing they can act on.
      };

      recognition.onend = () => finish();

      try {
        recognition.start();
      } catch {
        // Chrome throws InvalidStateError if a session is somehow still
        // running. Nothing was started, so nothing needs tearing down.
        return;
      }

      store._session.current = recognition;
      patchState(store, { recording: true, elapsedSeconds: 0, permissionDenied: false });
      store._session.timer = setInterval(() => {
        patchState(store, (state) => ({ elapsedSeconds: state.elapsedSeconds + 1 }));
      }, 1000);
    }

    return {
      start,

      stop,

      toggle(): void {
        if (store.recording()) {
          stop();
          return;
        }

        start();
      },

      /** The composer acknowledges a phrase it has appended. */
      consumeSpoken(): void {
        patchState(store, { spoken: null });
      },

      /** @internal Teardown for the hook below. */
      _abort(): void {
        clearTimer();
        // Detached first: `abort` fires `onend`, and finish() would otherwise
        // patch state on a store being destroyed.
        const session = store._session.current;
        store._session.current = null;
        if (session !== null) {
          session.onend = null;
          session.onresult = null;
          session.onerror = null;
          session.abort();
        }
      },
    };
  }),
  withHooks({
    onDestroy(store) {
      // Leaving the screen must stop the microphone. Nothing else will: the
      // engine holds the device until told otherwise.
      store._abort();
    },
  }),
);
