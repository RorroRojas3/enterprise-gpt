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
import { chatMatcher } from '@features/chat/chat-route';

/**
 * Guards in one array run concurrently, so nesting is the only way to run authGuard before
 * sessionGuard; only `canMatch` keeps a lazy chunk off the wire. The public routes stay
 * unguarded because a guard there would block the sign-in handshake or loop.
 */
export const routes: Routes = [
  // First, so `/` never depends on the authenticated tree failing to match and backtracking.
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
          // Outside the shell: a sidebar would offer the navigation this page exists to refuse.
          { path: 'forbidden', component: Forbidden, title: 'Access denied — Enterprise GPT' },
          {
            // Lazy: the public routes never render this ~24 kB of chrome.
            path: '',
            loadComponent: () => import('@features/shell/shell').then((m) => m.Shell),
            children: [
              {
                // One config for `/chat` and `/chat/{id}`, so the router reuses the component.
                matcher: chatMatcher,
                loadComponent: () => import('@features/chat/chat').then((m) => m.Chat),
                title: 'Chat — Enterprise GPT',
              },
              {
                path: 'conversations',
                loadComponent: () =>
                  import('@features/conversations/conversations').then((m) => m.Conversations),
                title: 'Conversations — Enterprise GPT',
              },
              {
                // loadChildren: the grid and a project's detail share one layout, which
                // hosts the dialogs both of them invoke.
                path: 'projects',
                loadChildren: () => import('@features/projects/projects.routes'),
              },
              {
                path: 'documents',
                loadComponent: () =>
                  import('@features/documents/documents').then((m) => m.Documents),
                title: 'Documents — Enterprise GPT',
              },
              {
                // canMatch on the route that owns loadChildren still runs during URL
                // recognition, so a non-admin's browser never requests the chunk.
                path: 'admin',
                canMatch: [adminCanMatch],
                loadChildren: () => import('@features/admin/admin.routes'),
              },
            ],
          },
        ],
      },
    ],
  },

  { path: '**', redirectTo: 'chat' },
];
