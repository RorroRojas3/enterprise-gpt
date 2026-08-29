# Browser Telemetry

Reference for `core/telemetry/browser-telemetry.ts`: what it collects about a signed-in user's browser session, what it deliberately does not, how a browser event correlates back to the server-side trace [Request Logging and Application Insights](request-logging.md) documents, and the security posture that follows from a connection string this SDK cannot keep secret. Audience: engineers wiring a deployment's `config.json`, and anyone reading a browser telemetry item in Application Insights and wanting to know where it came from.

## 1. Why this exists

The server side of a request has been observable since [Request Logging and Application Insights](request-logging.md): every API call, every LLM turn, every tool run. None of that explains what happened *before* the request left the browser — a slow page load, a JavaScript exception the user saw and the API never heard about, or a network failure that meant no request was ever sent to correlate against.

`provideBrowserTelemetry()` closes that gap with the [Application Insights JavaScript SDK](https://learn.microsoft.com/azure/azure-monitor/app/javascript-sdk) (`@microsoft/applicationinsights-web` 3.4.3) — a **different SDK** from the OpenTelemetry distro the API uses, because [Microsoft Entra-authenticated ingestion explicitly does not support the JavaScript SDK](https://learn.microsoft.com/azure/azure-monitor/app/azure-ad-authentication#unsupported-scenarios); a browser has no way to hold the credential OpenTelemetry's exporter would need. That mismatch is also why this feature ships behind an optional `config.json` block rather than always-on: most deployments will run without it, and it must never be able to stop the application from starting.

## 2. The `config.json` shape

```json
{
  "telemetry": {
    "connectionString": "InstrumentationKey=...;IngestionEndpoint=https://....applicationinsights.azure.com/"
  }
}
```

- **Absent, and browser telemetry is off** — nothing is imported, nothing is constructed, nothing is sent. The committed dev `public/config.json` carries no `telemetry` key at all, and `assertAppConfig` treats the whole block as optional for exactly that reason: an older deployment's config must keep booting a newer bundle.
- **Present, and it is validated** the moment it exists — `telemetry.connectionString` must be a non-empty string, or `assertAppConfig` throws before the application bootstraps. A block written by hand and left half-finished fails loudly here instead of silently sending nothing.
- There is no per-flag switch beyond presence — no `telemetry.enabled: false`. Omit the block.

## 3. What is collected, and how it is started

[`provideBrowserTelemetry`](../../enterprise-gpt-ui/src/app/core/telemetry/browser-telemetry.ts) is a root-injector `provideAppInitializer`. When `config.telemetry` is present, it dynamically imports the SDK, constructs `ApplicationInsights`, and starts it — without awaiting the import, so a slow or blocked CDN can never hold up first paint. Everything before the chunk resolves (`config.json` itself, the icon sprite, MSAL's token exchange) is uninstrumented by construction; that is the price of keeping the SDK off the initial bundle, not a defect.

Once started:

| Collected | How |
|---|---|
| Page views | Raised by hand on every Angular `NavigationEnd`, plus one for the landing page the SDK itself never emits on load |
| Unhandled exceptions and promise rejections | The SDK's own global handlers (`enableUnhandledPromiseRejectionTracking: true`; uncaught exceptions are captured by default) |
| Ajax / `fetch` dependency calls | The SDK's default instrumentation, left on — this is what produces a browser-side `dependencies` row for every call to the API |
| The signed-in user's identity | A telemetry initializer stamps `ai.user.authUserId` from `AuthService.account.localAccountId` on every item, read per item because sign-in resolves asynchronously and a one-shot capture would race the account it means to read |

And explicitly not collected:

- **No cookies.** `disableCookiesUsage: true` — no `ai_user`/`ai_session`. Every signed-in item already carries the Entra object id (above), which groups by the identity that actually matters more usefully than a browser-generated session id would, without handing an enterprise deployment a cookie-consent decision it never made.
- **No SDK-driven route tracking.** `enableAutoRouteTracking` is **off** — see §4.
- **No query strings or URL fragments, anywhere.** See §5.

## 4. Why `enableAutoRouteTracking` is off

The SDK's built-in route-change hook fires on every history mutation, with no deduplication, and reports `location.href` **verbatim**. Both properties are disqualifying here:

- The sign-in path mutates browser history three times for one arrival — the router's own initial navigation, MSAL clearing the redirect hash, then the redirect onward — so the built-in hook would log three page views for one visit.
- At the moment those mutations happen, `location.href` still holds the MSAL authorization code and `client_info` in its fragment. A hook that reports the URL verbatim would ship an authorization code to Application Insights.

Instead, `provideBrowserTelemetry` subscribes to the Angular `Router`'s `NavigationEnd` events directly and calls `trackPageView` once per **completed** navigation, with the URL already scrubbed (§5). One completed navigation is one page view, and raising it by hand resets the SDK's operation id the same way its own hook would have.

## 5. URL scrubbing

A telemetry initializer strips everything from the first `?` or `#` onward — the query string and the fragment — from every field on every telemetry item that can carry a whole URL: `uri` and `refUri` (page views) and `data` (the request URL on an ajax/dependency item). Two things ride those fields in this application and neither may reach an Application Insights resource whose connection string is public (§7):

- The MSAL redirect fragment — the authorization code and `client_info` — on the page view raised right after the sign-in redirect lands.
- Search and filter terms typed into the conversation, user and report screens, which live in the query string.

The scrub runs on every item regardless of origin, not only on page views, because an ajax dependency item's `data` field is a full request URL and a conversation search hits the API with its query terms on it.

## 6. Correlation to the server trace

A browser event and the server request it triggered are not automatically the same "thing" in Application Insights — three independent choices in this feature are what stitch them together:

1. **Distributed trace correlation.** `enableCorsCorrelation: true`, restricted to the API's own host via an **anchored** `correlationHeaderDomains` (`^host$` — the SDK compiles each entry to an unanchored regular expression otherwise, and a bare host also matches `<host>.attacker.example`). This is what makes the SDK attach the W3C trace-context headers to a `fetch`/XHR call toward the API, so the browser's own dependency item and the server's `requests` row for that same call share one `operation_Id` — the same `union … | where operation_Id == "…"` query from the [KQL cookbook](kql-cookbook.md#correlating-one-request-end-to-end-by-traceid) finds both sides. The correlation headers are not sent to Entra or any other cross-origin call — only to the API host, deliberately, so an unexpected trace-context header cannot turn a sign-in failure into something that looks like a telemetry defect.
2. **The `Request-Context` response header.** The API's CORS policy exposes it, not merely allows it (`Program.cs`), because the SDK reads that response header to resolve which server the ajax call actually reached — without it, the dependency call still correlates by trace id, but the SDK cannot label the target and silently drops that part of the picture.
3. **A shared user identity.** The initializer's `ai.user.authUserId` (§3) is MSAL's `localAccountId` — the same Entra object id the API's `EndUserEnrichingProcessor` stamps as `enduser.id` on server spans, surfaced as `user_AuthenticatedId` on the `requests` row (see [request-logging.md §7.1](request-logging.md#71-registration)). A query that joins browser and server telemetry by this field, rather than by a single request's `operation_Id`, is how you find everything one *person* did across a session, not just one call.

## 7. Security: this connection string is public, and that has to shape where it points

**`config.json` is fetched over an unauthenticated request before the application signs anyone in**, so `telemetry.connectionString` is exactly as public as viewing page source. This is not a gap in this implementation — [Microsoft's own guidance says the same thing about every deployment of the JavaScript SDK](https://learn.microsoft.com/azure/azure-monitor/app/javascript-sdk#get-started): "this connection string is visible in plain text in client browsers, and there is no straightforward way to use Microsoft Entra ID-based authentication for browser telemetry." The JavaScript SDK is explicitly one of the [unsupported scenarios for Entra-authenticated ingestion](https://learn.microsoft.com/azure/azure-monitor/app/azure-ad-authentication#unsupported-scenarios) — there is no configuration that fixes this, only a choice about which resource absorbs the exposure.

**That is why this connection string must name a separate Application Insights resource from the API's, with local (key-based) authentication left enabled on it — never the same resource the API's `AzureMonitor:ConnectionString` points at.** [Microsoft's own recommendation](https://learn.microsoft.com/azure/azure-monitor/app/application-insights-faq#should-i-use-a-separate-application-insights-resource-for-browser-telemetry-if-i-use-authenticated-server-side-ingestion-or-otlp) is exactly this, as risk isolation: anyone who copies the connection string out of a browser's network tab can send arbitrary telemetry to whatever resource it names, and the blast radius of that has to be a resource that exists only to receive it — never the one carrying the server's own request/dependency/exception data used for real diagnosis and alerting. Concretely, sharing the API's resource would mean:

- A malicious or merely careless caller can pollute the *server's* diagnostic data — the same `requests`/`dependencies`/`exceptions` tables an operator queries to find a real production incident.
- The API's resource cannot enable Entra-authenticated ingestion (a hardening step worth taking on the server side independently) without silently breaking browser telemetry, since the JavaScript SDK cannot authenticate that way at all.
- Rotating the browser connection string — the only real remedy for abuse — would also rotate the server's, disrupting telemetry neither side asked to lose.

End-to-end correlation (§6) still works across two separate resources — `operation_Id` and the shared user identity are properties of the telemetry items themselves, not of which resource ingested them — so this separation costs nothing on the query side and only requires provisioning a second, disposable resource. Provisioning that resource, and populating a deployment's `config.json` with its connection string, is outside this repository's scope — see §9.

## 8. Bundle posture and the build gate

`@microsoft/applicationinsights-web` sits behind `await import()` in `TELEMETRY_LOADER`, never a static import — the SDK is a **195.46 kB raw lazy chunk** of its own, far more than the initial bundle's remaining headroom (the production build fails above 720 kB and was at 671.52 kB raw before this feature — see `.claude/CLAUDE.md`'s bundle-budget note for the current figure), and most deployments configure no `telemetry` block at all. The two-attribute `provideBrowserTelemetry()` wiring that *is* on the initial graph (the token, the app initializer, the `NavigationEnd` subscription and the optional `telemetry` field on `AppConfig`) added +1.38 kB there — the cost of the on/off switch itself, not of the SDK. `scripts/check-initial-chunk.mjs` fails the build if the SDK ever becomes reachable from a static import chain, the same gate that already protects `ngx-markdown`, `mermaid` and `katex` — and, uniquely among those, also **fails the build if the SDK's lazy chunk goes missing entirely**, since every other check in that script only catches a dependency that leaked onto the wrong graph, not one that quietly stopped being built at all.

## 9. What this does not do

- **No provisioning.** This repository has no Terraform and no other infrastructure-as-code; supplying a real `telemetry.connectionString` — for a resource that must already exist, per §7 — in a deployed environment's `config.json` is outside this repository, tracked by the infrastructure PRD (`docs/prd/azure-infrastructure/`).
- **No offline queueing.** A network failure while the SDK is loading, or while it is sending, simply loses that telemetry. There is no retry-on-reconnect behavior configured.
- **No click tracking, no session replay, no Click Analytics plug-in.** Only page views, dependency calls, and unhandled errors — the plug-ins that would add richer interaction telemetry are not installed.

## 10. Key files

| Concern | File |
|---|---|
| Provider, loader token, scrubbing, correlation config | [`src/app/core/telemetry/browser-telemetry.ts`](../../enterprise-gpt-ui/src/app/core/telemetry/browser-telemetry.ts) |
| Tests | [`src/app/core/telemetry/browser-telemetry.spec.ts`](../../enterprise-gpt-ui/src/app/core/telemetry/browser-telemetry.spec.ts) |
| `telemetry` config shape and validation | [`src/app/core/config/app-config.ts`](../../enterprise-gpt-ui/src/app/core/config/app-config.ts) |
| Wiring into bootstrap | [`src/app/app.config.ts`](../../enterprise-gpt-ui/src/app/app.config.ts) |
| The bundle gate | [`scripts/check-initial-chunk.mjs`](../../enterprise-gpt-ui/scripts/check-initial-chunk.mjs) |
| The server-side counterpart | [Request Logging and Application Insights](request-logging.md), specifically §7.1 (`EndUserEnrichingProcessor`) and §7.2 (correlation) |
| Querying both sides together | [Application Insights KQL Cookbook](kql-cookbook.md) |
