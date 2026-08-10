import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { retryInterceptor } from './core/http/interceptors/retry.interceptor';
import { ThemeService } from './core/theme/theme-service';

export const appConfig: ApplicationConfig = {
  providers: [
    // Redundant on Angular 21, where zoneless is the default, but stated so the
    // change-detection model is inspectable rather than inferred from the absence
    // of zone.js.
    provideZonelessChangeDetection(),
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    // withFetch so HttpClient and the raw-fetch chat transport share one set of
    // network semantics. Interceptors are functional; MSAL's class interceptor is
    // deliberately not used, because the streaming fetch bypasses HttpClient
    // entirely and both paths must draw tokens from the same source.
    provideHttpClient(withFetch(), withInterceptors([retryInterceptor])),
    // Eager, so ownership of <html>'s theme attributes is explicit rather than a side
    // effect of whichever component happens to inject the service first — a route
    // with no theme control still has to follow the OS when the preference is
    // `system`.
    provideAppInitializer(() => {
      inject(ThemeService);
    }),
  ],
};
