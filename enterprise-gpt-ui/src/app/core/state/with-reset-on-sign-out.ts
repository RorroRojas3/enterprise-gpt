import { isDevMode, untracked } from '@angular/core';
import {
  getState,
  patchState,
  signalStoreFeature,
  type,
  withHooks,
  withProps,
} from '@ngrx/signals';
import { withEventHandlers } from '@ngrx/signals/events';
import { tap } from 'rxjs';
import { injectSignedOut, sessionEvents } from '@core/events/session-events';

/**
 * Resets the store on {@link sessionEvents} sign-out; an event, since a root service cannot
 * reach route-scoped stores. Compose it last, or later features' keys survive (asserted in dev).
 * In-flight requests still land, so async stores must also cancel on `injectSignedOut()`.
 */
export function withResetOnSignOut<State extends object>() {
  return signalStoreFeature(
    { state: type<State>() },
    withProps((store) => ({
      // Construction can run inside a computed, which would then depend on the whole store.
      _initialState: untracked(() => getState(store)),
    })),
    withEventHandlers((store, signedOut$ = injectSignedOut()) => [
      signedOut$.pipe(tap(() => patchState(store, store._initialState))),
    ]),
    withHooks({
      onInit(store) {
        if (!isDevMode()) {
          return;
        }

        // By onInit the live state has every key, later features' included. Reflect.ownKeys
        // matches what getState and patchState enumerate, symbol keys included.
        const captured = new Set(Reflect.ownKeys(store._initialState));
        const unreset = Reflect.ownKeys(untracked(() => getState(store))).filter(
          (key) => !captured.has(key),
        );

        if (unreset.length > 0) {
          throw new Error(
            `withResetOnSignOut must be the last feature in the store: ` +
              `${unreset.map(String).join(', ')} would survive sign-out.`,
          );
        }
      },
    }),
  );
}
