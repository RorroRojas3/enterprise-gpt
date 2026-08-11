import { inject } from '@angular/core';
import { CanMatchFn, Router } from '@angular/router';
import { SessionBootstrap } from '@core/session/session-bootstrap';
import { SessionStore } from '@core/session/session-store';
import { AuthService } from '../auth-service';
import { CHAT_ROUTE, FORBIDDEN_ROUTE, SESSION_ERROR_ROUTE } from '../auth-routes';

/**
 * Keeps the admin chunk off a non-administrator's device.
 *
 * **`canMatch`, not `canActivate`, and this is the whole story of US-203.** The
 * router resolves `loadChildren` during URL recognition, and `canMatch` is the only
 * guard that runs in that phase — a `canActivate` guard is evaluated *after* the
 * chunk has already been fetched, which hides the admin area without withholding it.
 * The guard must therefore also sit on the same route object that carries
 * `loadChildren`; on a parent it would run before that route was even reached.
 *
 * The same phase ordering is why this guard cannot lean on `authGuard` and
 * `sessionGuard`: every `canMatch` in the tree runs before any `canActivate`, so it
 * establishes the account and the session itself. That costs nothing — both loads are
 * memoized, so the work is shared with the guards that run afterwards.
 *
 * Every branch returns a `UrlTree`. A `canMatch` returning `false` does not cancel
 * the navigation — it declines the route and lets matching continue, so the URL falls
 * through to the wildcard and the user silently lands on chat with no explanation of
 * why. A `UrlTree` names the destination instead.
 */
export const adminCanMatch: CanMatchFn = async () => {
  const auth = inject(AuthService);
  const bootstrap = inject(SessionBootstrap);
  const session = inject(SessionStore);
  const router = inject(Router);

  const { account } = await auth.ready();

  if (!account) {
    // Deliberately not a sign-in from here: starting one would still have to decide
    // whether to fetch the chunk afterwards. Handing the navigation to the chat route
    // lets `authGuard` run the redirect, and the chunk stays unrequested either way.
    return router.parseUrl(CHAT_ROUTE);
  }

  if (!(await bootstrap.ensureSession())) {
    return router.parseUrl(SESSION_ERROR_ROUTE);
  }

  return session.isAdministrator() || router.parseUrl(FORBIDDEN_ROUTE);
};
