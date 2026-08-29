# Frontend Foundation

End-to-end reference for the foundation layer of the rebuilt Angular client at `enterprise-gpt-ui/`: how the app learns which environment it is in, how every failure becomes one typed value, and the five composable store features every screen's state is assembled from. Audience: a developer about to write the first feature store or screen, who needs to know which wheels already exist and which of their edges are sharp.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), which is the authority for every `US-xxx` reference below, and to [Conversation Streaming Contract](../conversations/streaming-contract.md), which the chat transport (EP-4) is written against.

## 1. Overview

> **Scope, stated plainly.** This is foundation only — the substrate the feature stories build on, all of it covered by specs that run without a backend. Sign-in, the session, permission gating, the guarded route tree and sign-out arrived afterwards in EP-2 and have their own reference in [Authentication and Session](authentication-and-session.md); the signed-in shell, the conversation sidebar and the chat route followed in EP-3, in [Shell and Navigation](shell-and-navigation.md). Several sections below hand off to both where they meet.

Three pieces landed here, each solving a problem the deleted `enterprise-ui/` client had. The presentation half of EP-1 — design tokens, theming, self-hosted assets and the shared component kit — has its own reference in [Design System](design-system.md).

1. **Runtime configuration** (US-102). Nothing environment-specific is compiled into the bundle. One artifact is built once and promoted; `config.json` is replaced per environment. A production bundle can no longer ship pointed at `localhost`, and a deployment that forgot to write `config.json` says so on screen instead of rendering a blank page (§3).
2. **Error normalization** (US-103). The API answers every failure with RFC 9457 Problem Details across ten domain types. The client mirrors all ten verbatim and collapses every observable failure — HTTP, transport, abort, and stray throws — into one discriminated `AppError`, so a store, a form, and a toast all consume the same shape (§4).
3. **The reusable store features** (US-104). Five `@ngrx/signals` features and one event group, written before any store exists, because all sixteen planned stores compose them. This is the part most worth reading before writing a store: several of the constraints they encode are invisible from the signatures (§5).

### 1.1 Where each piece lives

