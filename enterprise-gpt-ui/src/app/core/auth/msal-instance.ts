import { InjectionToken } from '@angular/core';
import {
  BrowserCacheLocation,
  Configuration,
  IPublicClientApplication,
  LogLevel,
} from '@azure/msal-browser';
import { AppConfig } from '../config/app-config';

/**
 * The initialized MSAL application.
 *
 * Ours rather than `@azure/msal-angular`'s token of the same name, because this
 * application does not use that package at all. Three verified reasons, none of them
 * stylistic:
 *
 * 1. `MsalGuard.canMatch()` calls its helper with no `RouterStateSnapshot`, and the
 *    helper answers a signed-out user with `of(false)` instead of redirecting. A
 *    `canMatch` guard is the only kind that can keep a lazy chunk off the wire
 *    (US-203), so the one guard shape this application needs is the one that class
 *    cannot provide.
 * 2. `MsalService` caches the redirect hash in its constructor and replays it into
 *    every later `handleRedirectObservable()` call, with `navigateToLoginRequestUrl`
 *    left at its default. After `/auth` has already consumed the response, that
 *    replay either navigates the page again or throws — and the guard turns a throw
 *    into a bounce to `/login-failed` immediately after a *successful* sign-in.
 * 3. `MsalBroadcastService` routes every MSAL event through `NgZone.run`, which
 *    under `provideZonelessChangeDetection()` is a no-op that schedules no change
 *    detection. Anything bound to `inProgress$` would render once and then lie.
 *
 * What replaces it is `AuthService` — one memoized `handleRedirectPromise` and one
 * `loginRedirect` — plus `authGuard`. `MsalInterceptor` was already excluded by the
 * PRD, since the streaming `fetch` never reaches an `HttpClient` interceptor and
 * both paths must draw from one `TokenService`.
 */
export const MSAL_INSTANCE = new InjectionToken<IPublicClientApplication>('MSAL_INSTANCE');

/**
 * Builds MSAL's configuration from the runtime `config.json`.
 *
 * Only the four Entra values are environment-specific. The rest is a fixed security
 * posture, deliberately absent from {@link AppConfig} so that a deployment cannot
 * weaken it by editing one file:
 *
 * - **`sessionStorage`.** Tokens must not outlive the tab on a shared machine, and
 *   PRD §5 states `localStorage` holds the theme and the sidebar state and nothing
 *   else. The visible cost is that a second tab has no token cache and re-establishes
 *   the session through Entra; a top-level redirect still works where third-party
 *   cookies are blocked, so it is fast, but it does show the startup shell for a beat.
 * - **`allowRedirectInIframe: false`.** A sign-in redirect inside a frame is either a
 *   bug or a clickjacking attempt.
 * - **`piiLoggingEnabled: false`.** That channel carries account identifiers and token
 *   claims, which this application never logs.
 *
 * Two options a v4 configuration would have carried are gone in msal-browser 5:
 * `storeAuthStateInCookie` was removed outright, and `navigateToLoginRequestUrl` moved
 * onto `handleRedirectPromise` as a per-call option — see `AuthService.ready()`.
 *
 * @param config The validated runtime configuration.
 * @param baseUri The document base URI the relative redirect URIs resolve against.
 */
export function buildMsalConfig(config: AppConfig, baseUri: string): Configuration {
  return {
    auth: {
      clientId: config.auth.clientId,
      authority: config.auth.authority,
      // Absolute, because Entra matches the registered redirect URI verbatim and a
      // relative value would resolve against the current page rather than the app.
      //
      // Note what the `/` in the committed `config.json` values means: a root-relative
      // path resolves against the *origin*, not against `<base href>`. That is correct
      // at `<base href="/">` and wrong under a sub-path deployment, where these values
      // must be written without the leading slash so they resolve under the base.
      redirectUri: new URL(config.auth.redirectUri, baseUri).toString(),
      postLogoutRedirectUri: new URL(config.auth.postLogoutRedirectUri, baseUri).toString(),
    },
    cache: {
      cacheLocation: BrowserCacheLocation.SessionStorage,
    },
    system: {
      allowRedirectInIframe: false,
      loggerOptions: {
        loggerCallback: () => {
          // Silent. MSAL's own messages are diagnostic noise, and its PII channel is
          // off, so there is nothing here worth writing to a console a user may
          // screenshot into a support ticket.
        },
        logLevel: LogLevel.Error,
        piiLoggingEnabled: false,
      },
    },
  };
}
