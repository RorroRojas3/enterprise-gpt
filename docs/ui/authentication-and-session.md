# Authentication and Session

How the rebuilt Angular client at `enterprise-gpt-ui/` signs a user in with Microsoft Entra ID, gets an access token onto every API call, learns who the user is and what they may do, and keeps the administration code off a non-administrator's device.

Audience: a developer about to write their first guarded screen, add a permission check, or debug a redirect loop. Read [Frontend Foundation](frontend-foundation.md) first if you have not — this page builds directly on that document's runtime configuration, its `AppError` union, and its composable store features. Bare `§` references below are to sections of *this* page.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference below.

## 1. Overview

EP-2 is what turns the EP-1 foundation into an application a person can be *in*. Three stories landed:

| Story | What it delivers |
| --- | --- |
| **US-201** | Sign-in with Entra ID over the MSAL redirect flow, the four full-page auth states, and one `TokenService` both transports draw from |
| **US-202** | `POST api/users/me` resolved before any screen activates, holding the user and their grants, followed by the model and MCP catalogs |
| **US-203** | The admin route chunk withheld from a non-administrator's browser rather than hidden from their sidebar |

> **Two EP-2 stories are not implemented.** **US-204** (hiding upload affordances behind the `Upload File` grant) has its policy in place — `SessionStore.canUploadFiles` exists and is deliberately independent of `isAdministrator` — but no upload control exists yet to gate, so the story lands with the composer in EP-8. **US-205** (sign-out) is not started: `sessionEvents.signedOut` is defined and every store already resets on it, but nothing dispatches it and there is no sign-out control. See §11.

Four things in this epic are worth knowing *before* you touch it, because each is a decision that looks arbitrary from the outside:

1. **`@azure/msal-angular` is installed and used nowhere.** Three verified defects make it unusable in a zoneless app that needs a `canMatch` guard (§3).
2. **Guards in one `canActivate` array run concurrently.** Nesting is the only way to order two of them, which is why the authenticated route tree is two levels deep with one guard each (§6).
3. **The startup shell is removed on the first settled navigation, not when bootstrap resolves** — otherwise an empty app shell paints while guards are still deciding (§8).
4. **MSAL costs 248.9 kB of the initial bundle**, not the ~150 kB the PRD estimated, and the budgets moved accordingly (§10).

### 1.1 Where each piece lives

| Concern | Where |
| --- | --- |
| MSAL instance token and configuration | [`core/auth/msal-instance.ts`](../../enterprise-gpt-ui/src/app/core/auth/msal-instance.ts) |
| Creating and initializing MSAL before bootstrap | [`src/main.ts`](../../enterprise-gpt-ui/src/main.ts) |
| The redirect handshake, sign-in, return URL | [`core/auth/auth-service.ts`](../../enterprise-gpt-ui/src/app/core/auth/auth-service.ts) |
| Access tokens | [`core/auth/token-service.ts`](../../enterprise-gpt-ui/src/app/core/auth/token-service.ts) |
| Attaching the bearer token to `HttpClient` | [`core/auth/interceptors/auth.interceptor.ts`](../../enterprise-gpt-ui/src/app/core/auth/interceptors/auth.interceptor.ts) |
| Guards | [`core/auth/guards/`](../../enterprise-gpt-ui/src/app/core/auth/guards/) — `auth.guard.ts`, `session.guard.ts`, `admin.guard.ts` |
| Route path constants | [`core/auth/auth-routes.ts`](../../enterprise-gpt-ui/src/app/core/auth/auth-routes.ts) |
| Who is signed in, and their grants | [`core/session/session-store.ts`](../../enterprise-gpt-ui/src/app/core/session/session-store.ts) |
| Bootstrap sequencing | [`core/session/session-bootstrap.ts`](../../enterprise-gpt-ui/src/app/core/session/session-bootstrap.ts) |
| Models and MCP servers | [`core/catalog/`](../../enterprise-gpt-ui/src/app/core/catalog/) — `model-catalog-store.ts`, `mcp-catalog-store.ts` |
| Permission ids | [`domain/api/permission-ids.ts`](../../enterprise-gpt-ui/src/app/domain/api/permission-ids.ts) |
| The four full-page auth states | [`features/auth/`](../../enterprise-gpt-ui/src/app/features/auth/) |
| Startup-shell handoff | [`core/config/startup-shell-handoff.ts`](../../enterprise-gpt-ui/src/app/core/config/startup-shell-handoff.ts) |
| Route table and guard nesting | [`src/app/app.routes.ts`](../../enterprise-gpt-ui/src/app/app.routes.ts) |
| Admin-chunk build gate | [`scripts/check-initial-chunk.mjs`](../../enterprise-gpt-ui/scripts/check-initial-chunk.mjs) |

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