| Concern                                                        | Where                                                                                                                                                                                                                                                                                                                           |
| -------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Config fetch, validation, freeze                               | [`core/config/load-app-config.ts`](../../enterprise-gpt-ui/src/app/core/config/load-app-config.ts), [`core/config/app-config.ts`](../../enterprise-gpt-ui/src/app/core/config/app-config.ts)                                                                                                                                    |
| Providing config before bootstrap                              | [`src/main.ts`](../../enterprise-gpt-ui/src/main.ts), [`core/config/app-config.token.ts`](../../enterprise-gpt-ui/src/app/core/config/app-config.token.ts)                                                                                                                                                                      |
| Startup and fatal shell                                        | [`src/index.html`](../../enterprise-gpt-ui/src/index.html), [`core/config/fatal-shell.ts`](../../enterprise-gpt-ui/src/app/core/config/fatal-shell.ts)                                                                                                                                                                          |
| Problem type URIs                                              | [`core/errors/problem-types.ts`](../../enterprise-gpt-ui/src/app/core/errors/problem-types.ts)                                                                                                                                                                                                                                  |
| The `AppError` union                                           | [`core/errors/app-error.ts`](../../enterprise-gpt-ui/src/app/core/errors/app-error.ts)                                                                                                                                                                                                                                          |
| Normalization entry points                                     | [`to-app-error.ts`](../../enterprise-gpt-ui/src/app/core/errors/to-app-error.ts), [`to-app-error-from-response.ts`](../../enterprise-gpt-ui/src/app/core/errors/to-app-error-from-response.ts), [`build-app-error.ts`](../../enterprise-gpt-ui/src/app/core/errors/build-app-error.ts)                                          |
| Server errors onto a form                                      | [`core/errors/apply-server-errors.ts`](../../enterprise-gpt-ui/src/app/core/errors/apply-server-errors.ts), [`camel-case.ts`](../../enterprise-gpt-ui/src/app/core/errors/camel-case.ts)                                                                                                                                        |
| Retry and refresh policy                                       | [`core/errors/retry-policy.ts`](../../enterprise-gpt-ui/src/app/core/errors/retry-policy.ts), [`core/http/interceptors/retry.interceptor.ts`](../../enterprise-gpt-ui/src/app/core/http/interceptors/retry.interceptor.ts)                                                                                                      |
| User-facing error text                                         | [`core/errors/error-message.ts`](../../enterprise-gpt-ui/src/app/core/errors/error-message.ts)                                                                                                                                                                                                                                  |
| The five store features                                        | [`core/state/`](../../enterprise-gpt-ui/src/app/core/state/)                                                                                                                                                                                                                                                                    |
| Cross-store events, and the sign-out abort primitive           | [`core/events/session-events.ts`](../../enterprise-gpt-ui/src/app/core/events/session-events.ts)                                                                                                                                                                                                                                |
| API URL building and ownership                                 | [`core/http/api-url.ts`](../../enterprise-gpt-ui/src/app/core/http/api-url.ts)                                                                                                                                                                                                                                                  |
| The one permitted `localStorage` call site                     | [`core/storage/local-preferences.ts`](../../enterprise-gpt-ui/src/app/core/storage/local-preferences.ts)                                                                                                                                                                                                                        |
| Replacing the document, as opposed to routing                  | [`core/navigation/hard-navigation.ts`](../../enterprise-gpt-ui/src/app/core/navigation/hard-navigation.ts)                                                                                                                                                                                                                      |
| Sign-in, tokens, guards, session, sign-out (EP-2)              | [Authentication and Session](authentication-and-session.md), [`core/auth/`](../../enterprise-gpt-ui/src/app/core/auth/), [`core/session/`](../../enterprise-gpt-ui/src/app/core/session/), [`core/catalog/`](../../enterprise-gpt-ui/src/app/core/catalog/), [`core/dnd/`](../../enterprise-gpt-ui/src/app/core/dnd/)           |
| The shell, the conversation sidebar, the chat route (EP-3)     | [Shell and Navigation](shell-and-navigation.md), [`core/conversations/`](../../enterprise-gpt-ui/src/app/core/conversations/), [`core/ui/`](../../enterprise-gpt-ui/src/app/core/ui/), [`features/shell/`](../../enterprise-gpt-ui/src/app/features/shell/), [`features/chat/`](../../enterprise-gpt-ui/src/app/features/chat/) |
| Sanitized markdown, the streaming split, scroll pinning (EP-6) | [Answer Rendering](answer-rendering.md), [`features/chat/markdown/`](../../enterprise-gpt-ui/src/app/features/chat/markdown/), [`domain/markdown/`](../../enterprise-gpt-ui/src/app/domain/markdown/), [`features/chat/transcript-pinning.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript-pinning.ts)             |
| The vendored streaming contract (US-107)                       | [`domain/stream/andes/assistant-ui.contract.ts`](../../enterprise-gpt-ui/src/app/domain/stream/andes/assistant-ui.contract.ts) — copied byte-for-byte from `Andes.Extensions.AI.UI` and **never hand-edited**; see [the README](../../enterprise-gpt-ui/README.md#vendored-contract)                                            |
| Tokens, theming, assets, the shared UI kit (US-105/106/109)    | [Design System](design-system.md), [`src/styles/`](../../enterprise-gpt-ui/src/styles/), [`src/app/shared/`](../../enterprise-gpt-ui/src/app/shared/)                                                                                                                                                                           |
| Asset builds and drift checks                                  | [`enterprise-gpt-ui/scripts/`](../../enterprise-gpt-ui/scripts/) — `copy-fonts`, `build-icon-sprite`, `build-brand-images`, `check-andes-contract`, `check-tokens`, `check-icon-names`, `check-forbidden-apis`, `check-initial-chunk`                                                                                           |

### 1.2 Workspace facts, once

| Fact             | Value                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| ---------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Angular          | **21.2.19**, standalone, **zoneless** — `provideZonelessChangeDetection()` is stated in `app.config.ts` even though it is v21's default, so the change-detection model is readable rather than inferred from the absence of `zone.js`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| Change detection | `OnPush` is the schematic default in `angular.json`; nothing re-renders without a signal write                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| State            | `@ngrx/signals` **21.1.1**, pinned exact (no caret) to match the `ngrx-signal-store` skill's doc snapshot. `@ngrx/operators` 21.1.1 alongside it                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| Tests            | **Vitest** through `@angular/build:unit-test`, `watch: false`, `providersFile: src/testing/test-providers.ts` — which installs `provideCheckNoChangesConfig({ exhaustive: true })`, so a view reading mutated plain-object state fails in CI rather than in someone's browser                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| Layering         | `features → shared → core → domain`, one way only. `domain/` imports nothing from Angular, so the SSE codec and the activity fold will test in Node with no `TestBed`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| Path aliases     | `@core/*`, `@domain/*`, `@shared/*`, `@features/*`, `@testing/*`. No barrel `index.ts` files; components are `foo.ts`/`foo.html`/`foo.scss` with no `.component` suffix                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| TypeScript       | Strict, plus `strictTemplates`, `noPropertyAccessFromIndexSignature`, and `noUncheckedIndexedAccess`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| Initial bundle   | **670.33 kB raw** after P7, against a **675 kB warn / 720 kB error** budget. It was 349.83 kB / 88.13 kB at the end of EP-1; MSAL added 248.9 kB and is unavoidably initial ([Authentication and Session §12.2](authentication-and-session.md#122-bundle-re-baseline)), while EP-3 added only 6.52 kB and the markdown renderer added none of its 124.8 kB, because the shell and every route under it are lazy ([Shell and Navigation §9](shell-and-navigation.md#9-bundle-and-budgets), [Answer Rendering §6](answer-rendering.md#6-the-bundle-gate-resolved)). Only the **warning** line has ever moved, most recently by US-1405, which re-baselined both after the previous pair left 0.07 kB of headroom; the 720 kB ceiling has not. A separate `styles` budget caps the global stylesheet at **66 kB warn / 80 kB error**; it currently compiles to 64.47 kB |
| Lint             | **ESLint 10** flat config (`eslint.config.mjs`) over typescript-eslint 8.66, angular-eslint 21.4 (`tsRecommended`, `templateRecommended`, `templateAccessibility`) and `@ngrx/eslint-plugin` 21.1.1 at **`signalsTypeChecked`** — which needs `projectService: true`, because the one rule the plain `signals` preset omits throws without type information. Its four NgRx rules are the ones §5 was written against: no arrays at the root of `withState`, writes only through `patchState`, no `store.method()` inside a `computed`, and the `with*` naming convention. `no-restricted-syntax` bans `bypassSecurityTrustHtml`; `@typescript-eslint/no-unused-vars` is **off**, because tsc's `noUnusedLocals`/`noUnusedParameters` own that and, unlike the rule, count a `{@link}` as a use     |

**Installed but not initial.** `ngx-markdown` 21.3.0 (over `marked` 18 and `prismjs` 1.30) and `dompurify` 3.4.13 are imported as of US-601 and are **124.8 kB of the lazy `chat` chunk**, not of the initial graph; `check-initial-chunk.mjs` fails the build if a static import from `main.ts` ever reaches them ([Answer Rendering §6](answer-rendering.md#6-the-bundle-gate-resolved)). **Installed and not in either graph:** `mermaid` 11 and `katex` 0.16 are US-605's, reached only through `await import(…)` behind injection tokens and behind `config.json` flags — so the same check now fails the build if either is statically reachable from `main.ts` _or_ from the chat chunk, and fails it again if either disappears from the build entirely ([Answer Rendering §8.7](answer-rendering.md#87-what-the-build-gate-now-checks-in-both-directions)). **Installed and permanently unimported:** `@azure/msal-angular` 6.0.3 — EP-2 uses `@azure/msal-browser` 5.18.0 directly and rejected the Angular wrapper for three verified reasons ([Authentication and Session §3](authentication-and-session.md#3-azuremsal-angular-is-installed-and-unused)); it stays installed only because US-101's acceptance criteria require it. `bootstrap-icons` 1.13.1 has left this list without entering the bundle: `scripts/build-icon-sprite.mjs` reads its individual SVGs at build time and emits a sprite into `public/`, so the package is a build-time input rather than a dependency the browser ever sees.

## 2. Quick start — a store that composes the foundation

Illustrative — the sketch below shows the composition order §5.7 requires and the two cancellation concerns §5.5 explains.

> **The real reference now exists.** US-302's [`ConversationListStore`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-list-store.ts) is the collection store the remaining fifteen copy, and it is documented decision by decision in [Shell and Navigation §4](shell-and-navigation.md#4-conversationliststore--the-reference-list-store). Copy it rather than this sketch, which predates it: it is the one that exercises pagination, a declaratively wired load, and the two cancellation rules a paged list needs. `ToastStore`, `SessionStore` and the two catalog stores are app-wide singletons over short lists and exercise none of that; `ConversationStore` (`features/chat/`) is the first genuinely route-scoped store.

```ts
import { computed, inject } from "@angular/core";
import {
  patchState,
  signalStore,
  withFeature,
  withMethods,
} from "@ngrx/signals";
import { setAllEntities, withEntities } from "@ngrx/signals/entities";
import { rxMethod } from "@ngrx/signals/rxjs-interop";
import { mapResponse } from "@ngrx/operators";
import { pipe, switchMap, takeUntil, tap } from "rxjs";
import { toAppError } from "@core/errors/to-app-error";
import { injectSignedOut } from "@core/events/session-events";
import { setQuery, withClientQuery } from "@core/state/with-client-query";
import {
  setFirstPage,
  withOffsetPagination,
} from "@core/state/with-offset-pagination";
import { withPendingIds } from "@core/state/with-pending-ids";
import {
  setError,
  setFulfilled,
  setPending,
  withRequestStatus,
} from "@core/state/with-request-status";
import { withResetOnSignOut } from "@core/state/with-reset-on-sign-out";

export const ProjectStore = signalStore(
  withEntities<Project>(),
  withRequestStatus(),
  withOffsetPagination(),
  withPendingIds(),
  // withFeature, because withClientQuery binds to a signal the store above it owns.
  withFeature(({ entities, isFulfilled, isFullyLoaded }) =>
    withClientQuery(entities, {
      searchableText: (project) => project.name,
      comparators: { name: (a, b) => a.name.localeCompare(b.name) },
      // Both flags: isFullyLoaded alone reads true before the first fetch (§5.2).
      isAuthoritative: computed(() => isFulfilled() && isFullyLoaded()),
      incompleteSetReason: "Only the first 500 projects have loaded.",
    }),
  ),
  withMethods(
    (store, api = inject(ProjectApi), signedOut$ = injectSignedOut()) => ({
      search: rxMethod<string>(
        pipe(
          tap((query) => patchState(store, setQuery(query), setPending())),
          switchMap(() =>
            api.search({ skip: 0, take: store.take() }).pipe(
              // Clearing state is not cancelling work (§5.5).
              takeUntil(signedOut$),
              mapResponse({
                next: (page) =>
                  patchState(
                    store,
                    setAllEntities(page.items),
                    setFirstPage(page),
                    setFulfilled(),
                  ),
                error: (error: unknown) =>
                  patchState(store, setError(toAppError(error))),
              }),
            ),
          ),
        ),
      ),
    }),
  ),
  // Last. Always. §5.5 explains what happens otherwise.
  withResetOnSignOut(),
);
```

`protectedState` stays on (the default), so nothing outside the store can `patchState` it, and every write goes through a standalone updater.

## 3. Runtime configuration

### 3.1 Why there is no `environments/` directory

The build produces **one** artifact. It is promoted from dev to test to production unchanged, and the only thing that differs per environment is a small JSON file sitting beside `index.html`. There are no `fileReplacements` in `angular.json` and no `environment.ts`.

The failure this prevents is specific and familiar: a build-time replacement bakes an API URL into a hashed chunk, someone builds the wrong configuration, and the artifact that reaches production is indistinguishable from the correct one until a user's browser calls `localhost`. Moving the value to a fetched file makes that a deployment-time mistake with a deployment-time fix, and one the app can _detect_ (§3.4).

### 3.2 The shape

```json
{
  "apiBaseUrl": "https://localhost:7045",
  "auth": {
    "clientId": "b40defa0-5309-45c4-82fc-cb284010cc10",
    "authority": "https://login.microsoftonline.com/{tenantId}",
    "redirectUri": "/auth",
    "postLogoutRedirectUri": "/signed-out",
    "apiScopes": ["api://{apiClientId}/access_as_user"]
  },
  "features": { "diagrams": false, "math": false, "rawStreamCodec": false }
}
```

| Field                        | Rule                                 | Notes                                                                                                                                                                                                                                                          |
| ---------------------------- | ------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `apiBaseUrl`                 | absolute `http:`/`https:` URL        | Trailing slashes are stripped on load, so `ApiUrl.build()` cannot emit a double slash                                                                                                                                                                          |
| `auth.clientId`              | non-empty string **and a GUID**      | Checked here rather than at sign-in, where Entra reports it as an opaque "application not found" long after the cause scrolled away                                                                                                                            |
| `auth.authority`             | absolute **`https:`** URL            | The tenant id is part of it                                                                                                                                                                                                                                    |
| `auth.redirectUri`           | non-empty string                     | Relative to the app origin                                                                                                                                                                                                                                     |
| `auth.postLogoutRedirectUri` | non-empty string                     | Relative to the app origin. Since US-205 it points at `/signed-out`, and must be registered as a post-logout redirect URI in the Entra app registration ([Authentication and Session §11.3](authentication-and-session.md#113-what-the-failure-path-lands-on)) |
| `auth.apiScopes`             | non-empty array of non-empty strings | The API access token's scopes                                                                                                                                                                                                                                  |
| `features.diagrams`          | boolean                              | Load the diagram renderer chunk                                                                                                                                                                                                                                |
| `features.math`              | boolean                              | Load the math renderer chunk                                                                                                                                                                                                                                   |
| `features.rawStreamCodec`    | boolean                              | Read the chat stream as unframed text — the fallback for a deployment still running a server that predates the framed contract                                                                                                                                 |

Two deliberate choices in that table. The booleans are **rejected when they arrive as the string `"false"`**, which is exactly what environment-variable substitution produces when a template forgets to strip the quotes — the case where a flag silently reads truthy forever. And **unknown extra keys are accepted**, so a `config.json` written for a newer bundle still boots an older one during a rollback.

MSAL's `cacheLocation`, `allowRedirectInIframe` and logger options are deliberately _not_ here: they are a security posture rather than an environment value, so they are fixed in `buildMsalConfig` where no deployment can weaken them by editing one file ([Authentication and Session §12.1](authentication-and-session.md#121-posture)). `storeAuthStateInCookie` is not here either, and could not be — msal-browser 5 removed it from the package.

### 3.3 The startup sequence

```text
index.html paints the startup shell            ← visible immediately, before any script runs
  └─ main.ts
       ├─ loadAppConfig()
       │    ├─ fetch(new URL('config.json', document.baseURI), no-store, credentials omit, 10s timeout)
       │    ├─ response.ok?          → else AppConfigError naming the status
       │    ├─ response.json()       → else AppConfigError "not valid JSON"
       │    ├─ assertAppConfig(body) → else AppConfigError naming the offending field
       │    └─ normalizeAppConfig    → frozen, trailing slash stripped
       ├─ createStandardPublicClientApplication(buildMsalConfig(config, document.baseURI))   ← US-201
       └─ bootstrapApplication(App, { providers: [APP_CONFIG, MSAL_INSTANCE] })
```

The startup shell is **not** removed here. Since US-201 that is `provideStartupShellHandoff()`'s job, on the first settled navigation, because bootstrap now resolves while the guards are still deciding — see [Authentication and Session §8](authentication-and-session.md#8-startup-paint).

Four details of that flow are load-bearing:

- **`cache: 'no-store'`.** A cached `config.json` is how one environment ends up pointed at another after a promotion.
- **Resolved against `document.baseURI`**, not `/config.json` and not `./config.json`. The first breaks a sub-path deployment; the second breaks a hard reload on a deep route such as `/chat/{id}`, where the relative resolution would look for `/chat/config.json`.
- **`response.ok` is checked before the body is read.** A host with SPA fallback routing answers a missing `config.json` with `index.html` and a `200`, so trusting the status alone would produce a JSON parse error with a misleading message; trusting the body alone would miss the 404.
- **The value is resolved _before_ `bootstrapApplication`**, not inside an app initializer. That is what makes `APP_CONFIG` genuinely available to every provider factory from the first moment of the injector's life — `ApiUrl` reads it in a field initializer, and `MSAL_INSTANCE` is built from it in the same pre-bootstrap step, since `initialize()` is asynchronous and no synchronous DI factory can await it.

`APP_CONFIG` carries **no `factory`**. A missing provider must fail loudly at injection rather than hand out a default that quietly points at the wrong environment. Tests and any injector created outside the bootstrap path use `provideAppConfig(config)`.

### 3.4 The startup shell, and why it starts visible

`index.html` carries a small static shell with two states, `data-state="starting"` and `data-state="failed"`. `main.ts` removes it on success and switches it to `failed` on either failure — a config that could not be loaded, or a bootstrap that threw.

Three properties matter, and each covers a failure the app cannot report from inside itself:

- **It is visible from first paint.** Starting hidden and revealing it from script would leave a blank page whenever the _bundle itself_ never loads — a hashed-filename mismatch, a CDN miss, a CSP block — because nothing would be running to reveal it.
- **Its styles are inline in `index.html`, system fonts only.** It has to render when `styles.scss` never loaded, which is one of the cases it exists for.
- **A 60-second watchdog** runs from an inline `<script>` and flips the shell to `failed` if nothing has cleared it. That is the only report available for "the bundle never ran at all". It was 30 seconds until US-201 put a sign-in handshake and a session bootstrap inside the shell's lifetime, where a cold API can legitimately hold the screen for tens of seconds and an early alarm would tell a user their app had failed while it was still starting.

The failure state names the offending field in a `<code>` block, tells the reader this is a deployment problem rather than their account, and offers a **Try again** button that reloads. Smaller decisions inside `fatal-shell.ts` are worth knowing before editing it: the detail is written with `textContent` and never `innerHTML`, because the reason can carry a server-supplied status line and this page renders before any sanitizer exists; the retry handler is attached with `addEventListener` rather than an inline `onclick`, so the page stays CSP-clean; and `role="alert"` is applied to the status region **after** its content is final, because setting the role up front and mutating afterwards is the usual way a live region ends up announcing nothing.

To see it: rename `public/config.json`, hard-reload, rename it back.

### 3.5 What operations has to do

1. **Serve `config.json` uncached, beside `index.html`.** `public/config.json` is the committed development copy and ships to `dist/` unhashed. The client already sends `cache: 'no-store'`; a CDN or host that caches it anyway reintroduces the promotion bug the design removes, so set `Cache-Control: no-store` (or at minimum `no-cache`) on that path.
2. **Overwrite it as a deployment step**, per environment, before or with the artifact. A missing or invalid file does not degrade — the app does not bootstrap at all, by design.
3. **Do not put secrets in it.** It is public deployment metadata, fetched with `credentials: 'omit'` precisely so cookies are never sent to a CDN origin serving it.

## 4. Error normalization

### 4.1 Ten problem types, mirrored verbatim

[`problem-types.ts`](../../enterprise-gpt-ui/src/app/core/errors/problem-types.ts) mirrors `Enterprise.Gpt.Api/Problems/ProblemTypes.cs` exactly: `validation-error`, `upload-too-large`, `resource-not-found`, `forbidden`, `permission-required`, `conversation-busy`, `mcp-server-unavailable`, `provider-not-configured`, `storage-not-configured`, `export-renderer-not-configured`, all under the relative base `/problems/`.

They are **opaque identifiers matched verbatim, never resolved as links** — RFC 9457 §3.1.1 permits a relative reference, and the API treats them that way. Changing one is a breaking API change on both sides. A response carrying any other `type` (typically the RFC 9110 status-section link ASP.NET Core supplies) is a framework-level problem, not a domain one, and lands on the `http` arm.

`traceId` is **body-only**. The API's CORS policy exposes `Content-Disposition` and nothing else, so there is no header to read a correlation id from — which is why §4.2 keeps the whole problem body around.

### 4.2 One `AppError`, fourteen arms

Every failure the client can observe becomes one value with a `kind` discriminant. Ten arms correspond to the problem types and carry their extensions in typed form; four cover everything else.

| Arm                          | Status | Extra fields          | Meaning                                                               |
| ---------------------------- | ------ | --------------------- | --------------------------------------------------------------------- |
| `validation-error`           | 400    | `errors`              | Per-field messages, keys **verbatim and PascalCase** (§4.4)           |
| `upload-too-large`           | 400    | `maxBytes`            | From the upload filter — a 400, not a 413                             |
| `resource-not-found`         | 404    | —                     | Missing, deactivated, or someone else's                               |
| `forbidden`                  | 403    | —                     | Not allowed                                                           |
| `permission-required`        | 403    | `permissions`         | Display names, not permission ids                                     |
| `conversation-busy`          | 409    | —                     | A turn is already running. Never auto-retried                         |
| `mcp-server-unavailable`     | 502    | `serverName`          | Tool server unreachable, or its on-behalf-of token could not be acquired (§4.5) |
| `provider-not-configured`    | 503    | `providerId`          | The model's provider has no chat client here                          |
| `storage-not-configured`     | 503    | —                     | Blob storage cannot sign download links here                          |
| `export-renderer-not-configured` | 503 | `format`             | This deployment has no renderer for the requested export format       |
| `http`                       | any    | —                     | Any failure with no domain type: 401, routing 404, bare 413, 499, 500 |
| `network`                    | 0      | —                     | No response at all: DNS, TLS, offline, CORS rejection, timeout        |
| `aborted`                    | 0      | —                     | The caller stopped it. Not a failure                                  |
| `client`                     | 0      | —                     | Anything else thrown, including non-`Error` values                    |

Every arm carries `status`, `title`, `detail`, `traceId`, `instance`, `url`, `message`, `problem`, and `cause`. `url` exists separately from `instance` because the API's `instance` omits the query string and therefore cannot reconstruct the failing request.

Two things are deliberately **absent** from the union:

- **A truncated chat stream.** A `text/event-stream` body that stops without a `Finished` event arrives as a successful `200`, so it is a turn state the transcript renders as "cut off", not an error. Normalization never sees it.
- **A `maxBytes` on a bare 413.** Kestrel produces that response, not the upload filter, so it lands on `http` and upload UI has to handle both shapes.

Two helpers guard classification mistakes. `isProblemAppError` is backed by a positive `satisfies` table rather than a negation, so adding an arm without classifying it is a compile error — and misclassifying one silently changes whether it is retried. `isRouteNotFound` separates a routing 404 (a client bug: no such endpoint) from `resource-not-found` (the API's deliberate answer for a row the caller may not see), because reporting the latter as "deleted" is wrong.

### 4.3 Two entry points, one arm selector

| Function                                 | Shape               | Use for                                                                                                                  |
| ---------------------------------------- | ------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| `toAppError(value, options?)`            | synchronous, total  | Every `catch`. Handles `HttpErrorResponse`, abort/timeout signals, `TypeError`, response-like objects, and anything else |
| `toAppErrorFromResponse(response, url?)` | `Promise<AppError>` | The **raw-fetch stream path**, where reading a problem body is asynchronous                                              |

Both funnel into `buildAppError`, which is the single place an arm is chosen, so the two cannot drift.

`toAppError` never throws and never returns a promise. There is deliberately no overload that returns `Promise<AppError>` for a `Response`: it type-checks, but every `catch` binds `unknown`, which selects the synchronous signature and hands the caller a promise _typed_ as an `AppError` — a failure that surfaces far from its cause. Handed a bare `Response`, it still returns a correct arm built from the status alone.

Its classification order is not arbitrary. The thrown value's own identity decides first, and `options.signal` only rescues values that cannot identify themselves, because `AbortSignal.timeout()` sets `aborted` _and_ rejects with a `TimeoutError` — testing the signal first would classify every timeout as a user cancellation, silently, and only for the callers that pass a signal. A timeout is a failed request, not a cancelled one, so it becomes `network` and stays retriable. Passing `signal` is what lets `abort(reason)` — the idiomatic way to record _why_ a turn was stopped, which rejects with the reason itself and can be a plain string — still be recognized as a deliberate stop.

`toAppErrorFromResponse` is called by the streaming transport the moment `response.ok` is false, before it reads a byte of the stream: the chat endpoint sets its `text/event-stream` headers lazily on the first frame, so every pre-stream failure arrives as ordinary `application/problem+json`. It never rejects and never hangs — it skips a `bodyUsed` body, skips a non-JSON content type, and races the read against a 5-second timeout, degrading to the plain `http` arm in each case. The losing promise's rejection is swallowed on purpose, or a stalled body failing later would surface through `provideBrowserGlobalErrorListeners()` as a fresh error seconds after this one was handled.

### 4.4 `applyServerErrors` — validation messages onto a reactive form

The API keys a validation problem's `errors` dictionary by its own property paths. Because `JsonSerializerDefaults.Web` camel-cases property _names_ but leaves dictionary _keys_ alone, those keys arrive PascalCase (`ContextWindowSize`), dotted (`Chunking.MaxTokens`), and indexed (`Files[0].FileName`) while the controls bound to those same properties are named after the camelCase JSON.

```ts
const result = applyServerErrors(this.form, error); // error: ValidationAppError
this.summary.set(result.unmatched);
```

Each key is normalized with **.NET's own camel-casing algorithm** ([`camel-case.ts`](../../enterprise-gpt-ui/src/app/core/errors/camel-case.ts)) rather than a naive lower-casing of the first character, which would turn `IOStream` into `iOStream` where .NET produces `ioStream`. Lookup then falls back to the verbatim path and finally to a case-insensitive walk. Indexed segments become their own numeric segments and the array overload of `form.get()` is used throughout, because a numeric segment in a dotted string is ambiguous between a `FormArray` index and a control literally named `"0"`.

Messages land under a dedicated `server` error key, so Angular drops them on the control's next validation pass — the moment the user edits the field, which is what they expect.

**Nothing is silently discarded, and nothing is written to the form root.** Entries that matched no control — object-level rules, which FluentValidation keys with the empty string, and any path the form does not model — come back on `result.unmatched` for the caller to render. Writing them to the root would not survive: `updateValueAndValidity` walks up the tree and every ancestor replaces its whole `errors` object with its own validators' output, so a hand-set root summary vanishes on the first keystroke in any field. Hold it in a signal instead.

### 4.5 `authErrorDecision` — the only place refresh is decided

```ts
authErrorDecision(error); // 'refresh' | 'passthrough'
```

**Only a bare 401 may trigger a token refresh.** `authInterceptor` (US-201) consults this rather than re-deriving the rule, and replays the request exactly once with a forced refresh when it answers `refresh` — see [Authentication and Session §5.4](authentication-and-session.md#54-the-401-replay).

The arms that matter most are `forbidden` and `permission-required`. The API answers both **403, deliberately not 401**, because the token is fine and the grant is not — something no number of token refreshes can supply. A client that treats either as an authentication failure enters a refresh loop that cannot terminate. (An earlier arm, `mcp-authorization-required`, existed here for the same reason before US-414/US-415 deleted it: tool servers are provisioned tenant-wide by an administrator, so there was never a per-user consent state for a 403 to describe, and a token acquisition that cannot complete now raises 502 `/problems/mcp-server-unavailable` instead — retryable, unlike the card it replaced.)

The decision is backed by `AUTH_DECISIONS`, an exhaustive `as const satisfies Record<AppErrorKind, AuthErrorDecision>` table. Adding an arm to `AppError` without deciding its refresh behaviour is a **compile error**, and the table is exported so a spec asserts the key set too — which makes a new arm fail the suite as well as the compiler.

### 4.6 `retryInterceptor` — GETs, gateway errors, full jitter

`retryInterceptor` is first in `withInterceptors([retryInterceptor, authInterceptor])` in `app.config.ts`, and **must stay first**: `authInterceptor` runs _inside_ each retry, so a retried request acquires a fresh token instead of replaying the one that already failed. A spec pins that — three attempts carry three distinct tokens — and it holds only because `authInterceptor` defers its acquisition to subscription time ([Authentication and Session §5.2](authentication-and-session.md#52-interceptor-order-and-the-defer-that-makes-it-true)).

| Rule          | Value                                                                                                                                                 |
| ------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| Methods       | `GET` only. No other verb is safe to replay, and the chat stream is a `POST` over raw `fetch`, which never reaches an `HttpClient` interceptor at all |
| Retried on    | `network` (transport failure or timeout), and statuses **502, 503, 504**                                                                              |
| Never retried | `409 conversation-busy`, any 503 carrying an application problem type, `aborted`, `client`, and every other problem-typed failure                     |
| Backoff       | Full jitter: a uniform draw from `[0, min(cap, base · 2^attempt))`, base 500 ms, cap 5 s, 3 retries                                                   |

The two exclusions carry the weight. Retrying a **409** races the user's other tab against a turn that is already running. Retrying an **app-typed 503** — `provider-not-configured`, `storage-not-configured` — buys three round trips of nothing, because a deployment state is deterministic rather than transient load, and delays an actionable message by seconds.

Jitter is the point rather than a refinement: synchronized clients retrying on the same schedule are what turn a recovering server back into an overloaded one. `RETRY_POLICY` is an injection token so specs can pin `random`.

Anything not retried is rethrown as the **identical `HttpErrorResponse` instance**, so downstream handlers see the original object.

### 4.7 Turning an error into words

[`error-message.ts`](../../enterprise-gpt-ui/src/app/core/errors/error-message.ts) holds the presentation policy, ready for the toast component in US-106:

- `shouldNotify(error)` — whether the failure deserves a toast. `aborted` does not (the user pressed Stop; announcing it says their own action failed) and `validation-error` does not (the messages are already on the fields). Backed by the same exhaustive `satisfies` pattern.
- `canRetry(error)` — whether to render a Retry control at all. Added by US-302. **A different question from §4.6's**, which decides whether the _interceptor_ may silently replay a GET; this is the slower one of whether a person pressing a button could plausibly get past the failure. A 403, a 404 or a missing grant answers identically however many times it is asked, and a Retry that cannot work reads as a broken button. The `http` arm is decided by status (`>= 500 || 408`), since it also covers a routing 404 that is not going to start existing.
- `userMessage(error)` — one actionable sentence per arm, preferring a written line over the server's `detail`, which is aimed at a developer reading a log.
- `traceLine(error)` — `Trace ID: …`, or a real sentence when there is none, since a 499 carries no body at all.

## 5. The reusable store features

Five features in [`core/state/`](../../enterprise-gpt-ui/src/app/core/state/), each `signalStoreFeature(...)` with standalone updaters exported beside it. They were written before any store, because all sixteen planned stores compose them and a divergence between two hand-rolled pagination implementations is not something review catches reliably.

### 5.1 `withRequestStatus`

|          |                                                                            |
| -------- | -------------------------------------------------------------------------- |
| State    | `requestStatus: 'idle' \| 'pending' \| 'fulfilled' \| { error: AppError }` |
| Computed | `isIdle`, `isPending`, `isFulfilled`, `error` (the `AppError` or `null`)   |
| Updaters | `setPending()`, `setFulfilled()`, `setError(error)`, `setIdle()`           |

**One value, not the `isLoading` + `error` pair it replaces.** That pair can represent "loading and errored at the same time", which is not a state any screen should have to render, and which is where stale spinners come from. Making it unrepresentable is the whole feature.

**The error arm carries the whole `AppError`, not a message string.** The error surfaces in the design — the toast, the error panel in frame `4k` — show the `traceId`, and re-deriving one from a rendered string is impossible.

### 5.2 `withOffsetPagination(take = 100)`

|          |                                                                    |
| -------- | ------------------------------------------------------------------ |
| State    | `skip` (offset for the **next** request), `take`, `totalCount`     |
| Computed | `hasMore`, `isFullyLoaded`                                         |
| Updaters | `setPage(response)`, `setFirstPage(response)`, `resetPagination()` |

It reads the API's `PaginatedResponseDto` envelope but derives from `skip` and `totalCount` rather than the envelope's `currentPage`/`totalPages`, and `skip` advances by the number of items **actually returned** rather than by `take`. One reason for both: a short final page, a page size the server clamped, and a `totalCount` that is an exact multiple of the page size then all land on `hasMore === false` with no special case. `take` is clamped to `MAX_PAGE_SIZE` (100) because the server clamps it to 1–100 anyway, and a drain that assumed it got `take` items would never terminate.

Two traps worth internalizing:

- **`isFullyLoaded` reads `true` before the first fetch.** It is the negation of `hasMore`, and offset state alone cannot tell "no rows exist" from "nothing has been asked for". Anywhere the distinction matters, compose `withRequestStatus` and gate on both: `computed(() => isFulfilled() && isFullyLoaded())`. §5.4's `isAuthoritative` is exactly this case, and a sort control that appears over zero rows and vanishes when the first page lands is worse than one never offered.
- **`setPage` caps `totalCount` at the offset reached when a page comes back empty.** Without that cap, a total that disagrees with the page query — a count that sees rows the page does not, or concurrent inserts shifting the window — leaves `skip` stationary and `hasMore` true, and a `while (hasMore())` drain reissues the identical request forever.

`setFirstPage` exists separately because a search that narrows the result set must not carry the old offset forward — that is how a list comes back empty until the user pages backwards. It also adopts the page size the server actually used, ignoring a nonsensical zero that would wedge the drain at a zero-length step, and applies the same empty-page cap as `setPage` — otherwise an empty first page under a non-zero total costs one wasted request and briefly reports a genuinely empty result as an incomplete set.

### 5.3 `withPendingIds`

|          |                                                                |
| -------- | -------------------------------------------------------------- |
| State    | `pendingIds: ReadonlySet<string>`                              |
| Computed | `hasPendingIds`, `pendingIdCount`                              |
| Methods  | `isRowPending(id)`                                             |
| Updaters | `addPendingId(id)`, `removePendingId(id)`, `clearPendingIds()` |

Per-row in-flight tracking, so a rename on one conversation does not disable the other forty. A `Set` rather than an array because the lookup happens once per row per render, and `isRowPending` reads the signal on every call so a template binding tracks it.

**Every updater replaces the set rather than mutating it, and that is load-bearing rather than stylistic.** `patchState` compares each key by reference before writing, so an updater that mutates the existing `Set` and returns the same reference performs **no signal write at all**. Nothing re-renders and nothing fails — the list simply stops updating, which is a bug that survives review and reproduces only under a real click. (This is also the class of bug `provideCheckNoChangesConfig({ exhaustive: true })` in the test providers is there to catch.)

### 5.4 `withClientQuery(source, config)`

|          |                                                                                                                       |
| -------- | --------------------------------------------------------------------------------------------------------------------- |
| State    | `query`, `sortKey`, `sortDirection`                                                                                   |
| Computed | `results`, `canSort`, `sortUnavailableReason`, `hasQuery`, `isEmptyResult`, `isResultPartial`, `resultsPartialReason` |
| Updaters | `setQuery(q)`, `setSort(key, dir?)`, `toggleSort(key)`, `clearSort()`                                                 |
| Config   | `searchableText`, `comparators`, `isAuthoritative`, `incompleteSetReason`                                             |

**`incompleteSetReason` accepts `string \| Signal<string>`, resolved the same way `isAuthoritative` already was** (a plain value wrapped in a `computed`) — widened for [the documents library's served origin filter](documents-library.md#43-withclientquery-and-where-this-sits-in-the-regime-taxonomy), whose ceiling notice names a different noun once the listing is narrowed to generated documents. A host with nothing to vary can keep passing a plain string.

**Why it exists at all: three list endpoints still accept no sort parameter — the `/all` catalogue routes and the documents listing.** Sorting is therefore only ever _correct_ over a set the client holds in full there, and sorting only the page in hand is never an option — "Load more" would append a second sorted run beneath the first. Rather than leaving that condition implicit in each screen, this feature makes it a value: when `isAuthoritative` is false the sort keys are ignored, results stay in server order, and `sortUnavailableReason` carries the sentence the UI renders in place of the control. Naming the limit is the point; a silently disabled control reads as a bug. The three paginated list endpoints — conversations, projects, users — accept `sort=`/`dir=` as of US-706, and the two screens over them order server-side instead of composing this feature at all (see the closing note below).

**Filtering is not suppressed on an incomplete set** — a filter over part of the data is still useful where a sort over it is simply wrong. It is _qualified_ instead: `isResultPartial` says the count on screen was computed over less than everything, so "No projects match" can be rendered as "No matches among the projects loaded so far" rather than as a false statement about the user's data.

It is composed through `withFeature` so it binds to any `Signal<readonly T[]>`, whatever the host store calls it (see §2). `results` copies before sorting, because `Array.prototype.sort` is in place and the source array is store state.

**The PRD's four sort regimes (§6)** described the world before US-706. Two of them collapsed into one on 2026-08-20 and are kept here as a single row so the table stays checkable against that history:

| Regime                                        | Screens                                  | Loading                                       | `withClientQuery`                                                                                                                                                |
| ---------------------------------------------- | ----------------------------------------- | --------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **A/B** — server-side ordering (since US-706) | Projects, Conversations list             | Drain at `take=100` to a **500-item ceiling** (projects) / server-paged (conversations) — `sort=`/`dir=` ride every request | Not composed for sort in either. The projects grid's select is never disabled at the ceiling, because the order is the server's over the whole set before the ceiling truncates it, not a comparator over a partial one; the conversations toolbar's select replaced US-705's stated caption ([Conversation Library §5.1](conversation-library.md#51-the-toolbar)) |
| **C** — unpaginated, fully correct            | Admin models, admin MCPs (`/all` routes) | Whole set in one request                      | `isAuthoritative: true` (the plain boolean overload), no `withOffsetPagination`                                                                                  |
| **D** — server-paged, search only             | Admin users                              | Server-paged, server-side search              | No client sort; `GET api/users` accepts `sort=`/`dir=` since US-706 too, but the tab wires no control onto either — now a screen-level gap on that tab rather than a limit of the endpoint |

Regime C is untouched by any of this: its `/all` routes were never paginated and needed no order parameter to begin with. One screen the old regime-A row listed never actually reached the client-side comparator it forecast: the project's own Conversations tab (`ProjectConversationsStore`, [Projects (UI) §10.1](projects.md#101-one-filtered-request-and-the-criterion-that-never-shipped)) held no sort control before US-706 and gains none from it — it still takes `GET api/conversations/search`'s default order, unrequested, and is absent from the table above for that reason.

One typing note that saves a confusing hour: the updaters are typed against `SortReadableState`, whose `sortKey` is widened to `string | null`, because a host store's `sortKey` is its own key union and an updater declaring only the literal it was called with would not accept that state. `toggleSort`'s flip branch deliberately returns **no** `sortKey` — re-emitting the one already in state would widen it back to `string` and stop matching the host's union.

### 5.5 `withResetOnSignOut` — compose it last

```ts
signalStore(
  withEntities<Project>(),
  withRequestStatus(),
  /* … everything that contributes state … */
  withResetOnSignOut(), // ← last
);
```

It snapshots the store's initial state with `getState` during construction and, on `sessionEvents.signedOut`, patches that snapshot back.

**Why the ordering rule is real.** `withProps` runs during construction in composition order. Listed last, it snapshots the complete state — everything `withState`, `withEntities`, `withRequestStatus`, and `withOffsetPagination` contributed — and it takes that snapshot before any `onInit` hook has loaded data into it. A state-contributing feature composed _after_ it adds a key the snapshot does not carry, and `patchState` then leaves that key untouched on sign-out: **the store half-clears, and the surviving slice belongs to the previous user.**

The type system cannot catch it — the feature's `State` parameter has no inference site and degrades to `object` — so the rule is asserted at runtime instead. A **dev-only `onInit` guard** compares the live state's keys against the snapshot's and throws naming the offending ones:

```text
Error: withResetOnSignOut must be the last feature in the store: sortKey, sortDirection would survive sign-out.
```

By `onInit` the live state carries every key, including those from features composed afterwards — which is precisely what the snapshot missed. The guard is skipped outside dev mode, so it costs nothing in production. Features that contribute no state (`withMethods`, `withComputed`) may follow it safely, but "last" is the rule worth remembering.

The snapshot is taken inside `untracked(...)`, because construction can occur inside a reactive context — an injection from within a `computed` — where an eager read of every state signal would silently make that computed depend on the whole store.

**Clearing state is not the same as cancelling work.** This is the single most important sentence in this section. A request already on the wire when sign-out fires will still resolve and write the previous user's rows back over the just-cleared state. This feature cannot cancel on the store's behalf, so **any store that loads asynchronously must also compose `takeUntil(injectSignedOut())` into its inner pipe**:

```ts
switchMap((id) => api.load(id).pipe(takeUntil(signedOut$), mapResponse({ … })))
```

`injectSignedOut()` is a function rather than a prop on the feature exactly because the feature has to be composed last and nothing after it could read such a prop. It must be called in an injection context — the `withMethods` factory parameter list in §2 is the idiomatic place.

### 5.6 Cross-store events

The `@ngrx/signals/events` plugin is a **deliberate, narrow opt-in**, not the app's default state style. This is not classic NgRx: there are no actions, reducers, or effects.

Sign-out is the canonical case events exist for — one thing happens and _every_ store has to react to it, across route-scope boundaries, without any of them knowing the others exist. A method on a session service would require each store to register with it, which inverts the dependency the wrong way: a root service would have to reach into route-scoped stores.

```ts
export const sessionEvents = eventGroup({
  source: "Session",
  events: { signedOut: type<void>() },
});
```

`turnEvents` (US-409) follows this shape — one `completed` event carrying `{ conversationId, wasFirstTurn }`, dispatched by the route-scoped `TurnStore` and handled by `ConversationStore` and, through it, the root-scoped sidebar list ([Turn Lifecycle §7.8](../conversations/turn-lifecycle.md#78-the-name-the-server-generates-us-409)) — and `adminEvents` (US-1207, US-1208) will. Everything else stays on plain store methods.

**It has exactly one producer, and only one is wanted.** Since US-205 that is `AuthService.signOut()`, which dispatches it with `{ scope: 'global' }` — plus the `SessionChannel` listener, which does the same when _another tab_ signs out. Every store — `SessionStore`, `ModelCatalogStore`, `McpCatalogStore` — resets on it and cancels its in-flight request with `takeUntil`; `AuthService` also clears its own handshake memo and the two `sessionStorage` keys it owns. Do not dispatch it from a component to "reset things": the event means the session has ended, and the guards, the interstitial and MSAL's cache are all keyed to that meaning ([Authentication and Session §11](authentication-and-session.md#11-sign-out-us-205)).

> **Dispatch `signedOut` on the global scope.** `Dispatcher` and `Events` are `platform`-scoped by default, but a component or route calling `provideDispatcher()` creates a local scope, and an event dispatched inside one is invisible to ancestors and siblings. A sign-out dispatched from within such a scope must carry `{ scope: 'global' }`, or the route-scoped stores in every other scope never reset — which is the entire failure this event exists to prevent.

#### `injectSignedOutAbort()` — for work `takeUntil` cannot stop

```ts
const signedOutSignal = injectSignedOutAbort(); // injection context, once

