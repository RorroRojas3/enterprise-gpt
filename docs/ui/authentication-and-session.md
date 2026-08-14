# Authentication and Session

How the rebuilt Angular client at `enterprise-gpt-ui/` signs a user in with Microsoft Entra ID, gets an access token onto every API call, learns who the user is and what they may do, and keeps the administration code off a non-administrator's device.

Audience: a developer about to write their first guarded screen, add a permission check, or debug a redirect loop. Read [Frontend Foundation](frontend-foundation.md) first if you have not — this page builds directly on that document's runtime configuration, its `AppError` union, and its composable store features. Bare `§` references below are to sections of *this* page.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference below.

## 1. Overview

EP-2 is what turns the EP-1 foundation into an application a person can be *in*, and then out of again. All five stories have landed:

| Story | What it delivers |
| --- | --- |
| **US-201** | Sign-in with Entra ID over the MSAL redirect flow, the four full-page auth states, and one `TokenService` both transports draw from |
| **US-202** | `POST api/users/me` resolved before any screen activates, holding the user and their grants, followed by the model and MCP catalogs |
| **US-203** | The admin route chunk withheld from a non-administrator's browser rather than hidden from their sidebar |
| **US-204** | The `Upload File` grant enforced two ways — `@if` for every affordance, a directive for the drop zone that must render and stay inert (§10) |
| **US-205** | Sign-out that leaves nothing behind: every store emptied, every tab told, the token cache wiped, and a landing page that does not sign the user straight back in (§11) |

Five things in this epic are worth knowing *before* you touch it, because each is a decision that looks arbitrary from the outside:

1. **`@azure/msal-angular` is installed and used nowhere.** Three verified defects make it unusable in a zoneless app that needs a `canMatch` guard (§3).
2. **Guards in one `canActivate` array run concurrently.** Nesting is the only way to order two of them, which is why the authenticated route tree is two levels deep with one guard each (§6).
3. **The startup shell is removed on the first settled navigation, not when bootstrap resolves** — otherwise an empty app shell paints while guards are still deciding (§8).
4. **`logoutRedirect` throws in three different ways, and MSAL's cache is in a different state in each** — which is the whole reason sign-out is ordered the way it is (§11.2).
5. **MSAL costs 248.9 kB of the initial bundle**, not the ~150 kB the PRD estimated, and the budgets moved accordingly (§12).

> **US-204 shipped its enforcement primitives, not a screen.** The composer belongs to US-401/US-801 and does not exist yet, so there is no attach control to hide and no prompt box to make inert. What landed is the pair of mechanisms EP-8 consumes in two lines, plus a test host standing in for the composer. The frame `2h` assertion re-runs against the real one when it arrives.

### 1.1 Where each piece lives

| Concern | Where |
| --- | --- |
| MSAL instance token and configuration | [`core/auth/msal-instance.ts`](../../enterprise-gpt-ui/src/app/core/auth/msal-instance.ts) |
| Creating and initializing MSAL before bootstrap | [`src/main.ts`](../../enterprise-gpt-ui/src/main.ts) |
| The redirect handshake, sign-in, sign-out, return URL | [`core/auth/auth-service.ts`](../../enterprise-gpt-ui/src/app/core/auth/auth-service.ts) |
| Access tokens | [`core/auth/token-service.ts`](../../enterprise-gpt-ui/src/app/core/auth/token-service.ts) |
| Attaching the bearer token to `HttpClient` | [`core/auth/interceptors/auth.interceptor.ts`](../../enterprise-gpt-ui/src/app/core/auth/interceptors/auth.interceptor.ts) |
| Guards | [`core/auth/guards/`](../../enterprise-gpt-ui/src/app/core/auth/guards/) — `auth.guard.ts`, `session.guard.ts`, `admin.guard.ts` |
| Route path constants | [`core/auth/auth-routes.ts`](../../enterprise-gpt-ui/src/app/core/auth/auth-routes.ts) |
| Who is signed in, and their grants | [`core/session/session-store.ts`](../../enterprise-gpt-ui/src/app/core/session/session-store.ts) |
| Bootstrap sequencing | [`core/session/session-bootstrap.ts`](../../enterprise-gpt-ui/src/app/core/session/session-bootstrap.ts) |
| Cross-tab sign-out | [`core/session/session-channel.ts`](../../enterprise-gpt-ui/src/app/core/session/session-channel.ts) |
| Session events, and the abort primitive | [`core/events/session-events.ts`](../../enterprise-gpt-ui/src/app/core/events/session-events.ts) |
| The `localStorage` allowlist | [`core/storage/local-preferences.ts`](../../enterprise-gpt-ui/src/app/core/storage/local-preferences.ts) |
| Replacing the document, as opposed to routing | [`core/navigation/hard-navigation.ts`](../../enterprise-gpt-ui/src/app/core/navigation/hard-navigation.ts) |
| Models and MCP servers | [`core/catalog/`](../../enterprise-gpt-ui/src/app/core/catalog/) — `model-catalog-store.ts`, `mcp-catalog-store.ts` |
| Permission ids | [`domain/api/permission-ids.ts`](../../enterprise-gpt-ui/src/app/domain/api/permission-ids.ts) |
| The five full-page auth states | [`features/auth/`](../../enterprise-gpt-ui/src/app/features/auth/) |
| The sign-out interstitial | [`shared/feedback/signing-out/`](../../enterprise-gpt-ui/src/app/shared/feedback/signing-out/) |
| Who is signed in, in the chrome | [`shared/nav/user-footer/`](../../enterprise-gpt-ui/src/app/shared/nav/user-footer/) |
| Drop zone, drop overlay | [`shared/upload/`](../../enterprise-gpt-ui/src/app/shared/upload/) — `file-drop-target.ts`, `drop-overlay.ts` |
| Window-level drop suppression | [`core/dnd/prevent-drop-navigation.ts`](../../enterprise-gpt-ui/src/app/core/dnd/prevent-drop-navigation.ts) |
| Startup-shell handoff | [`core/config/startup-shell-handoff.ts`](../../enterprise-gpt-ui/src/app/core/config/startup-shell-handoff.ts) |
| Route table and guard nesting | [`src/app/app.routes.ts`](../../enterprise-gpt-ui/src/app/app.routes.ts) |
| Admin-chunk build gate | [`scripts/check-initial-chunk.mjs`](../../enterprise-gpt-ui/scripts/check-initial-chunk.mjs) |
| `localStorage` build gate | [`scripts/check-forbidden-apis.mjs`](../../enterprise-gpt-ui/scripts/check-forbidden-apis.mjs) |

Versions: `@azure/msal-browser` **5.18.0**, `@azure/msal-angular` **6.0.3** (installed, unused), Angular **21.2.19**, `@ngrx/signals` **21.1.1**.

## 2. Quick start

### 2.1 Reading the signed-in user and their permissions

`SessionStore` is a root-provided signal store. By the time any guarded component renders, it is loaded.

```ts
import { Component, inject } from '@angular/core';
import { SessionStore } from '@core/session/session-store';
import { PERMISSION_ID } from '@domain/api/permission-ids';

@Component({ /* … */ })
export class ConversationHeader {
  protected readonly session = inject(SessionStore);

  // Signals, read directly in the template.
  //   session.displayName()     → 'Ada Lovelace'
  //   session.isAdministrator() → boolean
  //   session.canUploadFiles()  → boolean
  //   session.permissionIds()   → ReadonlySet<string>

  // Run-time grants — one per MCP server — have no compile-time constant:
  protected canUse(mcpPermissionId: string): boolean {
    return this.session.hasPermission(mcpPermissionId);
  }
}
```

Two rules that are easy to get wrong:

- **Match permissions by id, never by name.** `PERMISSION_ID` holds the only two grants that exist at compile time. Every other permission is created per MCP server at run time, and its `name` is display text an administrator can edit.
- **`Administrator` implies nothing.** The API deliberately does not grant admins every permission, and `canUploadFiles` is a separate computed for exactly that reason. An administrator without `Upload File` may not upload (US-204).

### 2.2 Calling the API

Do nothing. `authInterceptor` attaches the bearer token to every `HttpClient` request whose URL belongs to the configured API.

```ts
const http = inject(HttpClient);
const url = inject(ApiUrl).build('conversations/search'); // → https://…/api/conversations/search

http.get<PaginatedResponse<ConversationDto>>(url); // Authorization: Bearer … is added for you
```

For the raw-`fetch` chat stream (EP-4), which never reaches an `HttpClient` interceptor, take the token yourself from the same service:

```ts
const token = await inject(TokenService).getToken();
```

### 2.3 Guarding a new route

Put it inside the existing authenticated tree in `app.routes.ts`, as a child of the `sessionGuard` level. Do **not** add `authGuard` or `sessionGuard` to your own route — §6 explains why that breaks ordering.

### 2.4 Gating an affordance on a permission

