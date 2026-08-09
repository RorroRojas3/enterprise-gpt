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
import { injectSignedOut, sessionEvents } from '../events/session-events';

/**
 * Returns the composing store to its initial state when the session ends.
 *
 * **Compose it last.** `withProps` runs during construction in composition order,
 * so listed last it snapshots the complete state — everything `withState`,
 * `withEntities`, `withRequestStatus` and `withOffsetPagination` contributed — and
 * it takes that snapshot before any `onInit` hook has had the chance to load data
 * into it. A feature composed *after* this one adds a key the snapshot does not
 * carry, and `patchState` then leaves that key untouched on sign-out: the store
 * half-clears and the surviving slice belongs to the previous user. The type system
 * cannot catch that — `State` has no inference site and degrades to `object` — so
 * the ordering is asserted at startup in development instead.
 *
 * The reset is driven by an event rather than by a method a session service calls,
 * because the stores that must clear are route-scoped and a root service cannot
 * reach them. See {@link sessionEvents}.
 *
 * It clears state and nothing else. A request already in flight when sign-out
 * fires will still resolve and write the previous user's rows back over the
 * cleared state, so any store that loads asynchronously must also cancel on
 * `injectSignedOut()` — this feature cannot do that on the store's behalf.
 */
export function withResetOnSignOut<State extends object>() {
  return signalStoreFeature(
    { state: type<State>() },
    withProps((store) => ({
      // untracked because construction can occur inside a reactive context — an
      // injection from within a computed — where an eager read of every state
      // signal would silently make that computed depend on the whole store.
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

        // By onInit the live state carries every key, including those contributed
        // by features composed after this one — which is exactly what the snapshot
        // would have missed. Enumerated with Reflect.ownKeys because that is what
        // getState and patchState use, so the assertion covers exactly what the
        // reset covers, symbol-keyed slices included.
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
