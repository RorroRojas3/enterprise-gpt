import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../auth-service';
import { LOGIN_FAILED_ROUTE } from '../auth-routes';

/**
 * Requires a signed-in account, starting the Entra ID redirect when there is none.
 *
 * It sits **alone** on its route level. Guards listed in one `canActivate` array are
 * subscribed together through `combineLatest`, so they all run concurrently —
 * pairing this with `sessionGuard` in a single array would issue
 * `POST api/users/me` before an account, and therefore before a token, exists.
 * Nesting is the router's only ordering primitive: per-route checks run under
 * `concatMap`, so a parent's `canActivate` settles before a child's begins.
 *
 * `canActivate` rather than `canMatch`, for two reasons: a `canMatch` guard receives
 * `UrlSegment[]` and cannot see query parameters, so the return URL would lose them;
 * and the route it protects lazy-loads through `loadComponent`, which the router
 * resolves *after* guards, so no chunk is fetched on the signed-out path either.
 *
 * Every failure returns a `UrlTree`, never a bare `false`. A cancelled navigation
 * renders nothing at all, and `provideStartupShellHandoff` treats `NavigationError`
 * as its cue to remove the startup card — so a guard that simply gives up leaves a
 * genuinely blank page, which is the state US-201 exists to rule out. The one
 * `false` is the line after `signIn` resolves, unreachable in a browser that is
 * already leaving for Entra.
 *
 * Sign-out is the documented exception to that rule, and it is not theoretical. A
 * navigation during teardown would find the handshake memo already cleared, re-run
 * `handleRedirectPromise` — releasing the `interaction_in_progress` lock the logout
 * still needs — find no account, and start a *sign-in*, sending the browser to the
 * authorize endpoint and cancelling the sign-out outright. Returning `false` is safe
 * here precisely because the document is leaving either way, and a `UrlTree` would be
 * one more navigation re-running these same guards.
 *
 * `signIn` is awaited inside a `try` for the same reason. MSAL takes its
 * `interaction_in_progress` lock and discovers the authority metadata *before* it
 * navigates, so an unreachable Entra, a framed redirect, or a lock left behind by an
 * abandoned attempt rejects here — and an unhandled rejection is exactly the blank
 * page above.
 */
export const authGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.isSigningOut()) {
    return false;
  }

  const { account, error } = await auth.ready();

  if (error) {
    return router.parseUrl(LOGIN_FAILED_ROUTE);
  }

  if (account) {
    return true;
  }

  try {
    await auth.signIn(state.url);
  } catch {
    return router.parseUrl(LOGIN_FAILED_ROUTE);
  }

  return false;
};
