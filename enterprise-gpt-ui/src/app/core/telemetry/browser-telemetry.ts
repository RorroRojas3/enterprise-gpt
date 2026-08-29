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
 * The dynamic import itself, behind a token so a spec can swap it.
 *
 * The literal `import()` has to stay in a real module for esbuild to split a chunk for it,
 * and `scripts/check-initial-chunk.mjs` fails the build if the SDK ever becomes statically
 * reachable: it is an order of magnitude more than the initial graph's headroom, and most
 * deployments configure no `telemetry` block at all.
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
 * Starts browser telemetry when `config.json` carries a `telemetry` block.
 *
 * Provided in the root injector rather than under the signed-in shell, because the failures
 * worth seeing are the ones that never reach it: a guard or handshake terminating on
 * `/login-failed` or `/session-error` renders no shell and reaches no server, so nothing
 * else in this system can observe it.
 *
 * Everything before the chunk resolves is uninstrumented by construction — `config.json`,
 * the icon sprite and MSAL's token exchange all complete before the SDK patches `fetch`.
 * That is the price of keeping it off the initial graph, not a defect to chase.
 */
export function provideBrowserTelemetry(): EnvironmentProviders {
  return provideAppInitializer(() => {
    const config = inject(APP_CONFIG);

    // Ahead of every other injection, so a deployment with no telemetry block constructs
    // nothing on this provider's account.
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

    // Deliberately not awaited. An app initializer that returns this promise holds the first
    // paint on a CDN fetch, and telemetry must never be able to keep the application from
    // starting.
    void load(buildConfig(config.telemetry, config.apiBaseUrl))
      .then((insights) => {
        // The load can outlive the injector — a rejected bootstrap, or a spec resetting
        // TestBed. The SDK patches fetch, XHR, history and the global error handlers, so an
        // instance nobody owns keeps them patched and a second bootstrap doubles them.
        if (!live) {
          insights.unload();

          return;
        }

        destroyRef.onDestroy(() => insights.unload());

        insights.addTelemetryInitializer((item) => {
          // Read per item rather than through setAuthenticatedUserContext: sign-in resolves
          // asynchronously and sign-out is a redirect, so a one-shot call races the account
          // it is meant to read.
          const objectId = auth.account?.localAccountId;

          if (objectId) {
            item.tags ??= {};
            item.tags[AUTHENTICATED_USER_TAG] = objectId;
          }

          scrubUrls(item);
        });

        // The SDK emits no page view of its own on load, and the chunk resolves after the
        // first navigation has already happened — so the landing page is only counted here.
        insights.trackPageView({ uri: currentPage() });
        navigations.subscribe(() => insights.trackPageView({ uri: currentPage() }));
      })
      .catch((cause: unknown) => console.error('Browser telemetry could not start.', cause));
  });
}

function buildConfig(telemetry: TelemetryConfig, apiBaseUrl: string): IConfiguration & IConfig {
  return {
    connectionString: telemetry.connectionString,
    // Off, with page views raised from NavigationEnd instead. The SDK's hook fires once per
    // history mutation with no dedupe, and one arrival on the sign-in path mutates history
    // three times — the router's initial navigation, MSAL clearing the hash, then the
    // redirect onward. It also reports location.href verbatim, which at that moment still
    // carries the authorization code in its fragment. One completed navigation is one page
    // view, its URL is this application's to scrub, and raising it by hand resets the
    // operation id exactly as the SDK's own hook would.
    enableAutoRouteTracking: false,
    enableUnhandledPromiseRejectionTracking: true,
    // No ai_user/ai_session cookies. Session grouping is worth less here than not handing an
    // enterprise deployment a consent decision it did not make: every signed-in item already
    // carries the Entra object id, which groups by the identity that matters.
    disableCookiesUsage: true,
    // Correlation headers on cross-origin calls, restricted to the API. An allow-list rather
    // than the blanket switch: the same fetch layer talks to Entra, and an unexpected
    // Request-Id on a token request is a sign-in failure nobody would connect to telemetry.
    // The API accepts them because its CORS policy allows any header.
    //
    // Anchored, because the SDK compiles each entry to an unanchored regular expression, and
    // a bare host also matches `<host>.attacker.example`.
    enableCorsCorrelation: true,
    correlationHeaderDomains: [`^${new URL(apiBaseUrl).host}$`],
  };
}

/**
 * Strips the query string and fragment from every URL an item carries.
 *
 * Two things ride those here: the MSAL redirect fragment, which holds the authorization code
 * and `client_info`, and the search and filter terms the conversation, user and report
 * screens keep in the query string. The ingestion resource is named by a connection string
 * that ships in `config.json`, so neither may reach it.
 */
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
