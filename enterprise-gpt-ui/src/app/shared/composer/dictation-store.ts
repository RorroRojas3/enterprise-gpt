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
} from '@core/speech/speech-recognition';

interface DictationState {
  readonly recording: boolean;
  readonly elapsedSeconds: number;
  /**
   * The microphone was refused. Held until the next attempt rather than for
   * the session: a browser that has been told "block" will not prompt again,
   * so the message has to outlive the click that earned it — but a user who
   * has since allowed it in browser settings must not keep being told they
   * did not.
   */
  readonly permissionDenied: boolean;
  /**
   * A finalized phrase waiting for the composer to append it. A fresh object
   * per phrase — the same words twice must still propagate — consumed through
   * {@link DictationStore.consumeSpoken}, exactly as `composerSeed` is.
   */
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
 * Dictation for the composer (US-413).
 *
 * Provided by `Composer`, not in root: a recording session belongs to the
 * prompt box that started it, and tearing the screen down has to end it.
 *
 * `interimResults` stays off. Interim guesses rewrite themselves word by word,
 * and a textarea the user may also be typing in is the wrong place to watch
 * that happen — phrases land once they are settled, which is also the only
 * form that survives being edited afterwards.
 */
export const DictationStore = signalStore(
  withState(initialState),
  withProps(() => ({
    _create: inject(SPEECH_RECOGNITION),
    /**
     * The live session and its timer. Mutable props rather than state: neither
     * renders, and both have to be reachable from the teardown hook.
     */
    _session: {
      current: null as SpeechRecognitionLike | null,
      timer: null as ReturnType<typeof setInterval> | null,
    },
  })),
  withComputed((store) => ({
    /** Whether this browser can dictate at all. False means the control is absent. */
    supported: computed(() => store._create !== null),

    /** The frame `2i` clock: `m:ss`, counting the session rather than the words. */
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
      // `stop` rather than `abort`: it lets the engine emit whatever it has
      // already recognized, so the last phrase spoken is not lost. `onend`
      // then runs the teardown, whichever way the session ended.
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
        // Every other error — no speech, no network, an engine failure — ends
        // the session silently. The prompt box is still there to type in, and
        // a message about `audio-capture` explains nothing the user can act on.
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

      /** The one control's single handler, as the send/stop button has. */
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
