import { isDevMode } from '@angular/core';
import { Routes } from '@angular/router';
import { adminCanMatch } from '@core/auth/guards/admin.guard';
import { authGuard } from '@core/auth/guards/auth.guard';
import { sessionGuard } from '@core/auth/guards/session.guard';
import { AuthCallback } from '@features/auth/auth-callback/auth-callback';
import { Forbidden } from '@features/auth/forbidden/forbidden';
import { LoginFailed } from '@features/auth/login-failed/login-failed';
import { SessionError } from '@features/auth/session-error/session-error';
import { SignedOut } from '@features/auth/signed-out/signed-out';

/**
 * Two structural rules govern this table, and both come from how the router actually
 * evaluates guards rather than from taste.
 *
 * **Guards in one array run concurrently.** `runCanActivate` and `runCanMatchGuards`
 * both hand their guards to `combineLatest`, which subscribes to all of them at once.
 * So `canActivate: [authGuard, sessionGuard]` would issue `POST api/users/me` before
 * an account — and therefore a token — exists. Per-route checks, on the other hand,
 * run under `concatMap`, so **nesting is the only way to order two guards**. That is
 * why the authenticated area is two levels deep with one guard each.
 *
 * **`canMatch` runs in a different phase from `canActivate`.** Every `canMatch` in the
 * tree is evaluated during URL recognition, alongside `loadChildren`; `canActivate`
 * runs afterwards. Only `canMatch` can therefore keep a lazy chunk off the wire,
 * which is why `/admin` uses it and establishes its own session — and why `/chat` can
 * safely use `loadComponent`, which the router resolves only after guards pass.
 *
 * The four public routes are unguarded deliberately. `/auth` is the redirect URI, so
 * a guard there would block the handshake it is waiting on; `/login-failed` and
 * `/session-error` have to render precisely when sign-in or the session cannot
 * succeed, so guarding either would be a loop rather than a protection; and
 * `/signed-out` exists specifically so that leaving does not immediately start
 * arriving again.
 */
export const routes: Routes = [
  // First, so `/` resolves without relying on the authenticated tree failing to match
  // its own children and backtracking.
  { path: '', pathMatch: 'full', redirectTo: 'chat' },

  { path: 'auth', component: AuthCallback, title: 'Signing in — Enterprise GPT' },
  {
    path: 'login-failed',
    component: LoginFailed,
    title: 'Sign-in didn’t complete — Enterprise GPT',
  },
  {
    path: 'session-error',
    component: SessionError,
    title: 'Couldn’t start — Enterprise GPT',
  },
  {
    path: 'signed-out',
    component: SignedOut,
    title: 'Signed out — Enterprise GPT',
  },

  {
    path: 'ui-kit',
    canMatch: [() => isDevMode()],
    loadChildren: () => import('@features/ui-kit/ui-kit.routes'),
  },

  {
    path: '',
    canActivate: [authGuard],
    children: [
      {
        path: '',
        canActivate: [sessionGuard],
        children: [
          {
            path: 'chat',
            loadComponent: () => import('@features/chat/chat').then((m) => m.Chat),
            title: 'Chat — Enterprise GPT',
          },
          { path: 'forbidden', component: Forbidden, title: 'Access denied — Enterprise GPT' },
          {
            // canMatch belongs on the route that owns loadChildren: a parent's runs
            // before the child config is resolved, a child's runs after.
            path: 'admin',
            canMatch: [adminCanMatch],
            loadChildren: () => import('@features/admin/admin.routes'),
          },
        ],
      },
    ],
  },

  { path: '**', redirectTo: 'chat' },
];
