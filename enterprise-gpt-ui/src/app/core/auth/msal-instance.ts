import { InjectionToken } from '@angular/core';
import {
  BrowserCacheLocation,
  Configuration,
  IPublicClientApplication,
  LogLevel,
} from '@azure/msal-browser';
import { AppConfig } from '../config/app-config';

/**
 * Ours, not `@azure/msal-angular`'s: its `canMatch` guard cannot redirect, `MsalService` replays a
 * consumed redirect hash, and `MsalBroadcastService` relies on `NgZone.run`, a no-op when zoneless.
 */
export const MSAL_INSTANCE = new InjectionToken<IPublicClientApplication>('MSAL_INSTANCE');

/**
 * Only the Entra values come from {@link AppConfig}; the security posture is fixed here so a
 * deployment cannot weaken it. `sessionStorage` keeps tokens from outliving the tab on a shared
 * machine, at the cost of a second tab re-establishing its session through Entra.
 */
export function buildMsalConfig(config: AppConfig, baseUri: string): Configuration {
  return {
    auth: {
      clientId: config.auth.clientId,
      authority: config.auth.authority,
      // Absolute, because Entra matches the registered URI verbatim. A leading `/` in
      // `config.json` resolves against the origin, so a sub-path deployment must omit it.
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
          // Silent: MSAL's messages are noise in a console a user may screenshot.
        },
        logLevel: LogLevel.Error,
        piiLoggingEnabled: false,
      },
    },
  };
}