fetch(url, { signal: AbortSignal.any([stop.signal, signedOutSignal()]) });
```

`takeUntil(injectSignedOut())` stops **delivery**. It leaves the HTTP connection open and, for EP-4's streaming turn, leaves the model generating on the server — only aborting the underlying `fetch` ends the request. That is the gap this fills, and it is the primitive US-405's Stop composes with.

Four properties are deliberate:

- **A factory, not one shared signal.** Two concurrent requests need signals that can be told apart, or a store cancelling one turn also aborts the other. (`AbortSignal.any` composes without consuming its sources, so sharing one would not _break_ a caller's Stop — it would just make every request indistinguishable.)
- **`AbortSignal.any` is what keeps it from leaking.** The DOM Standard holds a signal's dependent signals in a **weak set**, so each minted signal is collectable as soon as the caller drops it, and a thousand-turn session accumulates no controllers.
- **It also aborts on `DestroyRef.onDestroy`.** A stream can outlive the store that opened it; without this, a route-scoped store's in-flight request keeps running after the route is gone.
- **A signal minted after the session ended is already aborted**, which is correct rather than incidental: sign-in here is always a redirect, so a session never resumes within the same document. The request fails immediately as an `aborted` `AppError`, which `shouldNotify` maps to no toast (§4.7).

The abort reason is `SIGNED_OUT_ABORT_REASON`, exported so a reader can tell a sign-out from a user's Stop.

### 5.7 Composition order

`signalStore` composes in order, so the order is part of the contract:

| Position | Feature kind                                                                               | Why                                                |
| -------- | ------------------------------------------------------------------------------------------ | -------------------------------------------------- |
| 1        | `withState`, `withEntities`, `withRequestStatus`, `withOffsetPagination`, `withPendingIds` | Everything that contributes state comes first      |
| 2        | `withProps`                                                                                | Injected dependencies and derived non-signal props |
| 3        | `withComputed`, `withFeature(… withClientQuery …)`                                         | Derivations, which need the state above them       |
| 4        | `withMethods`                                                                              | Reads state and computed values                    |
| 5        | `withHooks`                                                                                | Runs after the store exists                        |
| last     | `withResetOnSignOut`                                                                       | §5.5                                               |

Beyond ordering, the standing rules for stores in this repo: keep `protectedState` on, write only through `patchState` with standalone updaters, use `rxMethod` (not `signalMethod`) wherever requests can overlap so `switchMap` prevents a stale response overwriting a fresh one, and use one store per entity type with `withEntities` for keyed collections.

## 6. Testing

Everything above is covered by Vitest specs that run with no backend and, for the framework-free parts, no `TestBed`.

| Area                        | Specs                                                                                                                                                             | Notable cases                                                                                                                                                                                                                        |
| --------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Config                      | `app-config`, `app-config.token`, `load-app-config`, `fatal-shell`                                                                                                | Each named-field failure; a 200 carrying `index.html`; the timeout; the shell's `textContent`-only rendering and watchdog clearing                                                                                                   |
| Errors                      | `to-app-error` (31 cases), `to-app-error-from-response`, `problem-details`, `problem-types`, `apply-server-errors`, `camel-case`, `error-message`, `retry-policy` | Timeout-versus-abort ordering; a `bodyUsed` body; PascalCase, dotted and indexed keys; the `AUTH_DECISIONS` key set                                                                                                                  |
| HTTP                        | `api-url`, `retry.interceptor`                                                                                                                                    | GET-only, the 409 and app-typed-503 exclusions, jitter bounds via a pinned `random`                                                                                                                                                  |
| State                       | `with-request-status`, `with-offset-pagination` (15 cases), `with-pending-ids`, `with-client-query` (16 cases), `with-reset-on-sign-out`, `toast-store`           | The exact-multiple page boundary; the empty-page cap; the replace-not-mutate signal write; `isAuthoritative` false suppressing sort; the dev-only ordering guard throwing; a toast timer cancelled on sign-out                       |
| Storage and navigation      | `local-preferences`                                                                                                                                               | The allowlist holding only values about the browser; storage that throws rather than returning `null`                                                                                                                                |
| Theme and assets            | `theme-service`, `load-icon-sprite`, `icon`, `icon-names`, `brand-logo`, `ridgeline`, `theme-toggle`                                                              | Blocked `localStorage`; an OS change while the preference is `system`; a sprite response that is really `index.html`; both logo variants carrying the same `alt`                                                                     |
| UI kit                      | `a11y`, `modal`, `offcanvas`, `menu`, `tooltip`, `feedback`, `data`, `badge`, `attachment-chip`, `upload`, `user-footer`                                          | Focus in and focus return; Escape and outside-pointer dismissal; roving menu focus; the two live regions; the polite result summary; selection updaters returning a new `Set`; a drop zone that binds no listeners without the grant |
| Streaming contract          | `assistant-ui.contract`                                                                                                                                           | `foldAssistantEvents` over the vendored types, in Node with no `TestBed`                                                                                                                                                             |
| Auth and session (EP-2)     | `auth-service`, `token-service`, `auth.interceptor`, the three guards, `session-store`, `session-channel`, `session-events`, `auth-pages`, `app`                  | Covered in [Authentication and Session §14](authentication-and-session.md#14-testing-and-verification-status)                                                                                                                        |
| Shell and navigation (EP-3) | `conversation-list-store`, `shell`, `chat`, `ui-store`                                                                                                            | Covered in [Shell and Navigation §10](shell-and-navigation.md#10-testing)                                                                                                                                                            |

The suite is **610 specs** across 55 files as of the end of EP-3.

```bash
# from enterprise-gpt-ui/
npm test               # Vitest, single run
npm run test:watch     # watch mode
npm run format:check   # Prettier, read-only
npm run lint           # ESLint + check:icons + check:forbidden + check:tokens
npm run check:contract # the vendored streaming contract, byte-for-byte (needs the NuGet cache)
npm run build          # production build; the budgets, then check-initial-chunk.mjs
```

Those five gates are what `.github/workflows/ui-ci.yml` runs — the repository's first CI workflow, since joined by `api-ci.yml` on the .NET side. `check:contract` sits on its own job because it is the only one that needs the .NET toolchain. What each gate enforces, and why several of them are Node scripts rather than lint rules, is in [Design System §10](design-system.md#10-gates); how the two workflows decide what to run, and what has to be listed in branch protection for either to be required, is in [Pull-request checks](../ci/pull-request-checks.md).

**One of those gates constrains code you are about to write.** Since US-205, `check:forbidden` fails the build on any `localStorage.setItem` outside [`core/storage/local-preferences.ts`](../../enterprise-gpt-ui/src/app/core/storage/local-preferences.ts). Writes only — reading cannot leave a user's data on a shared machine, and `index.html`'s pre-paint script legitimately reads the theme before any module exists. The rule behind it: `localStorage` survives sign-out by design, so it holds only values **about the browser, not about the user**, and `PREFERENCE_KEYS` enumerates them (today: the theme, and the sidebar collapse state US-301 writes — which is why `UiStore` is the one store that must **not** compose `withResetOnSignOut`, [Shell and Navigation §6](shell-and-navigation.md#6-uistore--a-preference-about-the-browser-not-the-user)). Anything identifying, anything fetched from the API, and anything a second person on a shared machine should not see belongs in `sessionStorage` or in a store. `check:tokens` reads the theme key from that file too, so the pre-paint script and the application cannot drift.

## 7. What is not here yet

Written at the end of EP-1; every row has since been struck through.

| Missing                                                                  | Owner                                                                                                                                               |
| ------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------- |
| ~~The composer, the transcript, and any per-row conversation action~~    | Shipped — [Conversation Actions](conversation-actions.md) (US-304/305/306/308) and [Turn Lifecycle](../conversations/turn-lifecycle.md) (EP-4/EP-5) |
| ~~Markdown rendering; `ngx-markdown` is installed but imported nowhere~~ | Shipped — [Answer Rendering](answer-rendering.md) (US-601/602/606), behind the lazy chat route                                                      |
| ~~The SSE codec and the chat transport~~                                 | Shipped — [Streaming Client](../conversations/streaming-client.md) (US-404/405); `injectSignedOutAbort()` (§5.6) is what it cancels on              |

Sign-in, `TokenService`, `authInterceptor`, the guarded route tree and sign-out have all since landed — EP-2 is complete. So have EP-3 (the signed-in shell, the conversation sidebar, the reference list store and the chat route), EP-4/EP-5 (send, stream, stop, the activity timeline), the whole of **EP-6** (markdown, the streaming split, pinning, code chrome and theming, deferred diagrams and math, message-level Copy), and EP-7's opening story. See [Authentication and Session](authentication-and-session.md), [Shell and Navigation](shell-and-navigation.md), [Turn Lifecycle](../conversations/turn-lifecycle.md), [Answer Rendering](answer-rendering.md) and [Conversation Library](conversation-library.md).

## 8. Key files

| Concern                                | File                                                                                                                                                                                                                                                                                                                                                         |
| -------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Bootstrap order                        | [`src/main.ts`](../../enterprise-gpt-ui/src/main.ts)                                                                                                                                                                                                                                                                                                         |
| App providers, interceptor order       | [`src/app/app.config.ts`](../../enterprise-gpt-ui/src/app/app.config.ts)                                                                                                                                                                                                                                                                                     |
| Config shape and validation            | [`core/config/app-config.ts`](../../enterprise-gpt-ui/src/app/core/config/app-config.ts)                                                                                                                                                                                                                                                                     |
| Config load                            | [`core/config/load-app-config.ts`](../../enterprise-gpt-ui/src/app/core/config/load-app-config.ts)                                                                                                                                                                                                                                                           |
| Startup / fatal shell                  | [`src/index.html`](../../enterprise-gpt-ui/src/index.html), [`core/config/fatal-shell.ts`](../../enterprise-gpt-ui/src/app/core/config/fatal-shell.ts)                                                                                                                                                                                                       |
| Error union                            | [`core/errors/app-error.ts`](../../enterprise-gpt-ui/src/app/core/errors/app-error.ts)                                                                                                                                                                                                                                                                       |
| Arm selection                          | [`core/errors/build-app-error.ts`](../../enterprise-gpt-ui/src/app/core/errors/build-app-error.ts)                                                                                                                                                                                                                                                           |
| Retry and refresh policy               | [`core/errors/retry-policy.ts`](../../enterprise-gpt-ui/src/app/core/errors/retry-policy.ts)                                                                                                                                                                                                                                                                 |
| Store features                         | [`core/state/`](../../enterprise-gpt-ui/src/app/core/state/)                                                                                                                                                                                                                                                                                                 |
| The reference store that composes them | [`core/conversations/conversation-list-store.ts`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-list-store.ts)                                                                                                                                                                                                                             |
| Session events and the abort primitive | [`core/events/session-events.ts`](../../enterprise-gpt-ui/src/app/core/events/session-events.ts)                                                                                                                                                                                                                                                             |
| `localStorage` allowlist               | [`core/storage/local-preferences.ts`](../../enterprise-gpt-ui/src/app/core/storage/local-preferences.ts)                                                                                                                                                                                                                                                     |
| Document replacement seam              | [`core/navigation/hard-navigation.ts`](../../enterprise-gpt-ui/src/app/core/navigation/hard-navigation.ts)                                                                                                                                                                                                                                                   |
| Pagination envelope mirror             | [`domain/api/paginated-response.ts`](../../enterprise-gpt-ui/src/app/domain/api/paginated-response.ts)                                                                                                                                                                                                                                                       |
| Test providers                         | [`src/testing/test-providers.ts`](../../enterprise-gpt-ui/src/testing/test-providers.ts)                                                                                                                                                                                                                                                                     |
| Vendored streaming contract            | [`domain/stream/andes/assistant-ui.contract.ts`](../../enterprise-gpt-ui/src/app/domain/stream/andes/assistant-ui.contract.ts), [`scripts/check-andes-contract.mjs`](../../enterprise-gpt-ui/scripts/check-andes-contract.mjs)                                                                                                                               |
| Lint and CI                            | [`eslint.config.mjs`](../../enterprise-gpt-ui/eslint.config.mjs), [`.github/workflows/ui-ci.yml`](../../.github/workflows/ui-ci.yml)                                                                                                                                                                                                                         |
| Related reference                      | [Authentication and Session](authentication-and-session.md), [Shell and Navigation](shell-and-navigation.md), [Design System](design-system.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md), [Conversation Streaming Contract](../conversations/streaming-contract.md), [`enterprise-gpt-ui/README.md`](../../enterprise-gpt-ui/README.md) |
