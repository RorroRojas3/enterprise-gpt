import { DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
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

/** Why a request was aborted, so a reader can tell sign-out from a user's Stop. */
export const SIGNED_OUT_ABORT_REASON = 'Signed out.';

/**
 * Returns a factory that mints one `AbortSignal` per request, aborted when the session
 * ends or when the calling injection context is destroyed.
 *
 * `takeUntil(injectSignedOut())` is not enough for the streaming turn. It stops
 * *delivery* while leaving the HTTP connection open and the model generating on the
 * server; only aborting the underlying `fetch` ends the request. This is the primitive
 * for that path, and EP-4 combines it with the user's Stop as
 * `AbortSignal.any([stop, signedOut()])` — the shape `ToAppErrorOptions.signal` already
 * documents, and which `shouldNotify` maps to no toast.
 *
 * A **factory** rather than one shared signal, because two concurrent requests need
 * signals that can be told apart: a store cancelling one turn must not abort the other,
 * and only a per-request signal makes that expressible. (`AbortSignal.any` composes
 * without consuming its sources, so sharing one would not *break* a caller's Stop — it
 * would just make every request indistinguishable.)
 *
 * `AbortSignal.any` is also what keeps this from accumulating a controller per request:
 * the DOM Standard holds a signal's dependent signals in a **weak set**, so each one is
 * collectable as soon as the caller drops it and a thousand-turn session leaves nothing
 * behind.
 *
 * A signal minted *after* the session has ended is already aborted, which is correct
 * rather than incidental: sign-in in this application is always a redirect, so a
 * session never resumes within the same document and any work starting at that point
 * is doomed regardless. It fails immediately as an `aborted` `AppError`, which raises
 * no toast.
 *
 * Must be called in an injection context.
 */
export function injectSignedOutAbort(): () => AbortSignal {
  const sessionEnd = new AbortController();
  const abort = (): void => sessionEnd.abort(SIGNED_OUT_ABORT_REASON);

  inject(Events).on(sessionEvents.signedOut).pipe(takeUntilDestroyed()).subscribe(abort);
  // A stream can outlive the store that opened it. Without this, a route-scoped store's
  // in-flight request keeps running after the route is gone.
  inject(DestroyRef).onDestroy(abort);

  return () => AbortSignal.any([sessionEnd.signal]);
}
