import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '@core/auth/auth-service';
import { SESSION_ERROR_ROUTE } from '@core/navigation/route-paths';
import { SessionBootstrap } from '@core/session/session-bootstrap-service';

/**
 * Nested inside `authGuard`'s route level so `POST api/users/me` waits for an account. Returns
 * `false` during sign-out, or it would re-bootstrap the session for the user who just left.
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
