import { inject } from '@angular/core';
import { type } from '@ngrx/signals';
import { Events, eventGroup } from '@ngrx/signals/events';
import { Observable } from 'rxjs';

/**
 * Events describing the lifetime of a signed-in session.
 *
 * The Events plugin is used here deliberately rather than by default. Sign-out is
 * the canonical case it exists for: one thing happens and *every* store has to
 * react to it, across route-scope boundaries, without any of them knowing the
 * others exist. A method on a session service would require each store to be
 * registered with it, which inverts the dependency the wrong way — a root service
 * would have to reach into route-scoped stores.
 *
 * `turnEvents` (US-409) and `adminEvents` (US-1207, US-1208) follow this shape.
 * Everything else stays on plain store methods.
 *
 * **Dispatch `signedOut` globally.** `Dispatcher` and `Events` are `platform`-
 * scoped by default, but a component or route calling `provideDispatcher()`
 * creates a local scope, and an event dispatched inside one is invisible to
 * ancestors and siblings. Sign-out from within such a scope must carry
 * `{ scope: 'global' }`, or the route-scoped stores in every other scope never
 * reset — which is the entire failure this event exists to prevent.
 */
export const sessionEvents = eventGroup({
  source: 'Session',
  events: {
    /**
     * The user signed out, or the session was torn down. Every store composing
     * `withResetOnSignOut` returns to its initial state.
     */
    signedOut: type<void>(),
  },
});

/**
 * An observable that emits when the session ends, for cancelling work in flight.
 *
 * Clearing state on sign-out is not enough on its own: a request that was already
 * on the wire will still resolve and write the previous user's rows back into a
 * store that has just been emptied. Compose this into the inner pipe so the
 * response is dropped instead —
 * `switchMap((x) => api.load(x).pipe(takeUntil(signedOut$), …))`.
 *
 * A function rather than a prop on `withResetOnSignOut`, because that feature has
 * to be composed last and nothing after it could read such a prop.
 *
 * Must be called in an injection context.
 */
export function injectSignedOut(): Observable<unknown> {
  return inject(Events).on(sessionEvents.signedOut);
}
