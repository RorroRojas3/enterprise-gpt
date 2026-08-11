import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import {
  provideRouter,
  withComponentInputBinding,
  withEnabledBlockingInitialNavigation,
} from '@angular/router';
import { routes } from './app.routes';
import { AuthService } from './core/auth/auth-service';
import { authInterceptor } from './core/auth/interceptors/auth.interceptor';
import { provideStartupShellHandoff } from './core/config/startup-shell-handoff';
import { providePreventDropNavigation } from './core/dnd/prevent-drop-navigation';
import { retryInterceptor } from './core/http/interceptors/retry.interceptor';
import { ThemeService } from './core/theme/theme-service';

export const appConfig: ApplicationConfig = {
  providers: [
    // Redundant on Angular 21, where zoneless is the default, but stated so the
    // change-detection model is inspectable rather than inferred from the absence
    // of zone.js.
    provideZonelessChangeDetection(),
    provideBrowserGlobalErrorListeners(),
    // Blocking initial navigation is what keeps the first paint honest: without it
    // bootstrap resolves while `authGuard` is still deciding, and an empty app shell
    // renders behind the startup card — the state US-201 forbids. With it, nothing
    // Angular owns paints until the first navigation has been decided.
    provideRouter(routes, withComponentInputBinding(), withEnabledBlockingInitialNavigation()),
    provideStartupShellHandoff(),
    // Applies to every user including one with no upload grant at all: the default
    // action for a file dropped anywhere outside a live drop target is to navigate to
    // it, which replaces the application — and, mid-turn, the answer streaming into it.
    providePreventDropNavigation(),
    // withFetch so HttpClient and the raw-fetch chat transport share one set of
    // network semantics. Interceptors are functional; MSAL's class interceptor is
    // deliberately not used, because the streaming fetch bypasses HttpClient
    // entirely and both paths must draw tokens from the same source.
    //
    // Order is load-bearing: retry is outermost, so each retried attempt re-enters
    // authentication and carries a freshly acquired token rather than replaying the
    // one that already failed.
    provideHttpClient(withFetch(), withInterceptors([retryInterceptor, authInterceptor])),
    // Eager, so ownership of <html>'s theme attributes is explicit rather than a side
    // effect of whichever component happens to inject the service first — a route
    // with no theme control still has to follow the OS when the preference is
    // `system`.
    provideAppInitializer(() => {
      inject(ThemeService);
    }),
    // Eager for the same reason, and it is load-bearing rather than tidy: AuthService's
    // constructor is what subscribes this tab to another tab's sign-out. Left lazy, the
    // cross-tab teardown would exist only on routes that happen to inject the service,
    // and US-301 deleting the dev shell bar would silently switch it off.
    provideAppInitializer(() => {
      inject(AuthService);
    }),
  ],
};
