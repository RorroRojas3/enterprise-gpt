import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '@core/auth/auth-service';
import { LOGIN_FAILED_ROUTE } from '@core/navigation/route-paths';

/**
 * Alone on its route level: one `canActivate` array runs concurrently, and `sessionGuard` needs a
 * token first. Not `canMatch`, which cannot see the query parameters the return URL keeps.
 * Failures return a `UrlTree` because a cancelled navigation leaves a blank page; sign-out returns
 * `false`, or a navigation during teardown would start a fresh sign-in.
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
