import { DOCUMENT } from '@angular/common';
import {
  DestroyRef,
  EnvironmentProviders,
  InjectionToken,
  inject,
  provideAppInitializer,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { AuthService } from '@core/auth/auth-service';
import { TelemetryConfig } from '@core/config/app-config';
import { APP_CONFIG } from '@core/config/app-config.token';
import type { IConfig, IConfiguration, ITelemetryItem } from '@microsoft/applicationinsights-web';
import { filter } from 'rxjs';

/** The Application Insights tag that surfaces as `user_AuthenticatedId`. */
const AUTHENTICATED_USER_TAG = 'ai.user.authUserId';

/** Telemetry fields that carry a whole URL, and therefore a query string and a fragment. */
const URL_FIELDS = ['uri', 'refUri', 'data'] as const;

/** The slice of the Application Insights API this application uses. */
export interface BrowserTelemetry {
  addTelemetryInitializer(initializer: (item: ITelemetryItem) => boolean | void): unknown;
  trackPageView(pageView: { uri: string }): void;
  unload(isAsync?: boolean): void;
}

/** Loads, constructs and starts the SDK. */
export type TelemetryLoader = (config: IConfiguration & IConfig) => Promise<BrowserTelemetry>;

/**
 * A token so specs can swap it. The SDK must stay a lazy chunk: `check-initial-chunk.mjs` fails
 * the build if it becomes statically reachable.
 */
export const TELEMETRY_LOADER = new InjectionToken<TelemetryLoader>('TELEMETRY_LOADER', {
  providedIn: 'root',
  factory: () => async (config) => {
    const { ApplicationInsights } = await import('@microsoft/applicationinsights-web');
    const insights = new ApplicationInsights({ config });
    insights.loadAppInsights();

    return insights;
  },
});

/**
 * Root-provided, not under the shell, so failures ending on `/login-failed` or `/session-error`
 * are still seen. Requests made before the chunk loads (config, sprite, MSAL) go uninstrumented.
 */
export function provideBrowserTelemetry(): EnvironmentProviders {
  return provideAppInitializer(() => {
    const config = inject(APP_CONFIG);

    // Before any other injection, so a deployment without telemetry constructs nothing.
    if (!config.telemetry) {
      return;
    }

    const load = inject(TELEMETRY_LOADER);
    const auth = inject(AuthService);
    const destroyRef = inject(DestroyRef);
    const document = inject(DOCUMENT);
    const navigations = inject(Router).events.pipe(
      filter((event) => event instanceof NavigationEnd),
      takeUntilDestroyed(destroyRef),
    );

    let live = true;
    destroyRef.onDestroy(() => (live = false));

    const currentPage = (): string => withoutQueryOrFragment(document.location.href);

    // Not awaited: returning it would hold the first paint on a CDN fetch.
    void load(buildConfig(config.telemetry, config.apiBaseUrl))
      .then((insights) => {
        // The load can outlive the injector; an unowned SDK keeps fetch, XHR and history
        // patched, and a second bootstrap doubles them.
        if (!live) {
          insights.unload();

          return;
        }

        destroyRef.onDestroy(() => insights.unload());

        insights.addTelemetryInitializer((item) => {
          // Per item, not setAuthenticatedUserContext: a one-shot call races async sign-in.
          const objectId = auth.account?.localAccountId;

          if (objectId) {
            item.tags ??= {};
            item.tags[AUTHENTICATED_USER_TAG] = objectId;
          }

          scrubUrls(item);
        });

        // The SDK emits no initial page view, and the first NavigationEnd predates the chunk.
        insights.trackPageView({ uri: currentPage() });
        navigations.subscribe(() => insights.trackPageView({ uri: currentPage() }));
      })
      .catch((cause: unknown) => console.error('Browser telemetry could not start.', cause));
  });
}

function buildConfig(telemetry: TelemetryConfig, apiBaseUrl: string): IConfiguration & IConfig {
  return {
    connectionString: telemetry.connectionString,
    // Off: the SDK hook fires per history mutation (three on one sign-in arrival) and reports
    // the raw URL, authorization-code fragment included.
    enableAutoRouteTracking: false,
    enableUnhandledPromiseRejectionTracking: true,
    // No cookies, so no consent decision: the Entra object id already groups signed-in items.
    disableCookiesUsage: true,
    // Allow-listed to the API: an unexpected Request-Id on an Entra token request breaks sign-in.
    // Anchored, because the SDK compiles each entry to an unanchored regex.
    enableCorsCorrelation: true,
    correlationHeaderDomains: [`^${new URL(apiBaseUrl).host}$`],
  };
}

/** The fragment holds the MSAL authorization code; the query holds users' search terms. */
function scrubUrls(item: ITelemetryItem): void {
  const data: Record<string, unknown> | undefined = item.baseData;

  if (!data) {
    return;
  }

  for (const field of URL_FIELDS) {
    const value = data[field];

    if (typeof value === 'string') {
      data[field] = withoutQueryOrFragment(value);
    }
  }
}

/** Everything up to the first `?` or `#`. Never throws, and never drops a relative path. */
function withoutQueryOrFragment(value: string): string {
  const cut = value.search(/[?#]/);

  return cut === -1 ? value : value.slice(0, cut);
}
