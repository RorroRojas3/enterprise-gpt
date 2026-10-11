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
import { provideBrowserTelemetry } from './core/telemetry/browser-telemetry';
import { ThemeService } from './core/theme/theme-service';

export const appConfig: ApplicationConfig = {
  providers: [
    // The v21 default, stated so the change-detection model is explicit.
    provideZonelessChangeDetection(),
    provideBrowserGlobalErrorListeners(),
    // Without blocking, bootstrap resolves while `authGuard` decides and an empty shell paints
    // behind the startup card.
    provideRouter(routes, withComponentInputBinding(), withEnabledBlockingInitialNavigation()),
    provideStartupShellHandoff(),
    // For every user, upload grant or not: a file dropped outside a drop target would navigate
    // to it, replacing the app mid-turn.
    providePreventDropNavigation(),
    // No MSAL interceptor: the streaming fetch bypasses HttpClient and must share its token
    // source. Retry is outermost so each retried attempt re-authenticates with a fresh token.
    provideHttpClient(withFetch(), withInterceptors([retryInterceptor, authInterceptor])),
    // Eager: a route with no theme control must still follow the OS under `system`.
    provideAppInitializer(() => {
      inject(ThemeService);
    }),
    // Eager: its constructor subscribes this tab to sign-outs in other tabs.
    provideAppInitializer(() => {
      inject(AuthService);
    }),
    // Order is arbitrary: the account is read per telemetry item, not captured here.
    provideBrowserTelemetry(),
  ],
};
