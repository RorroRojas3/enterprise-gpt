import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { retryInterceptor } from './core/http/interceptors/retry.interceptor';

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
  ],
};
