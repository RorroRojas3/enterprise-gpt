import { DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { type } from '@ngrx/signals';
import { Events, eventGroup } from '@ngrx/signals/events';
import { Observable } from 'rxjs';

/**
 * Sign-out must reset every store across route scopes. Inside a `provideDispatcher()` scope,
 * dispatch `signedOut` with `{ scope: 'global' }` or stores in other scopes never reset.
 */
export const sessionEvents = eventGroup({
  source: 'Session',
  events: {
    /** The session ended; every store composing `withResetOnSignOut` resets. */
    signedOut: type<void>(),
  },
});

/**
 * `takeUntil` this in the inner pipe so an in-flight response cannot refill a reset store. Not
 * a `withResetOnSignOut` prop, because that feature composes last.
 */
export function injectSignedOut(): Observable<unknown> {
  return inject(Events).on(sessionEvents.signedOut);
}

/** Why a request was aborted, so a reader can tell sign-out from a user's Stop. */
export const SIGNED_OUT_ABORT_REASON = 'Signed out.';

/**
 * Mints a distinct `AbortSignal` per request, aborted on sign-out or destroy; unlike `takeUntil`
 * it ends the `fetch` itself. `AbortSignal.any` holds dependents weakly, so minting never leaks.
 * A signal minted after sign-out starts aborted, which is right: sign-in is always a redirect.
 */
export function injectSignedOutAbort(): () => AbortSignal {
  const sessionEnd = new AbortController();
  const abort = (): void => sessionEnd.abort(SIGNED_OUT_ABORT_REASON);

  inject(Events).on(sessionEvents.signedOut).pipe(takeUntilDestroyed()).subscribe(abort);
  // A stream can outlive the route-scoped store that opened it.
  inject(DestroyRef).onDestroy(abort);

  return () => AbortSignal.any([sessionEnd.signal]);
}
