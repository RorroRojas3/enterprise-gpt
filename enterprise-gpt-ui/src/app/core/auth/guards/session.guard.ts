import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionBootstrap } from '@core/session/session-bootstrap';
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
 * page.
 */
export const sessionGuard: CanActivateFn = async () => {
  const bootstrap = inject(SessionBootstrap);
  const router = inject(Router);

  return (await bootstrap.ensureSession()) || router.parseUrl(SESSION_ERROR_ROUTE);
};