### 4.2 The four routes

| Route | Frame | Guarded | Purpose |
| --- | --- | --- | --- |
| `/auth` | `6a`, `6f` in dark | **No** | The MSAL redirect URI. Resolves the handshake, then navigates onward with `replaceUrl` |
| `/login-failed` | `6b` | **No** | Sign-in did not complete. One "Try again" control; **no password field, here or anywhere** |
| `/session-error` | `6c` | **No** | Signed in, but `POST api/users/me` failed. Shows the `traceId` and a Retry |
| `/forbidden` | `6e` | Yes | Signed in, not an administrator. One "Back to Chat" action |

**The first three are unguarded deliberately, and it is not an oversight.** `/auth` *is* the redirect URI: a guard there would either recurse or block the very handshake it waits on, and an async guard would hold the navigation open and suppress the interstitial the acceptance criterion requires to be on screen. `/login-failed` and `/session-error` have to render precisely when sign-in or the session cannot succeed — guarding either turns a recovery into a loop.

All four share `AuthPage`, a centred chrome-free frame, and each focuses its own heading on render through `focusOnRender`. Angular moves focus on neither a router navigation nor a guard redirect, so without it a keyboard user resumes tabbing from the top of the document — and in the `/admin` → `/forbidden` case the element that *had* focus is removed from the DOM outright, dropping focus onto `<body>`. Focusing the heading rather than announcing through a live region is the same choice `fatal-shell.ts` makes: a region inserted together with its text announces nothing, because `aria-live` reports *changes* to a region already in the accessibility tree.

Frame `6f` needs no code of its own. `index.html`'s pre-paint script has already set `data-bs-theme` before any stylesheet loads (see [Design System §3](design-system.md#3-theming-and-the-pre-paint-contract)), so a dark-mode user sees the dark interstitial rather than a light flash.

### 4.3 The handshake, memoized

`AuthService.ready()` resolves `handleRedirectPromise` **once per page load**, however many callers ask, and returns `{ account, error }`.

```ts
const { account, error } = await inject(AuthService).ready();
```

Memoizing is not a micro-optimization. The handshake is the operation that **clears MSAL's `interaction_in_progress` lock**, so it has to run on every page load and not only on `/auth`: a user who reloads the tab mid-redirect leaves that lock set, and every later sign-in attempt fails until the tab is closed. When no interaction is outstanding it resolves in microseconds. `/login-failed` calls `ready()` in `ngOnInit` for exactly this reason — it is the one screen no guard has resolved the handshake for, and without the call its **Try again** button throws against a lock nothing will clear.

The memo is cleared on `sessionEvents.signedOut`. It holds an `AccountInfo`, which is the same class of per-session handle the stores clear — and `withResetOnSignOut` reaches none of it, because it is a field on a service rather than store state. Left alone, a guard consulting `ready()` after sign-out would keep activating for the departed user until the page happened to reload.

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
        { path: 'chat', loadComponent: /* … */ },
        { path: 'forbidden', component: Forbidden },
        { path: 'admin', canMatch: [adminCanMatch], loadChildren: /* … */ },
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

> **Scoped deviation from US-202.** The story's wording also lists conversations and projects among what the bootstrap requests. Those belong to EP-3 and EP-9, which own their stores; adding placeholder loads here would either duplicate that work or constrain it. `SessionBootstrap` exists precisely so those two epics have one obvious place to add their calls — the guard already delegates to it rather than reaching into stores itself.

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

## 10. Security posture, and the bundle

### 10.1 Posture

The security-relevant MSAL settings are fixed in `buildMsalConfig` and deliberately **absent from `config.json`**, so no deployment can weaken them by editing one file. Only the four Entra values — client id, authority, redirect URI, post-logout redirect URI — are environment-specific.

