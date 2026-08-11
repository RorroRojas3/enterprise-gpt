import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionBootstrap } from '@core/session/session-bootstrap';
import { AuthService } from '../auth-service';
import { SESSION_ERROR_ROUTE } from '../auth-routes';

/**
 * Requires a resolved session before any screen activates.
 *
 * It sits on its own route level, nested inside `authGuard`'s, because guards in one
 * `canActivate` array run concurrently — see the note on `authGuard`. Nesting is what
 * guarantees `POST api/users/me` is issued only after an account exists.
 *
 * Both `inject` calls happen before the first `await`: a guard loses its injection
 * context the moment it suspends.
 *
 * Failure returns a `UrlTree`, never `false`, so a broken session lands on frame `6c`
 * with its `traceId` and a Retry instead of cancelling the navigation into a blank
 * page. Sign-out is the one exception — see `authGuard` for why. It matters
 * specifically here because `SessionStore` drops its bootstrap memo on sign-out, so an
 * unlatched run would re-issue `POST api/users/me` and repopulate the store for the
 * user who just left.
 */
export const sessionGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const bootstrap = inject(SessionBootstrap);
  const router = inject(Router);

  if (auth.isSigningOut()) {
    return false;
  }

  return (await bootstrap.ensureSession()) || router.parseUrl(SESSION_ERROR_ROUTE);
};