`@if` on the permission, everywhere it will render at all:

```html
@if (session.canUploadFiles()) {
  <button type="button" class="composer__attach">Attach</button>
}
```

The one case `@if` cannot express is an element that must still render and simply not react — a prompt box that accepts typing but no dropped file. That is the directive in §10:

```html
<div [appFileDropTarget]="session.canUploadFiles()" (filesDropped)="attach($event)" #drop="appFileDropTarget">
  <textarea …></textarea>
  @if (drop.isDragOver()) { <app-drop-overlay /> }
</div>
```

### 2.5 Signing out

```ts
await inject(AuthService).signOut();
```

That is the whole API, and there is deliberately nothing else to call: it dispatches the event that empties every store, tells the other tabs, and leaves the page. Nothing needs to clean up after it, and nothing should try — a screen that reacts to sign-out by navigating will fight it (§11.4).

## 3. `@azure/msal-angular` is installed and unused

This is the largest deviation from the PRD in EP-2 and the first thing to understand, because the PRD names `MsalGuard` and the code contains no reference to the package at all. The rationale lives in [`core/auth/msal-instance.ts`](../../enterprise-gpt-ui/src/app/core/auth/msal-instance.ts); it is repeated here so that a reader deciding whether to "fix" the omission has the full argument.

**Three verified reasons, none stylistic:**

1. **`MsalGuard` cannot do `canMatch`.** Its `canMatch()` overload calls the internal helper with no `RouterStateSnapshot`, and that helper answers a signed-out user with `of(false)` rather than a redirect. A `canMatch` guard is the *only* kind that can keep a lazy chunk off the wire, which is precisely what US-203 requires — so the one guard shape this application needs is the one the class cannot provide.
2. **`MsalService` replays a consumed redirect.** It caches the redirect hash in its constructor and feeds it into every later `handleRedirectObservable()` call, with `navigateToLoginRequestUrl` left at its default. After `/auth` has already consumed the response, that replay either navigates the page again or throws — and `MsalGuard` turns the throw into a bounce to `/login-failed` immediately after a **successful** sign-in.
3. **`MsalBroadcastService` does not work zoneless.** It routes every MSAL event through `NgZone.run`, which under `provideZonelessChangeDetection()` is a no-op that schedules no change detection. Anything bound to `inProgress$` renders once and then lies.

`MsalInterceptor` was already excluded by the PRD for a separate reason: the chat stream is a raw `fetch` that never reaches an `HttpClient` interceptor, and both transports must draw from one token source (§5).

**What replaces it** is small: `AuthService` — one memoized `handleRedirectPromise`, one `loginRedirect` — plus three guards of our own and one interceptor. `@azure/msal-browser`, the library that does the actual work, is used directly and unchanged.

> **`@azure/msal-angular` is now an unused dependency.** It was left in `package.json` rather than removed, because US-101's acceptance criteria require it to be installed and removing it unilaterally would fail that story's gate. Removing it is a small, deliberate follow-up; nothing in `src/` imports it, and `npm run lint` would not notice either way.

### 3.1 msal-browser v5 API changes worth recording

Two options a v4 configuration carried are gone, and both bite silently:

| v4 | v5 (5.18.0) |
| --- | --- |
| `Configuration.auth.navigateToLoginRequestUrl` | Moved onto `handleRedirectPromise(options)` as a **per-call** option, still defaulting to `true` |
| `Configuration.cache.storeAuthStateInCookie` | **Removed from the package outright** |

The app passes `navigateToLoginRequestUrl: false`. Left at its default, MSAL answers the redirect with a `location.replace(...)` back to the originating URL — a second full page load, a second `config.json` fetch, and a second bootstrap. The return trip is this application's to make, through the router, so the deep link travels in `sessionStorage` instead (§4.3).

## 4. Sign-in

### 4.1 Bootstrap order

MSAL is created and `initialize()`d in [`main.ts`](../../enterprise-gpt-ui/src/main.ts) **before** `bootstrapApplication`, beside the runtime configuration it depends on, and provided as an app-owned `MSAL_INSTANCE` token.

```text
index.html paints the startup shell        ← visible from first paint
  └─ main.ts
       ├─ Promise.all([loadAppConfig(), loadIconSprite()])
       │     └─ failure → fatal shell, naming the offending field. No bootstrap.
       ├─ createStandardPublicClientApplication(buildMsalConfig(config, document.baseURI))
       │     └─ failure → fatal shell, "Sign-in could not be initialised for this deployment."
       └─ bootstrapApplication(App, { providers: [APP_CONFIG, MSAL_INSTANCE] })
             └─ failure → fatal shell, "The application failed to start."
```

`initialize()` is asynchronous and no synchronous DI factory can await it, so a provider factory would hand every consumer an instance that is not ready. Resolving it here makes "`MSAL_INSTANCE` is initialized" a property of the injector rather than something each caller has to remember. It also moves a malformed `authority` or `clientId` from sign-in time — where Entra reports it as an opaque "application not found" — to startup, on a screen that says a deployment is broken.

Note the `document.baseURI` argument: Entra matches the registered redirect URI verbatim, so relative values from `config.json` are made absolute here. The committed values start with `/`, which resolves against the **origin**, not against `<base href>`. That is correct at `<base href="/">` and wrong under a sub-path deployment, where they must be written without the leading slash.

### 4.2 The five routes

| Route | Frame | Guarded | Purpose |
| --- | --- | --- | --- |
| `/auth` | `6a`, `6f` in dark | **No** | The MSAL redirect URI. Resolves the handshake, then navigates onward with `replaceUrl` |
| `/login-failed` | `6b` | **No** | Sign-in did not complete. One "Try again" control; **no password field, here or anywhere** |
| `/session-error` | `6c` | **No** | Signed in, but `POST api/users/me` failed. Shows the `traceId` and a Retry |
| `/signed-out` | — | **No** | Where sign-out lands, successfully or otherwise. One "Sign in again" control (§11.6) |
| `/forbidden` | `6e` | Yes | Signed in, not an administrator. One "Back to Chat" action |

**The first four are unguarded deliberately, and it is not an oversight.** `/auth` *is* the redirect URI: a guard there would either recurse or block the very handshake it waits on, and an async guard would hold the navigation open and suppress the interstitial the acceptance criterion requires to be on screen. `/login-failed` and `/session-error` have to render precisely when sign-in or the session cannot succeed — guarding either turns a recovery into a loop. `/signed-out` exists so that leaving does not immediately start arriving again; `authGuard` on it would re-authenticate the user against a still-live Entra cookie the moment they walked away.

All five share `AuthPage`, a centred chrome-free frame, and each focuses its own heading on render through `focusOnRender` (which US-205 moved from `features/auth/` to [`shared/a11y/`](../../enterprise-gpt-ui/src/app/shared/a11y/focus-on-render.ts), because the sign-out interstitial needs it and lives outside the feature). Angular moves focus on neither a router navigation nor a guard redirect, so without it a keyboard user resumes tabbing from the top of the document — and in the `/admin` → `/forbidden` case the element that *had* focus is removed from the DOM outright, dropping focus onto `<body>`. Focusing the heading rather than announcing through a live region is the same choice `fatal-shell.ts` makes: a region inserted together with its text announces nothing, because `aria-live` reports *changes* to a region already in the accessibility tree.

Frame `6f` needs no code of its own. `index.html`'s pre-paint script has already set `data-bs-theme` before any stylesheet loads (see [Design System §3](design-system.md#3-theming-and-the-pre-paint-contract)), so a dark-mode user sees the dark interstitial rather than a light flash.

### 4.3 The handshake, memoized

`AuthService.ready()` resolves `handleRedirectPromise` **once per page load**, however many callers ask, and returns `{ account, error }`.

```ts
const { account, error } = await inject(AuthService).ready();
```

Memoizing is not a micro-optimization. The handshake is the operation that **clears MSAL's `interaction_in_progress` lock**, so it has to run on every page load and not only on `/auth`: a user who reloads the tab mid-redirect leaves that lock set, and every later sign-in attempt fails until the tab is closed. When no interaction is outstanding it resolves in microseconds. `/login-failed` calls `ready()` in `ngOnInit` for exactly this reason — it is the one screen no guard has resolved the handshake for, and without the call its **Try again** button throws against a lock nothing will clear.

The memo is cleared on `sessionEvents.signedOut`. It holds an `AccountInfo`, which is the same class of per-session handle the stores clear — and `withResetOnSignOut` reaches none of it, because it is a field on a service rather than store state. Left alone, a guard consulting `ready()` after sign-out would keep activating for the departed user until the page happened to reload. Clearing it is also the recovery path for a sign-out that failed mid-flight: MSAL leaves `interaction_in_progress` set to `SIGNOUT` when `logoutRedirect` throws after taking it, and only a fresh `handleRedirectPromise` releases it (§11.2).