| Decision | Why | Cost |
| --- | --- | --- |
| `cacheLocation: sessionStorage` | PRD §5: no token in `localStorage`, which holds the theme and sidebar state and nothing else. Tokens must not outlive the tab on a shared machine | A second tab has no token cache and re-establishes through Entra. A top-level redirect works where third-party cookies are blocked, so it is fast — but it does show the startup shell for a beat |
| `allowRedirectInIframe: false` | A sign-in redirect inside a frame is either a bug or a clickjacking attempt | — |
| `piiLoggingEnabled: false`, silent logger callback | That channel carries account identifiers and token claims, which this application never logs — and MSAL's messages are diagnostic noise a user may screenshot into a support ticket | Less MSAL detail when debugging; raise `logLevel` locally if you need it |
| Return URL validated | It survives a round trip through the browser and Entra; an absolute or protocol-relative value is an open redirect | — |
| `replaceUrl` on the auth callback | A consumed authorization code stays out of history and out of any URL that gets logged or shared | — |
| No password field, anywhere | Every credential is Entra's | — |

### 10.2 Bundle re-baseline

**MSAL is 248.9 kB of the initial graph** — `msal-browser` 148.7 kB plus `msal-common` 100.2 kB — not the ~150 kB the PRD estimated. The estimate counted `msal-browser` alone and missed its peer.

| | Initial raw | Initial transfer | Budget (warn / error) |
| --- | --- | --- | --- |
| End of EP-1 (US-106) | 349.83 kB | 88.13 kB | 360 kB / 400 kB |
| **End of EP-2 (US-203)** | **629.33 kB** | **151.96 kB** | **650 kB / 720 kB** |

None of it is deferrable: `main.ts` must create and initialize the instance before bootstrap, so MSAL is unavoidably initial. The `styles` bundle budget is unchanged at 65 kB warn / 80 kB error.

The PRD's §9 open question on the initial-bundle budget is re-stated with these figures.

## 11. What is not here yet

