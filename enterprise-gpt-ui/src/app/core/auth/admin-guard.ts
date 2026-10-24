import { inject } from '@angular/core';
import { CanMatchFn, Router } from '@angular/router';
import { CHAT_ROUTE, FORBIDDEN_ROUTE, SESSION_ERROR_ROUTE } from '@core/navigation/route-paths';
import { SessionBootstrap } from '@core/session/session-bootstrap';
import { SessionStore } from '@core/session/session-store';
import { Auth } from './auth';

/**
 * `canMatch`, on the route carrying `loadChildren`: only it runs before the chunk is fetched. Every
 * `canMatch` precedes every `canActivate`, so it resolves the account and session itself. Returns
 * a `UrlTree` because `false` falls through to the wildcard, except during sign-out.
 */
export const adminCanMatch: CanMatchFn = async () => {
  const auth = inject(Auth);
  const bootstrap = inject(SessionBootstrap);
  const session = inject(SessionStore);
  const router = inject(Router);

  if (auth.isSigningOut()) {
    return false;
  }

  const { account } = await auth.ready();

  if (!account) {
    // Hand off to chat and let `authGuard` start the sign-in; the chunk stays unrequested.
    return router.parseUrl(CHAT_ROUTE);
  }

  if (!(await bootstrap.ensureSession())) {
    return router.parseUrl(SESSION_ERROR_ROUTE);
  }

  return session.isAdministrator() || router.parseUrl(FORBIDDEN_ROUTE);
};