**What the handshake result is *not* re-read for.** MSAL memoizes `handleRedirectPromise` per hash for the lifetime of the instance, so every later call replays the same response, account included. The redirect result is therefore applied exactly once — `setActiveAccount` on the first pass and never again — and the account is read from the live instance afterwards. Re-applying it would re-select an account sign-out has since evicted, and reading the account *from* it would report a departed user as still signed in.

**The return URL is treated as untrusted.** `signIn(returnUrl)` writes it to `sessionStorage` under `egpt.returnUrl`; `consumeReturnUrl()` reads and clears it, and honours it only when it is an in-app absolute path — a value starting `//` (protocol-relative) or with a scheme falls back to `/chat`. It survived a round trip through the browser and through Entra, so it is an open-redirect vector otherwise. All three `sessionStorage` accesses are wrapped: private mode, blocked storage and enterprise policies throw on access rather than returning `null`, and losing a deep link is a worse landing page, not a failed sign-in.

**`signIn` can reject, and every caller handles it.** On the happy path the returned promise never resolves because the browser is leaving the page — but MSAL performs authority discovery and takes its `interaction_in_progress` lock *before* it navigates, so an unreachable Entra, a redirect attempted inside a frame, and a lock left behind by an abandoned attempt all throw there. An unhandled rejection at that point is a permanently blank page.

### 4.4 The inconclusive handshake

MSAL swallows some malformed responses and resolves `null` **without throwing**, so "no account, no error" is a real outcome. Passing such a visitor onward to a guarded route starts an unbounded `/auth` → `/chat` → Entra → `/auth` loop whose only visible state is the startup shell.

`noteInconclusiveHandshake()` counts those in `sessionStorage` and reports when it is time to stop. The **second** occurrence routes to `/login-failed`; the first is still allowed through, because the benign case — someone opening `/auth` directly — takes the same branch and deserves to land on chat. The counter is cleared whenever an account is established.

### 4.5 Leaving `/auth`

`AuthCallback` navigates with `replaceUrl: true`. That is not cosmetic: it drops `/auth#code=…` from history, so the back button cannot replay a consumed authorization code and the code stops travelling in a URL that later gets logged, bookmarked or shared.

## 5. Tokens

### 5.1 One `TokenService`, two transports

```ts
await tokens.getToken();                       // cached, shared in flight
await tokens.getToken({ forceRefresh: true }); // used once, by the 401 replay
```

`HttpClient` reaches it through `authInterceptor`; EP-4's chat stream is a raw `fetch` that will call it directly. Two token sources would mean two caches, two refresh schedules, and a stream that outlives the token the rest of the app is using — which is the concrete reason `MsalInterceptor` is not used. It returns a `Promise` rather than an `Observable` for the same reason: the stream path is promise-shaped, and an interceptor converts a promise trivially.

Concurrent plain acquisitions share one in-flight promise, so a turn that fires a stream and two list requests in the same tick starts one silent acquisition rather than three racing to write the same cache entry. A `forceRefresh` deliberately does **not** join that memo — it exists because the cached token was rejected, so sharing a non-refreshing acquisition would hand back the token that just failed.

> One honest limit: the guarantee stops at this class. MSAL's own `acquireTokenSilent` de-duplication keys on a request thumbprint that does **not** include `forceRefresh`, so a forced call overlapping a plain one for the same account and scopes can still be merged inside MSAL. The window is narrow, because the memo above already leaves at most one MSAL call outstanding at a time.

When Entra requires interaction — consent, MFA, an expired refresh token — `acquireTokenSilent` throws `InteractionRequiredAuthError`, the service starts a full-page `acquireTokenRedirect`, and then throws `InteractiveSignInRequiredError`. That throw exists to stop the caller, not to be recovered from: the page is on its way out. A second redirect while one is already committed is swallowed, because that is how redirect loops start.

### 5.2 Interceptor order, and the `defer` that makes it true

```ts
provideHttpClient(withFetch(), withInterceptors([retryInterceptor, authInterceptor]))
```

**Retry sits outside authentication.** Every retried attempt therefore re-enters the auth interceptor and acquires a token *for that attempt*, rather than replaying the one that already failed.

That only holds because `authInterceptor` wraps its acquisition in `defer()`. An interceptor function runs **once per request**, to build the chain — so a bare `from(tokens.getToken())` would capture one promise and hand `retryInterceptor` the identical, already-settled token on every re-subscription, quietly turning the retry loop into three copies of one failure. A spec pins this: three attempts against a 502 carry `token-1`, `token-2`, `token-3`.

### 5.3 Only our API gets the token

The interceptor short-circuits unless `ApiUrl.owns(request.url)`. Without that check the API's access token would be attached to whatever third-party host a future feature calls, and to `config.json` and the icon sprite besides.

`owns()` requires the separator explicitly — `url === base || url.startsWith(base + '/')` — because the base URL carries no trailing slash and a bare `startsWith` would also match a lookalike origin whose host merely *begins* with the configured one. It is the single definition of "our API", shared with the streaming `fetch` for the same decision.

### 5.4 The 401 replay