| Missing | Owner | Notes |
| --- | --- | --- |
| Upload affordances gated on `Upload File` | **US-204** | `SessionStore.canUploadFiles` is in place and independent of `isAdministrator`; there is no upload control to gate until the composer lands (EP-8) |
| Sign-out | **US-205** | `sessionEvents.signedOut` is defined and every store, `AuthService` and each bootstrap memo already reset on it — but nothing dispatches it and there is no control. When it lands, dispatch it with `{ scope: 'global' }` (see [Frontend Foundation §5.6](frontend-foundation.md#56-cross-store-events)) and call `logoutRedirect` |
| Conversation and project loads in the bootstrap | EP-3 / EP-9 | Add them to `SessionBootstrap.ensureSession()`, after the awaited session |
| A real chat screen | EP-3 / EP-4 | `features/chat/chat.ts` is a placeholder that renders session and catalog state so a live sign-in can be eyeballed. It is replaced entirely |
| A real admin area | EP-12 | `features/admin/` is one lazy route rendering "admin users", enough for the chunk-withholding gate to be real |
| Removing `@azure/msal-angular` | — | Unused; kept only because US-101 requires it installed (§3) |

## 12. Testing and verification status

The suite is **495 specs**, all green, with `npm run lint` and `npm run build` green alongside them.

| Area | Specs | Notable cases |
| --- | --- | --- |
| Handshake | `auth-service` | Memoization across callers; the inconclusive counter; open-redirect return URLs; blocked `sessionStorage`; the sign-out reset |
| Tokens | `token-service` | Shared in-flight acquisition; `forceRefresh` bypassing the memo; `InteractionRequiredAuthError` → redirect + throw; a swallowed second redirect |
| Interceptor | `auth.interceptor` | Non-API URLs untouched; one 401 replay with a forced refresh; an MCP-consent 403 **not** refreshed; three retried attempts carrying `token-1/2/3` |
| Guards | `auth.guard`, `session.guard`, `admin.guard` | `UrlTree` on every failure; a rejecting `signIn`; the `RouterTestingHarness` assertion that `loadChildren` is never invoked |
| Session | `session-store` | The empty-grant session as success; the generation counter; `idle` rather than `error` on sign-out cancellation |
| Auth pages | `auth-pages` | `replaceUrl` on the callback; the latching Try again; frame `6c` leaving when there is no error to explain; heading focus |

```bash
# from enterprise-gpt-ui/
npm run lint    # ESLint + check:icons + check:forbidden + check:tokens
npm test        # Vitest, single run — 495 specs
npm run build   # budgets, then check-initial-chunk.mjs (which now covers features/admin/)
```

> **Verification status, stated honestly.** The three gates above are green. **The interactive browser sign-in against a real Entra tenant has not been exercised**, and the API could not be started locally to check the flow end to end — it crashes at startup in `CreateDatabaseIfNotExistsAsync` because Cosmos DB is not running in this environment. Treat the redirect round trip, the token audience, and `POST api/users/me` against a live tenant as unverified until someone runs them. The `features/chat` placeholder renders session and catalog state specifically so that first live run needs no devtools session.

## 13. Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Every sign-in attempt fails until the tab is closed | MSAL's `interaction_in_progress` lock, left by a reload mid-redirect. `AuthService.ready()` clears it — check that the screen you are on calls it (§4.3) |
| Signing in succeeds, then bounces to `/login-failed` | A consumed redirect being replayed. This is defect 2 in §3; if you see it, something has reintroduced `MsalService` |
| `/auth` → `/chat` → Entra → `/auth`, forever | An inconclusive handshake. The second occurrence terminates on `/login-failed` by design (§4.4); look at the Entra response, not the client |
| "Application not found" from Entra | A wrong `clientId` or `authority` in `config.json`. Both are validated at load, so check the file the environment actually served — not the committed copy |
| The redirect URI is rejected | Under a sub-path deployment, the `/`-prefixed values in `config.json` resolve against the origin rather than `<base href>` (§4.1) |
| A new tab asks Entra again | Expected: the token cache is `sessionStorage` (§10.1) |
| Requests reach the API with no `Authorization` header | The URL did not come from `ApiUrl.build()`, so `ApiUrl.owns()` returned false (§5.3) |
| A 403 puts the client in a refresh loop | Something bypassed `authErrorDecision`. `mcp-authorization-required` must never refresh (§5.4) |
| Blank page after sign-in | A guard returned `false` instead of a `UrlTree` (§6.1) |
| The build fails naming `features/admin/` | A static import reached the admin area. The error names the import path (§9) |
| An empty shell flashes before the app | Something removed the startup shell early, or `withEnabledBlockingInitialNavigation()` was dropped (§8) |

## 14. Key files

| Concern | File |
| --- | --- |
| Bootstrap order | [`src/main.ts`](../../enterprise-gpt-ui/src/main.ts) |
| MSAL configuration and the rejection rationale | [`core/auth/msal-instance.ts`](../../enterprise-gpt-ui/src/app/core/auth/msal-instance.ts) |
| Handshake, sign-in, return URL | [`core/auth/auth-service.ts`](../../enterprise-gpt-ui/src/app/core/auth/auth-service.ts) |
| Access tokens | [`core/auth/token-service.ts`](../../enterprise-gpt-ui/src/app/core/auth/token-service.ts) |
| Bearer attachment and the 401 replay | [`core/auth/interceptors/auth.interceptor.ts`](../../enterprise-gpt-ui/src/app/core/auth/interceptors/auth.interceptor.ts) |
| Guards | [`core/auth/guards/auth.guard.ts`](../../enterprise-gpt-ui/src/app/core/auth/guards/auth.guard.ts), [`session.guard.ts`](../../enterprise-gpt-ui/src/app/core/auth/guards/session.guard.ts), [`admin.guard.ts`](../../enterprise-gpt-ui/src/app/core/auth/guards/admin.guard.ts) |
| Route table and guard nesting | [`src/app/app.routes.ts`](../../enterprise-gpt-ui/src/app/app.routes.ts) |
| Interceptor order | [`src/app/app.config.ts`](../../enterprise-gpt-ui/src/app/app.config.ts) |
| Session state | [`core/session/session-store.ts`](../../enterprise-gpt-ui/src/app/core/session/session-store.ts) |
| Bootstrap sequencing | [`core/session/session-bootstrap.ts`](../../enterprise-gpt-ui/src/app/core/session/session-bootstrap.ts) |
| Catalogs | [`core/catalog/model-catalog-store.ts`](../../enterprise-gpt-ui/src/app/core/catalog/model-catalog-store.ts), [`mcp-catalog-store.ts`](../../enterprise-gpt-ui/src/app/core/catalog/mcp-catalog-store.ts) |
| Permission ids | [`domain/api/permission-ids.ts`](../../enterprise-gpt-ui/src/app/domain/api/permission-ids.ts) |
| Auth pages | [`features/auth/`](../../enterprise-gpt-ui/src/app/features/auth/) |
| Startup handoff and watchdog | [`core/config/startup-shell-handoff.ts`](../../enterprise-gpt-ui/src/app/core/config/startup-shell-handoff.ts), [`src/index.html`](../../enterprise-gpt-ui/src/index.html) |
| Admin-chunk gate | [`scripts/check-initial-chunk.mjs`](../../enterprise-gpt-ui/scripts/check-initial-chunk.mjs) |
| Related reference | [Frontend Foundation](frontend-foundation.md), [Design System](design-system.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md), [User Management](../users/user-management.md) |