A 401 is replayed exactly once with `forceRefresh: true`, and only when [`authErrorDecision`](frontend-foundation.md#45-autherrordecision--the-only-place-refresh-is-decided) says `refresh`. That function — written in US-103 against an exhaustive `satisfies` table — is the single source of truth, and the arm that matters most is `mcp-authorization-required`: the API answers it **403, deliberately not 401**, because it asks for interactive consent to a downstream tool server, which no number of token refreshes can supply. Refreshing on it produces a loop that cannot terminate.

The single replay needs no attempt counter. `catchError` does not catch the observable its own handler returns, so the replay cannot re-enter the branch.

## 6. Guard ordering — the most surprising thing here

**If you remember one paragraph from this page, make it this one.** Angular hands every guard in a single `canActivate` (or `canMatch`) array to `combineLatest`, which subscribes to all of them at once — so **guards in one array run concurrently**. Per-route checks, in contrast, run under `concatMap`, so a parent's guards settle before a child's begin. **Nesting is the router's only ordering primitive.**

`canActivate: [authGuard, sessionGuard]` would therefore issue `POST api/users/me` *before* an account, and therefore a token, existed. `app.routes.ts` nests them instead:

```ts
{
  path: '',
  canActivate: [authGuard],          // ← establishes the account
  children: [
    {
      path: '',
      canActivate: [sessionGuard],   // ← only then, POST api/users/me
      children: [
        { path: 'forbidden', component: Forbidden },
        {
          path: '',
          loadComponent: /* Shell — the signed-in chrome, lazy */,
          children: [
            { matcher: chatMatcher, loadComponent: /* … */ },
            { path: 'admin', canMatch: [adminCanMatch], loadChildren: /* … */ },
          ],
        },
      ],
    },
  ],
}
```

The second rule sits underneath the first: **every `canMatch` in the tree runs before any `canActivate`**, because `canMatch` is evaluated during URL recognition, alongside `loadChildren`, and `canActivate` runs afterwards. That is why `adminCanMatch` cannot lean on the two guards above it and establishes the account and session itself (§7) — and why `/chat` can safely use `loadComponent`, which the router resolves only *after* guards pass.

### 6.1 The three guards

| Guard | Kind | Does | On failure |
| --- | --- | --- | --- |
| `authGuard` | `canActivate` | Awaits `AuthService.ready()`; starts `signIn(state.url)` when there is no account | `UrlTree` → `/login-failed` |
| `sessionGuard` | `canActivate` | Awaits `SessionBootstrap.ensureSession()` | `UrlTree` → `/session-error` |
| `adminCanMatch` | `canMatch` | Establishes account and session, then checks `isAdministrator()` | `UrlTree` → `/chat`, `/session-error` or `/forbidden` |

**Every failure returns a `UrlTree`, never a bare `false`.** A cancelled navigation renders nothing at all, and `provideStartupShellHandoff` treats `NavigationError` as its cue to remove the startup card — so a guard that simply gives up leaves a genuinely blank page, the state US-201 exists to rule out. For `canMatch` the argument is different but lands in the same place: returning `false` does not cancel the navigation, it *declines the route* and lets matching continue, so the URL falls through to the wildcard and the user silently lands on chat with no explanation.

**Sign-out is the one documented exception, and all three guards take it.** Each opens with `if (auth.isSigningOut()) return false;` — a bare `false`, on purpose. See §11.4 for the loop it closes; the short version is that during teardown a `UrlTree` is just one more navigation re-running these same guards, and a blank page is no longer a defect when the document is being replaced anyway.

`authGuard` uses `canActivate` rather than `canMatch` on purpose. A `canMatch` guard receives `UrlSegment[]` and cannot see query parameters, so the return URL would lose them; and the route it protects lazy-loads through `loadComponent`, which the router resolves after guards, so no chunk reaches the wire on the signed-out path either.

`sessionGuard` performs both its `inject` calls before its first `await` — a guard loses its injection context the moment it suspends.

## 7. The session

### 7.1 `SessionStore`

A root-provided `@ngrx/signals` store holding one `UserDto` and its `requestStatus`. Permissions arrive **inline** on that DTO, so a session costs one request rather than two, and three states stay distinguishable:

| | `requestStatus` | `user()` |
| --- | --- | --- |
| not loaded | `idle` | `null` |
| loaded, no grants at all | `fulfilled` | set, `permissions: []` |
| failed | `{ error }` | `null` |

**A user with zero grants is a valid session, not a load failure** (US-202). Success is `user() !== null`, never a permission count. The API takes the same position on its side, caching the empty set rather than treating it as a cache miss.

Computeds: `isLoaded`, `displayName`, `permissionIds`, `isAdministrator`, `canUploadFiles`. Methods: `ensureLoaded()` (memoized), `reload()` (frame `6c`'s Retry), `hasPermission(id)`.

Three implementation details you will meet:

- **The bootstrap memo is a prop, not state.** A promise handle is not a rendering input, and putting it in state would re-render every consumer on each guard run. The consequence is that `withResetOnSignOut` — which clears *state* — does not reach it, so an `onInit` hook nulls it on `signedOut` explicitly. Without that, the next user on a shared machine would inherit the previous user's resolved promise and never call `POST api/users/me` at all.
- **A generation counter, not `switchMap`.** `rxMethod` + `switchMap` is the house pattern for overlapping requests, but a guard has to `await` this, so the same guarantee is spelled out by hand: only the newest attempt may write.
- **`takeUntil(signedOut$)` on the request.** Clearing state is not cancelling work — a response already on the wire would otherwise write the previous user back over the state that was just emptied. When sign-out does cancel it, the store goes **`idle`**, not `error`: leaving `pending` behind strands it in a state no screen renders, and reporting it as an error would send `sessionGuard` to frame `6c` over the user's own deliberate action.

`reload()` is the only retry this call gets. `retryInterceptor` is restricted to GETs, so a transient 502 on this POST reaches the user rather than being replayed silently — which is why frame `6c` has a button.

### 7.2 `SessionBootstrap` — the sequencing is the point

```ts
async ensureSession(): Promise<boolean> {
  if (!(await this._session.ensureLoaded())) return false;

  void this._models.ensureLoaded();  // not awaited
  void this._mcps.ensureLoaded();    // not awaited

  return true;
}
```

`POST api/users/me` is **awaited first**. It self-provisions the user, returns their grants, and warms the API's own permission cache — so a catalog request issued *alongside* it could be answered from a cache that has not been populated yet. US-202 asks for the catalogs to be "sequenced after the bootstrap call rather than in parallel with it", and this is that sentence in code.

The catalogs are then **not** awaited. They are two independent GETs whose absence each screen already has to handle, and holding the startup shell on screen for them would trade a rendered app for a spinner.

> **Scoped deviation from US-202, since closed on one side.** The story's wording also lists conversations and projects among what the bootstrap requests. Those belong to EP-3 and EP-9, which own their stores. EP-3 has landed, and `ensureSession` now ends with `this._conversations.ensureLoaded()` — which only flips a release flag, because a signal-valued `rxMethod` needs an injection context this service does not have ([Shell and Navigation §4.2](shell-and-navigation.md#42-the-declarative-load-and-the-flag-sessionbootstrap-flips)). Projects follow in EP-9, in the same place. That `SessionBootstrap` exists at all is why the guard never has to reach into a store itself.

### 7.3 The catalog stores

`ModelCatalogStore` (`GET api/models`) and `McpCatalogStore` (`GET api/mcps`) are read-only root stores with the same shape as `SessionStore`: `withState` + `withRequestStatus`, a memoized `ensureLoaded()`, `reload()`, a generation counter, `takeUntil(signedOut$)`, and `withResetOnSignOut()` composed last.

Neither uses `withEntities`. Nothing here updates or removes an item by id — the administrative catalogs that do are separate stores behind the admin route (EP-12) — and both lists are short. `ModelCatalogStore` exposes `defaultModel`, the model a new turn starts on, which is `null` until the catalog has loaded.

`McpCatalogStore` is reloaded per session rather than cached across users: the `mcps` route is already filtered to the caller's grants server-side, and re-filtering client-side would be impossible anyway, since the permission behind each server is created per server at run time and no client constant names it.

## 8. Startup paint

The client now performs a network round trip — possibly a whole trip to Entra — between bootstrap and the first rendered screen. Two changes keep that from showing.

**`withEnabledBlockingInitialNavigation()`.** Without it, `bootstrapApplication` resolves while `authGuard` is still deciding, and an empty app shell renders behind the startup card. With it, nothing Angular owns paints until the first navigation has been decided.

**`provideStartupShellHandoff()`** removes `index.html`'s startup shell on the **first settled navigation** — `NavigationEnd`, `NavigationSkipped` or `NavigationError` — inside `afterNextRender`, rather than when bootstrap resolves. The shell is a block in normal flow, not an overlay, so removing it a render too early leaves an empty `<app-root>` sitting above a "Starting Enterprise GPT…" card. `afterNextRender` puts the removal in the same task as the render that replaces it, so neither a gap nor an overlap is ever painted.

Two near-misses that are worth naming:

- **`ApplicationRef.whenStable()` overshoots badly.** Every `HttpClient` request registers a pending task, so the shell would stay up for the whole session bootstrap *and* both catalog loads.
- **`NavigationCancel` is deliberately not a trigger.** A cancel means either a redirect — a second navigation is already coming, and hiding now would flash — or a guard that refused, which in this application happens only once `authGuard` has committed the browser to leaving for Entra. Keeping the shell up is correct in both cases.

**The `index.html` watchdog moved from 30 s to 60 s.** It exists for the one failure the application cannot report on its own — the bundle never ran — and it now has to sit outside a sign-in handshake and a session bootstrap. A cold API can legitimately keep the shell on screen for tens of seconds, and firing early would tell a user their app had failed while it was still starting.

## 9. Administration, withheld rather than hidden (US-203)

US-203 is a statement about the network, not about visibility. Three mechanisms enforce it, and each covers a way the other two can be defeated.

**1. `adminCanMatch` on the route that owns `loadChildren`.** The router resolves `loadChildren` during URL recognition, and `canMatch` is the only guard that runs in that phase — a `canActivate` guard is evaluated *after* the chunk has been fetched, which hides the admin area without withholding it. The guard must sit on the **same route object** that carries `loadChildren`; on a parent it would run before that route was reached.

**2. The Admin entry is absent, not disabled.** A user without the grant sees no link at all, so frame `6e` is reachable by URL only. That is `@if` in the template as a criterion, not a styling choice.

**3. A build gate.** `scripts/check-initial-chunk.mjs` walks the esbuild metafile's static-import graph from `src/main.ts` and fails the build if anything under `src/app/features/admin/` is in it. One stray static import from a shared module would put the whole admin area in the initial graph and hand it to everyone before any guard ran. The check also names the import path that pulled it in, which a size budget cannot do — a chunk created by a dynamic import has no entry point for a budget to attribute bytes to, so it sums zero and never fails.

A `RouterTestingHarness` spec closes the loop by asserting the observable behaviour rather than the intent: `loadChildren` is a spy, and it is **never invoked** for a non-administrator, a signed-out visitor, or a failed session — while an administrator gets exactly one call and lands on the users tab.

`adminCanMatch` establishes the account and session itself, because of the phase ordering in §6. That costs nothing: both loads are memoized, so the work is shared with the `canActivate` guards that run afterwards. A signed-out visitor is redirected to `/chat` rather than signed in from here — starting a sign-in would still have to decide whether to fetch the chunk afterwards, and handing the navigation to the chat route lets `authGuard` run the redirect with the chunk unrequested either way.

## 10. Upload, gated on the grant (US-204)

US-204 asks for two different things, and they need two different mechanisms. Getting that split right is the whole story, because it is the part a reviewer cannot see from the acceptance criteria.

| Criterion | Mechanism | Why not the other one |
| --- | --- | --- |
| "no attach control, paperclip button, or drop zone is present" | **`@if` in the template**, on `session.canUploadFiles()` | The same answer US-203 gave the Admin nav entry: absent, not disabled. Nothing to style around, nothing to re-enable in devtools |
| "and drag-and-drop is inert" | **The `[appFileDropTarget]` directive** | `@if` cannot express it. The prompt box still has to render and still has to accept typing — it just must not react to a dropped file |
| "an administrator whose permission set omits `Upload File`" still sees nothing | `SessionStore.canUploadFiles`, a computed over the permission id alone | `isAdministrator()` is a separate grant and implies nothing (§7.1). A spec asserts it against a real administrator whose grants omit the id |

### 10.1 `[appFileDropTarget]`

```html
<div [appFileDropTarget]="session.canUploadFiles()" (filesDropped)="attach($event)" #drop="appFileDropTarget">
  <textarea …></textarea>
  @if (drop.isDragOver()) { <app-drop-overlay /> }
</div>
```

| Member | Shape | Notes |
| --- | --- | --- |
| `appFileDropTarget` | `input.required<boolean>()` | Whether drops are accepted |
| `filesDropped` | `output<readonly File[]>()` | Never emits an empty list |
| `isDragOver` | `Signal<boolean>`, read through `exportAs` | Drives the overlay |

Five decisions inside it are worth knowing before editing it, because each looks like the wrong choice until you know what it is avoiding.

- **The permission is a required *input*, not an injected `SessionStore` read.** Burying an authorization decision inside a `shared/` primitive puts it where no reviewer looks, and makes the directive unusable for any non-upload drop. Keeping it an input is what "checked explicitly, never inferred" means in practice. **Required**, so an unbound directive is a `strictTemplates` compile error rather than a silently open drop zone.
- **Listeners are bound with raw `addEventListener` under an `AbortController`, re-bound by an `effect` on the input.** Angular's `host:` metadata cannot bind conditionally, and "binds no listeners at all when disabled" is the criterion — not "binds them and ignores the events". Raw rather than `Renderer2.listen`, because under zoneless the signal write *is* the change-detection notification and there is nothing for a zone-aware wrapper to do.
- **The drag state is a depth counter, not a boolean.** `dragleave` fires every time the pointer crosses into a *child*, so a boolean clears the overlay while the pointer is still well inside the zone. The prettier fix — testing whether `relatedTarget` is contained — is unreliable: Safari has historically reported `relatedTarget: null` on those inner transitions, which is indistinguishable from leaving the window.
- **The counter is a `linkedSignal` off the permission.** Revoking the grant mid-drag — which is exactly what sign-out does — has to clear the overlay, and deriving the reset makes it a property of the type rather than an ordering the next editor has to preserve. It also settles in one change-detection pass instead of two.
- **Every handler filters on `dataTransfer.types` including `'Files'`.** Dragging selected text into the textarea is a legitimate gesture the browser handles for free; an unconditional `preventDefault` would swallow it.

`drop` resets the depth to zero outright rather than decrementing: it consumes the drag and no balancing `dragleave` follows. `dragend` is not an alternative — it never fires for an OS file drag, because the drag did not start in this document. And a dropped **folder** yields an empty `files` in several browsers, so nothing is emitted rather than an empty array: reading a folder needs `webkitGetAsEntry()`, which belongs to EP-8, and a visible no-op the user can retry beats a silent one.

`<app-drop-overlay>` is frame `2a`'s dashed affordance, a separate component because the directive owns state and not pixels — the overlay is specified down to the token, and a component is reviewable against the board where DOM assembled in a directive is not. US-801's composer and US-902's project files panel render the same one. It is `aria-hidden`: a drag is a pointer gesture with no keyboard equivalent, so there is no assistive-technology user for whom it is ever on screen, and the accessible path to the same outcome is the attach button.

### 10.2 `providePreventDropNavigation()`

The browser's default action for a dropped file is to **navigate to it**. A user who misses the composer by twenty pixels loses the page — and, mid-turn, the answer streaming into it. Nothing in script recovers from that, so it is prevented at the window, for every user:

```ts
providers: [providePreventDropNavigation()]   // app.config.ts
```

It lives in `core/dnd/` rather than under `shared/upload/` deliberately: it has to work for a user holding **no** `Upload File` grant at all, which is precisely the US-204 case. Filing it beside the upload feature would tie the protection to the permission it exists to survive.

Three properties make it cooperate with real drop zones instead of breaking them:

- **Bubble phase, never capture, and never `stopPropagation`.** A live drop target is a descendant of the window, so by the bubble phase its handler has already run with `dataTransfer.files` intact and has already called `preventDefault()`. The second call is a no-op on a claimed event and the only effective one on an unclaimed one. In the capture phase this ordering inverts and every drop target breaks.
- **`dropEffect = 'none'` only when `defaultPrevented` is false.** Overwriting a live target's `copy` would make a working zone show the "no drop" cursor. `dropEffect` is honoured only when set during `dragover`; set on `dragenter` alone the browser ignores it.
- **File drags only**, on the same `types` test as the directive.

> **One documented constraint.** `defaultPrevented` detects an *author-installed* drop target only. The browser's own handling of a drop onto a native `<input type="file">` never sets it, so a file dragged onto one would be marked `dropEffect = 'none'` here and its drop cancelled. No such input exists today and EP-8's attach control is a click-to-browse button — but a file input that must also accept drops has to either opt out through a `dataset` flag checked here, or be wrapped in `FileDropTarget` so it claims the event first.

### 10.3 Testing drag and drop

jsdom implements neither `DragEvent` nor `DataTransfer` — there is no `DragEvent-impl.js` in its `living/events/` directory at all — so [`src/testing/drag.ts`](../../enterprise-gpt-ui/src/testing/drag.ts) ships with the story rather than as a convenience. It builds on `MouseEvent`, which is the right base (`DragEvent extends MouseEvent`, and `relatedTarget` comes free from it) and grafts on the subset of `DataTransfer` the app touches: `types`, `files`, and a writable `dropEffect`.

```ts
dispatchDrag(zone, 'drop', fileDrag(textFile('brief.txt')));
dispatchDrag(zone, 'dragleave', fileDrag(), { relatedTarget: child });
```

The specs render a `DropHost` test component that stands in for the composer and carries both halves of the story — the `@if`-gated attach button and the drop zone — bound to the same permission signal, so the two cannot drift apart before US-801 arrives. `/ui-kit` carries the same pair with a checkbox on the grant, for the half of this that only a real pointer can check.

## 11. Sign-out (US-205)

The invariant, and the reason for everything below: **once sign-out begins, the application never returns to an interactive signed-in state.** It either leaves for Entra or replaces the document. Nothing in between is a state any screen renders.

### 11.1 The sequence

```ts
async signOut(): Promise<void> {
  if (this._signingOut()) return;

  const account = this.account;      // captured first: MSAL empties its cache during logout

  this._signingOut.set(true);        // latch — App swaps in the interstitial, guards refuse
  this._channel.post();              // tell the other tabs
  this._dispatcher.dispatch(sessionEvents.signedOut(), { scope: 'global' });

  try {
    await this._instance.logoutRedirect({ account });
  } catch {
    await this._instance.clearCache();          // bare call: storage *and* the crypto keystore
    this._navigation.assign(SIGNED_OUT_ROUTE);  // hard navigation, never the router
  }
}
```

The wipe is itself wrapped and swallowed in the real code: a failed `clearCache` must not stop the user from leaving the page, which is the one thing this method still owes them.

**The latch, the broadcast and the dispatch all land in the same tick.** `App` is `OnPush` under zoneless change detection, so the interstitial and the emptied stores settle into a single render: no screen ever paints a signed-in shell with a cleared `SessionStore` behind it. A spec asserts exactly that — latch and empty in one synchronous step.

**The event is dispatched *before* `logoutRedirect`**, which looks premature and is not. §11.2 is why.

`postLogoutRedirectUri` is deliberately **not** passed in the request. `buildMsalConfig` already resolves it absolutely against `document.baseURI`, and a second source for the same value is a drift waiting to happen.

### 11.2 The MSAL v5 throw taxonomy

This is the least guessable thing on this page, and the ordering above is derived from it. Verified against `@azure/msal-browser` **5.18.0**; `RedirectClient.logout()` clears the token cache **before** it discovers the authority, so *when* a failure happens decides *what state the cache is in*.

| Class | Thrown by | Examples | Cache at the throw | Interaction lock |
| --- | --- | --- | --- | --- |
| **A — synchronous preflight** | `StandardController.logoutRedirect`, before `RedirectClient` runs | `interaction_in_progress`; a redirect attempted in a frame under `allowRedirectInIframe: false`; memory-storage cache | **Intact** | Not taken (A's own `interaction_in_progress` case means someone else holds it) |
| **B — authority discovery** | `RedirectClient.logout`, after `clearCacheOnLogout` | Entra unreachable, DNS or TLS failure, a malformed authority | **Already cleared** | Taken, stuck at `SIGNOUT` |
| **C — blocked navigation** | `NavigationClient.defaultNavigateWindow`, ~30 s *after* it appears to succeed | The redirect was blocked, or the tab is still alive for any other reason | Already cleared | Taken, stuck at `SIGNOUT` |

Three consequences fall out of that table:

1. **Dispatching first is what keeps this application consistent with MSAL, not briefly ahead of it.** Every failure that involves the network has already emptied MSAL's cache by the time it throws. Waiting for `logoutRedirect` to resolve before clearing our own state would mean an application still holding the previous user while MSAL held nobody.
2. **The `catch` calls `clearCache()` with no argument.** `clearCache({ account })` is `removeAccount()` and nothing more; the bare call clears browser storage *and* the crypto keystore. Class A is the only branch that reaches the `catch` with anything left to wipe, and when Entra cannot be reached the full local wipe is the entire point.
3. **The fallback is `location.assign`, never a router navigation.** A stuck `SIGNOUT` lock self-heals only through `handleRedirectPromise` in a **fresh document**, and a router navigation would keep this document, this MSAL instance, and that lock — while re-running the guards sign-out has just latched off.

> **`onRedirectNavigate` is not the teardown hook it looks like.** In msal-browser 5 it is **config-level** (`Configuration.d.ts`, on `BrowserAuthOptions`), not a property of `EndSessionRequest` as a v4 habit expects — and it is therefore shared with `loginRedirect`. A callback installed there to run sign-out cleanup would also fire on every sign-in.

### 11.3 What the failure path lands on

The `catch` navigates to `/signed-out` rather than to `postLogoutRedirectUri`, and that difference is the point. The browser is still here, so the end-session round trip never happened — which means **the Entra session cookie is still live**. Landing on `/` would take the user through `/chat` → `authGuard` → a silent re-authentication of the person who just walked away from the machine, which is the exact failure US-205 exists to prevent. A local wipe and an explicit signed-out page is the honest outcome instead.

### 11.4 The guard latch, and the loop it closes

All three guards return a bare `false` while `isSigningOut()` — the documented exception to §6.1's always-`UrlTree` rule. The loop is concrete rather than theoretical:

```text
a navigation during teardown
  → the handshake memo has been cleared, so the guard re-runs handleRedirectPromise
  → which releases the interaction_in_progress lock the logout still needs
  → which finds no account
  → and starts a *sign-in*, sending the browser to the authorize endpoint
```

The sign-out is cancelled by the sign-in that replaced it. Returning `false` is safe here precisely because the document is leaving either way, and a `UrlTree` would be one more navigation re-running these same guards. It matters most on `sessionGuard`, where `SessionStore` has dropped its bootstrap memo — an unlatched run would re-issue `POST api/users/me` and repopulate the store for the user who just left. On `adminCanMatch`, declining the route is exactly right: the wildcard leads to `/chat`, whose guards are latched off too, and fetching the admin chunk on the way out would be the only wrong answer.

### 11.5 Cross-tab

MSAL's `cacheLocation` is `sessionStorage`, which is deliberate — a token must not outlive the tab on a shared machine — but it is also **per tab**. Each tab holds its own token cache and its own populated stores, so signing out in one otherwise leaves every other window fully signed in. The criteria are written for a single tab; the story ("the next person sees nothing of mine") is not, and a second window left open is the ordinary way that story fails in an office.

[`SessionChannel`](../../enterprise-gpt-ui/src/app/core/session/session-channel.ts) is a `BroadcastChannel` carrying one message. **`BroadcastChannel` rather than a `storage` event**, because the `storage` event fires only for `localStorage` — precisely the store US-205 requires sign-out to leave alone.

A receiving tab latches, hides the startup shell, dispatches `signedOut`, wipes its cache and leaves for `/signed-out`. Two details:

- **It never broadcasts in response.** Receiving is one-way and terminal, so there is no echo to guard against and no way for two tabs to bounce a sign-out between them. A spec asserts it.
- **It hides the startup shell explicitly.** The broadcast can land mid-navigation, and a guard latching off *cancels* that navigation — which `provideStartupShellHandoff` deliberately does not treat as its cue (§8). Without the explicit call, the "Starting Enterprise GPT…" card stays stacked above the interstitial for as long as the wipe takes, because the shell is a block in normal flow rather than an overlay.

`AuthService` is constructed eagerly through `provideAppInitializer`, and that is load-bearing rather than tidy: its constructor is what subscribes this tab to another tab's sign-out. Left lazy, the cross-tab teardown would exist only on routes that happen to inject the service — and US-301 has since deleted the temporary shell bar that used to guarantee one, so the eager construction is now the *only* thing keeping it wired.

### 11.6 What survives, and how that is enforced

The criterion is that after sign-out `localStorage` holds the theme and sidebar preferences and **nothing else**. That is impossible to review one call site at a time, so it is not reviewed that way:

- [`core/storage/local-preferences.ts`](../../enterprise-gpt-ui/src/app/core/storage/local-preferences.ts) is the **only** place in the application that touches `localStorage`, and `PREFERENCE_KEYS` enumerates the two keys that may survive. `THEME_STORAGE_KEY` moved here out of `theme-service.ts` as `PREFERENCE_KEYS.theme`, and `scripts/check-tokens.mjs` — which compares that key against the pre-paint script in `index.html` — follows it.
- `scripts/check-forbidden-apis.mjs` gained a fourth pattern banning `localStorage.setItem` anywhere else, so the criterion holds for stories not yet written rather than only for today's code. Writes only: reading cannot leave a user's data on a shared machine, and the pre-paint script legitimately reads the theme before any module exists.

The rule that governs what may be added: `localStorage` survives sign-out by design, so it holds only values **about the browser, not about the user**. Anything identifying, anything fetched from the API, and anything a second person on a shared machine should not see belongs in `sessionStorage` or in a store.

**Sign-out deliberately sweeps nothing.** A spec plants a disallowed key, signs out, and asserts it is still there — the surprising half of the criterion, and the reason the build gate rather than a runtime cleanup is the enforcement point. A cleanup pass would make the gate look optional and would quietly delete a key some future story meant to keep.

### 11.7 Cancelling work, not just clearing it

`withResetOnSignOut` empties a store; it cannot cancel a request already on the wire ([Frontend Foundation §5.5](frontend-foundation.md#55-withresetonsignout--compose-it-last)). Every store composes `takeUntil(injectSignedOut())` for that, and that is enough for the stores that exist today.

It is **not** enough for EP-4's streaming turn. `takeUntil` stops *delivery* while leaving the HTTP connection open and the model generating on the server; only aborting the underlying `fetch` ends the request. `injectSignedOutAbort()` is the primitive for that path:

```ts
const signedOutSignal = injectSignedOutAbort();       // in an injection context

fetch(url, { signal: AbortSignal.any([stop.signal, signedOutSignal()]) });
```

It is a **factory** rather than one shared signal, so two concurrent requests have signals that can be told apart — a store cancelling one turn must not abort the other. `AbortSignal.any` is also what keeps it from accumulating a controller per request: the DOM Standard holds a signal's dependent signals in a **weak set**, so each is collectable as soon as the caller drops it and a thousand-turn session leaves nothing behind. A signal minted *after* the session ended is already aborted, which is correct rather than incidental — sign-in here is always a redirect, so a session never resumes within the same document — and it surfaces as an `aborted` `AppError`, which raises no toast.

### 11.8 The screens

| Surface | Where | Notes |
| --- | --- | --- |
| `<app-user-footer>` | [`shared/nav/user-footer/`](../../enterprise-gpt-ui/src/app/shared/nav/user-footer/) | Frame `3a`: initials avatar, display name, theme toggle, sign out. Since US-301 it sits in the sidebar footer, where it needs no session gate — the shell renders only behind `sessionGuard`. Its `compact` input stacks it into the collapsed 60 px strip ([Shell and Navigation §5.3](shell-and-navigation.md#53-two-widths-two-template-branches)) |
| `<app-signing-out>` | [`shared/feedback/signing-out/`](../../enterprise-gpt-ui/src/app/shared/feedback/signing-out/) | The full-page interstitial that replaces the routed area while sign-out runs |
| `/signed-out` | [`features/auth/signed-out/`](../../enterprise-gpt-ui/src/app/features/auth/signed-out/) | Unguarded landing page. One "Sign in again" control, latched |

**The footer asks for no confirmation**, matching the board. Nothing is lost by signing out — conversations are on the server — so a dialog would be friction guarding nothing. The button latches disabled off `isSigningOut()`, so the second press has nothing to press rather than merely being ignored. The avatar is `aria-hidden`: the initials restate the name sitting immediately beside them.

**The interstitial is a component rather than markup inline in `App`**, for the reason `focusOnRender` requires: the heading only exists once the `@if` instantiates it, so a `viewChild.required` in `App` would resolve to nothing at `afterNextRender` time. Without it, the button the user just pressed is removed from the DOM and focus lands on `<body>` — silence for a screen reader and a tab order restarting from the top. It is deliberately a **full page rather than an overlay**: a blocked navigation leaves it up for MSAL's 30-second timeout (class C above), and a frozen shell behind a spinner reads as a hang where an honest full-page state reads as work in progress. It is **not routed** — a navigation is precisely what sign-out must not do.

**`/signed-out` is unguarded, and that is the point rather than a convenience** (§11.3). Its "Sign in again" falls back to a hard reload when `signIn` rejects, because MSAL takes its interaction lock before navigating and this page — unlike `/login-failed` — has run no handshake that could clear it.

### 11.9 Known gaps

None of these blocks the story; each is the kind of thing that costs an hour when met cold.

| Gap | Detail |
| --- | --- |
| Class C leaves the interstitial up for up to 30 s | A blocked redirect rejects only at `redirectNavigationTimeout`, and the fallback runs after that. **Do not lower it** — the same setting governs sign-in, where 30 s is the right budget for a slow authorize round trip |
| `egpt.authAttempts` is cleared on sign-out | The signed-out subscription drops the inconclusive-handshake counter along with the return URL, so a tenant broken badly enough to loop (§4.4) restarts its count after a sign-out attempt. The counter is per sign-in attempt and sign-out ends the one it was counting, so this is deliberate — but it does mean the loop protection is not cumulative across a sign-out |
| The admin chunk stays in the module registry | US-203 is a statement about the **network**, and it holds: the chunk is never *requested* for a user who lacks the grant. A chunk already downloaded by an administrator stays resident in the JavaScript module registry until the document is replaced — which sign-out does, on both its paths |
| `/signed-out` must be registered in Entra | `postLogoutRedirectUri` now points at it, and Entra validates post-logout redirect URIs against the app registration. Without the entry Entra shows its own signed-out page instead, which degrades gracefully but is not this application's |

## 12. Security posture, and the bundle

### 12.1 Posture

The security-relevant MSAL settings are fixed in `buildMsalConfig` and deliberately **absent from `config.json`**, so no deployment can weaken them by editing one file. Only the four Entra values — client id, authority, redirect URI, post-logout redirect URI — are environment-specific.

| Decision | Why | Cost |
| --- | --- | --- |
| `cacheLocation: sessionStorage` | PRD §5: no token in `localStorage`, which since US-205 holds the theme and sidebar state and nothing else, enforced by a build gate (§11.6). Tokens must not outlive the tab on a shared machine | A second tab has no token cache and re-establishes through Entra. A top-level redirect works where third-party cookies are blocked, so it is fast — but it does show the startup shell for a beat. It is also why sign-out has to be broadcast across tabs (§11.5) |
| `allowRedirectInIframe: false` | A sign-in redirect inside a frame is either a bug or a clickjacking attempt | — |
| `piiLoggingEnabled: false`, silent logger callback | That channel carries account identifiers and token claims, which this application never logs — and MSAL's messages are diagnostic noise a user may screenshot into a support ticket | Less MSAL detail when debugging; raise `logLevel` locally if you need it |
| Return URL validated | It survives a round trip through the browser and Entra; an absolute or protocol-relative value is an open redirect | — |
| `replaceUrl` on the auth callback | A consumed authorization code stays out of history and out of any URL that gets logged or shared | — |
| No password field, anywhere | Every credential is Entra's | — |
| Sign-out lands on `/signed-out`, unguarded | A failed end-session leaves the Entra cookie live, so a guarded landing page would silently re-authenticate whoever walks up next (§11.3) | One extra press to come back |
| One permitted `localStorage` call site | The "nothing of mine survives" criterion is enforced at build time rather than reviewed per call site (§11.6) | A new preference needs an entry in `PREFERENCE_KEYS` |

### 12.2 Bundle re-baseline

**MSAL is 248.9 kB of the initial graph** — `msal-browser` 148.7 kB plus `msal-common` 100.2 kB — not the ~150 kB the PRD estimated. The estimate counted `msal-browser` alone and missed its peer.

| | Initial raw | Initial transfer | Budget (warn / error) |
| --- | --- | --- | --- |
| End of EP-1 (US-106) | 349.83 kB | 88.13 kB | 360 kB / 400 kB |
| End of US-203 | 629.33 kB | 151.96 kB | 650 kB / 720 kB |
| **End of EP-2 (US-205)** | **637.10 kB** | **153.38 kB** | **650 kB / 720 kB** |
| End of EP-3 (US-303) | 643.62 kB | 158.05 kB | 660 kB / 720 kB |
| After EP-6's US-601/602/606 | 660.24 kB | 161.01 kB | 665 kB / 720 kB |
| Current, after EP-5 and US-603 | 661.36 kB | 161.19 kB | 665 kB / 720 kB |

US-204 and US-205 cost 7.77 kB between them — the drop directive and overlay, the session channel, the storage allowlist, the navigation seam, and three components — so the budget set at US-203 still holds and **no re-baseline was needed**. None of the MSAL cost is deferrable: `main.ts` must create and initialize the instance before bootstrap, so it is unavoidably initial. The `styles` bundle budget is unchanged at 65 kB warn / 80 kB error, and the global stylesheet still compiles to 60.32 kB — the new components carry their own scoped styles.

The PRD's §9 open question on the initial-bundle budget is re-stated with these figures. Everything after EP-2 has moved only the *warning* threshold, because both the shell and the chat route are lazy — EP-3 added 6.52 kB, and EP-6's markdown renderer added none of its 124.8 kB, riding the chat chunk instead ([Shell and Navigation §9](shell-and-navigation.md#9-bundle-and-budgets), [Answer Rendering §6](answer-rendering.md#6-the-bundle-gate-resolved)). MSAL remains the largest single item in the initial graph.

## 13. What is not here yet

| Missing | Owner | Notes |
| --- | --- | --- |
| The composer that consumes US-204 | US-401 / **US-801** | `[appFileDropTarget]` and `@if (session.canUploadFiles())` are both ready; wiring them is the two-line snippet in §2.4. The frame `2h` acceptance assertion re-runs against the real composer |
| The project files panel's drop zone | US-902 | Renders the same `<app-drop-overlay>` |
| The streaming turn that `injectSignedOutAbort()` exists for | EP-4 | US-205's third criterion is met today for every store that exists, through `takeUntil(injectSignedOut())`; the abort primitive is what the raw-`fetch` path will use (§11.7) |
| The project load in the bootstrap | EP-9 | Add it to `SessionBootstrap.ensureSession()`, after the awaited session, beside the conversation list EP-3 added there |
| A real chat screen | EP-4 | EP-3 built the route, its header and its empty state; the composer and the transcript are US-401 onward ([Shell and Navigation §7](shell-and-navigation.md#7-the-chat-route)) |
| A real admin area | EP-12 | `features/admin/` is one lazy route rendering "admin users", enough for the chunk-withholding gate to be real |
| Removing `@azure/msal-angular` | — | Unused; kept only because US-101 requires it installed (§3) |

## 14. Testing and verification status

The suite was **550 specs** at the end of EP-2, all green, with `npm run lint` and `npm run build` green alongside them. (It stands at 610 after EP-3 — [Shell and Navigation §10](shell-and-navigation.md#10-testing).)

| Area | Specs | Notable cases |
| --- | --- | --- |
| Handshake | `auth-service` | Memoization across callers; the inconclusive counter; open-redirect return URLs; blocked `sessionStorage`; live MSAL state read rather than the memoized redirect result |
| Sign-out | `auth-service` (`signOut`) | The latch before the redirect; the account captured before the cache is cleared; a second press ignored; the local wipe and `/signed-out` landing when Entra is unreachable; still leaving when even the wipe fails; staying latched afterwards; the handshake memo, return URL and attempt counter all dropped |
| Cross-tab | `session-channel` | A sign-out carried to another tab; unrelated traffic ignored; the receiving tab tearing down and leaving; no echo, so two tabs cannot bounce a sign-out between them |
| Abort primitive | `session-events` | Every outstanding signal aborted on sign-out; one signal per request; composition with a caller-owned Stop; already-aborted after the session ended; aborted on injector destroy |
| Tokens | `token-service` | Shared in-flight acquisition; `forceRefresh` bypassing the memo; `InteractionRequiredAuthError` → redirect + throw; a swallowed second redirect |
| Interceptor | `auth.interceptor` | Non-API URLs untouched; one 401 replay with a forced refresh; an MCP-consent 403 **not** refreshed; three retried attempts carrying `token-1/2/3` |
| Guards | `auth.guard`, `session.guard`, `admin.guard` | `UrlTree` on every failure; a rejecting `signIn`; the `RouterTestingHarness` assertion that `loadChildren` is never invoked; each guard refusing to re-activate mid-sign-out, including the admin chunk never being fetched on the way out |
| Session | `session-store` | The empty-grant session as success; the generation counter; `idle` rather than `error` on sign-out cancellation; `canUploadFiles` false for an administrator whose grants omit it |
| Shell | `app` | The latch and the emptied session in one synchronous step; the interstitial replacing the shell for the whole of sign-out; the footer appearing only once a session exists; the toast region mounted throughout; focus moved to the interstitial heading |
| Upload | `upload` | Nothing offered and nothing prevented without the grant; a file accepted with it; the overlay surviving a pointer crossing into a child; a text drag ignored; the zone going inert when the grant is revoked mid-drag; a page-background drop swallowed; an unclaimed drag marked "no drop" |
| Preferences | `local-preferences` | Round trip; storage that throws; the allowlist holding only browser-level values |
| Footer | `user-footer` | Two initials and the one-word fallback; one press latching the button; the theme surviving sign-out while the session does not; a disallowed key **not** swept, because the build gate is the enforcement point |
| Auth pages | `auth-pages` | `replaceUrl` on the callback; the latching Try again; frame `6c` leaving when there is no error to explain; heading focus on all four; `/signed-out` stating what was cleared, latching its sign-in, and reloading when the redirect cannot start |

```bash
# from enterprise-gpt-ui/
npm run lint    # ESLint + check:icons + check:forbidden + check:tokens
npm test        # Vitest, single run — 550 specs at the end of this epic
npm run build   # budgets, then check-initial-chunk.mjs (which covers features/admin/)
```

> **Verification status, stated honestly.** The three gates above are green. **No part of this epic has been exercised against a real Entra tenant** — not the sign-in redirect round trip, not the token audience, not `POST api/users/me`, and, since US-205, not the end-session round trip or the `/signed-out` landing either. The API could not be started locally to check the flow end to end: it crashes at startup in `CreateDatabaseIfNotExistsAsync` because Cosmos DB is not running in this environment. Treat all of it as unverified against a live tenant until someone runs it, and note that `/signed-out` also needs registering as a post-logout redirect URI in the app registration before that first run (§11.9). Since EP-3 the first screen after sign-in is the real shell, so that run now exercises the conversation list against the API as well.

## 15. Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Every sign-in attempt fails until the tab is closed | MSAL's `interaction_in_progress` lock, left by a reload mid-redirect. `AuthService.ready()` clears it — check that the screen you are on calls it (§4.3) |
| Signing in succeeds, then bounces to `/login-failed` | A consumed redirect being replayed. This is defect 2 in §3; if you see it, something has reintroduced `MsalService` |
| `/auth` → `/chat` → Entra → `/auth`, forever | An inconclusive handshake. The second occurrence terminates on `/login-failed` by design (§4.4); look at the Entra response, not the client |
| "Application not found" from Entra | A wrong `clientId` or `authority` in `config.json`. Both are validated at load, so check the file the environment actually served — not the committed copy |
| The redirect URI is rejected | Under a sub-path deployment, the `/`-prefixed values in `config.json` resolve against the origin rather than `<base href>` (§4.1) |
| A new tab asks Entra again | Expected: the token cache is `sessionStorage` (§12.1) |
| Requests reach the API with no `Authorization` header | The URL did not come from `ApiUrl.build()`, so `ApiUrl.owns()` returned false (§5.3) |
| A 403 puts the client in a refresh loop | Something bypassed `authErrorDecision`. `mcp-authorization-required` must never refresh (§5.4) |
| Blank page after sign-in | A guard returned `false` instead of a `UrlTree` (§6.1) |
| The build fails naming `features/admin/` | A static import reached the admin area. The error names the import path (§9) |
| The build fails naming a `localStorage` write | Route it through `core/storage/local-preferences.ts` and add the key to `PREFERENCE_KEYS` — but read §11.6's rule first; most values do not belong there at all |
| An empty shell flashes before the app | Something removed the startup shell early, or `withEnabledBlockingInitialNavigation()` was dropped (§8) |
| "Signing you out…" sits there for half a minute, then lands on `/signed-out` | The redirect was blocked, so MSAL's promise rejects only at its 30-second timeout — class C in §11.2. Do **not** lower `redirectNavigationTimeout`; it also shortens sign-in |
| Sign-out completes and Entra shows *its* signed-out page, not ours | `/signed-out` is not registered as a post-logout redirect URI in the app registration (§11.9) |
| Signing out immediately signs the user back in | Something navigated during teardown. Check the latch is still the first line of all three guards (§11.4) |
| Another tab stays signed in | `BroadcastChannel` is unavailable in a few hardened configurations, and `SessionChannel` degrades silently by design. Single-tab sign-out still works everywhere (§11.5) |
| A drop zone renders and does nothing | Almost always a missing `preventDefault()` on `dragover` — without it the element never becomes a drop target and `drop` never fires (§10.1) |
| A file dropped on a native file input is refused | The window suppressor cannot see the browser's own drop handling, so it marks it "no drop". This is the documented constraint in §10.2 |

## 16. Key files

| Concern | File |
| --- | --- |
| Bootstrap order | [`src/main.ts`](../../enterprise-gpt-ui/src/main.ts) |
| MSAL configuration and the rejection rationale | [`core/auth/msal-instance.ts`](../../enterprise-gpt-ui/src/app/core/auth/msal-instance.ts) |
| Handshake, sign-in, sign-out, return URL | [`core/auth/auth-service.ts`](../../enterprise-gpt-ui/src/app/core/auth/auth-service.ts) |
| Access tokens | [`core/auth/token-service.ts`](../../enterprise-gpt-ui/src/app/core/auth/token-service.ts) |
| Bearer attachment and the 401 replay | [`core/auth/interceptors/auth.interceptor.ts`](../../enterprise-gpt-ui/src/app/core/auth/interceptors/auth.interceptor.ts) |
| Guards | [`core/auth/guards/auth.guard.ts`](../../enterprise-gpt-ui/src/app/core/auth/guards/auth.guard.ts), [`session.guard.ts`](../../enterprise-gpt-ui/src/app/core/auth/guards/session.guard.ts), [`admin.guard.ts`](../../enterprise-gpt-ui/src/app/core/auth/guards/admin.guard.ts) |
| Route table and guard nesting | [`src/app/app.routes.ts`](../../enterprise-gpt-ui/src/app/app.routes.ts) |
| Interceptor order | [`src/app/app.config.ts`](../../enterprise-gpt-ui/src/app/app.config.ts) |
| Session state | [`core/session/session-store.ts`](../../enterprise-gpt-ui/src/app/core/session/session-store.ts) |
| Bootstrap sequencing | [`core/session/session-bootstrap.ts`](../../enterprise-gpt-ui/src/app/core/session/session-bootstrap.ts) |
| Cross-tab teardown | [`core/session/session-channel.ts`](../../enterprise-gpt-ui/src/app/core/session/session-channel.ts) |
| Session events and the abort primitive | [`core/events/session-events.ts`](../../enterprise-gpt-ui/src/app/core/events/session-events.ts) |
| `localStorage` allowlist | [`core/storage/local-preferences.ts`](../../enterprise-gpt-ui/src/app/core/storage/local-preferences.ts) |
| Document replacement seam | [`core/navigation/hard-navigation.ts`](../../enterprise-gpt-ui/src/app/core/navigation/hard-navigation.ts) |
| Catalogs | [`core/catalog/model-catalog-store.ts`](../../enterprise-gpt-ui/src/app/core/catalog/model-catalog-store.ts), [`mcp-catalog-store.ts`](../../enterprise-gpt-ui/src/app/core/catalog/mcp-catalog-store.ts) |
| Permission ids | [`domain/api/permission-ids.ts`](../../enterprise-gpt-ui/src/app/domain/api/permission-ids.ts) |
| Auth pages | [`features/auth/`](../../enterprise-gpt-ui/src/app/features/auth/) |
| Sign-out chrome and interstitial | [`shared/nav/user-footer/`](../../enterprise-gpt-ui/src/app/shared/nav/user-footer/), [`shared/feedback/signing-out/`](../../enterprise-gpt-ui/src/app/shared/feedback/signing-out/) |
| Drop zone, overlay, window suppressor | [`shared/upload/file-drop-target.ts`](../../enterprise-gpt-ui/src/app/shared/upload/file-drop-target.ts), [`shared/upload/drop-overlay.ts`](../../enterprise-gpt-ui/src/app/shared/upload/drop-overlay.ts), [`core/dnd/prevent-drop-navigation.ts`](../../enterprise-gpt-ui/src/app/core/dnd/prevent-drop-navigation.ts) |
| Drag test shims | [`src/testing/drag.ts`](../../enterprise-gpt-ui/src/testing/drag.ts), [`src/testing/navigation.ts`](../../enterprise-gpt-ui/src/testing/navigation.ts) |
| Startup handoff and watchdog | [`core/config/startup-shell-handoff.ts`](../../enterprise-gpt-ui/src/app/core/config/startup-shell-handoff.ts), [`src/index.html`](../../enterprise-gpt-ui/src/index.html) |
| Admin-chunk and `localStorage` gates | [`scripts/check-initial-chunk.mjs`](../../enterprise-gpt-ui/scripts/check-initial-chunk.mjs), [`scripts/check-forbidden-apis.mjs`](../../enterprise-gpt-ui/scripts/check-forbidden-apis.mjs) |
| Related reference | [Frontend Foundation](frontend-foundation.md), [Shell and Navigation](shell-and-navigation.md), [Design System](design-system.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md), [User Management](../users/user-management.md) |
