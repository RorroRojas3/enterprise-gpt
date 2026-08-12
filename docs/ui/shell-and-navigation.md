# Shell and Navigation

How the rebuilt Angular client at `enterprise-gpt-ui/` frames a signed-in user: the layout route that owns the chrome, the sidebar that lists their conversations, the store behind that list, and the chat route that serves both `/chat` and `/chat/{conversationId}` from one configuration.

Audience: a developer about to add a per-row action to the conversation list (US-304 is next), a second list store anywhere in the app, or a screen inside the shell. Read [Frontend Foundation](frontend-foundation.md) first for the store features every store here composes, and [Authentication and Session](authentication-and-session.md) for the guards this shell sits inside. Bare `§` references below are to sections of *this* page.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference.

## 1. Overview

EP-3 turns the guarded route tree into an application with a shape. Three stories landed together, and US-302 carried the shell with it — no story owned the frame itself, and the sidebar has to live somewhere:

| Story | What it delivers |
| --- | --- |
| **US-302** | The conversation list, `ConversationListStore`, and the shell and sidebar that host it |
| **US-301** | Collapse to a 60 px icon strip, remembered across reloads in `UiStore` |
| **US-303** | The empty chat state a new conversation opens on — which creates nothing |

**`ConversationListStore` is the reference list store.** Fifteen more are written against its shape, and the PRD's risk table requires it be reviewed as a *pattern decision* before any of them exists. §4 is therefore the longest section here and the one to read before writing a store; four of its choices are decisions rather than details, and each one looks removable until you know what it prevents.

Six things are worth knowing before you touch this epic:

1. **The list loads declaratively.** One `rxMethod` is wired in `onInit` to a computation over two pieces of state; nothing else calls it. `SessionBootstrap` only flips a flag (§4.2).
2. **Two separate rules stop a stale page landing in a fresh result set**, and both are needed — a subject that cancels a superseded "Load more", and a refusal to start one while a query is in flight (§4.4).
3. **`refreshRow` and `prependNewest` are not the same operation**, and a row the list does not hold is deliberately ignored rather than inserted (§4.5).
4. **One route configuration serves `/chat` and `/chat/{id}`**, through a `UrlMatcher`, so the component is reused rather than remounted — which is what US-401 needs to create a conversation mid-turn (§7.1).
5. **The shell is lazy on purpose**, keeping ~24 kB of signed-in chrome off the four unguarded auth routes (§5.1).
6. **`UiStore` does not compose `withResetOnSignOut`.** The sidebar preference is about the browser, not the user, and US-205 requires it to survive sign-out (§6).

### 1.1 Where each piece lives

| Concern | Where |
| --- | --- |
| The conversation list, and the reference store pattern | [`core/conversations/conversation-list-store.ts`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-list-store.ts) |
| Layout preferences that survive sign-out | [`core/ui/ui-store.ts`](../../enterprise-gpt-ui/src/app/core/ui/ui-store.ts) |
| The signed-in frame | [`features/shell/shell.ts`](../../enterprise-gpt-ui/src/app/features/shell/shell.ts), [`shell.html`](../../enterprise-gpt-ui/src/app/features/shell/shell.html), [`shell.scss`](../../enterprise-gpt-ui/src/app/features/shell/shell.scss) |
| The sidebar, both widths | [`features/shell/sidebar/sidebar.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.ts), [`sidebar.html`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.html) |
| One conversation row | [`features/shell/sidebar/conversation-row.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/conversation-row.ts) |
| The chat route's URL matcher | [`features/chat/chat-route.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat-route.ts) |
| The open conversation | [`features/chat/conversation-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/conversation-store.ts) |
| The chat screen and its empty state | [`features/chat/chat.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat.ts), [`chat-empty-state.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat-empty-state.ts) |
| Route table, matcher wiring, lazy boundaries | [`src/app/app.routes.ts`](../../enterprise-gpt-ui/src/app/app.routes.ts) |
| Releasing the list load after the session | [`core/session/session-bootstrap.ts`](../../enterprise-gpt-ui/src/app/core/session/session-bootstrap.ts) |
| The API shapes this epic reads | [`domain/api/conversation.ts`](../../enterprise-gpt-ui/src/app/domain/api/conversation.ts) |
| Whether to offer a Retry at all | [`core/errors/error-message.ts`](../../enterprise-gpt-ui/src/app/core/errors/error-message.ts) — `canRetry` (§8) |
| Test fixtures for conversations and pages | [`src/testing/conversations.ts`](../../enterprise-gpt-ui/src/testing/conversations.ts) |

### 1.2 The route tree, after EP-3

```text
''                        → redirect to /chat
/auth  /login-failed  /session-error  /signed-out      ← unguarded, chrome-free
/ui-kit                                                 ← dev only
''  canActivate: [authGuard]
 └─ ''  canActivate: [sessionGuard]
      ├─ /forbidden                                      ← a sibling of the shell, not a child
      └─ ''  loadComponent: Shell                        ← lazy
           ├─ matcher: chatMatcher   loadComponent: Chat   ← serves /chat and /chat/{id}
           └─ /admin  canMatch: [adminCanMatch]  loadChildren
```

Two placements in that tree are deliberate and easy to undo by accident.

**`/forbidden` is a sibling of the shell, not a child of it.** Frame `6e` is a full-page state with no chrome, and a sidebar around it would offer exactly the navigation the page exists to refuse.

**`adminCanMatch` stays on the route that owns `loadChildren`**, even now that the route sits inside the shell. `canMatch` still runs during URL recognition, so a non-administrator's browser never requests the chunk — moving the guard to the shell route above it would run it before the admin config was reached. See [Authentication and Session §9](authentication-and-session.md#9-administration-withheld-rather-than-hidden-us-203).

## 2. Quick start

### 2.1 Reading the conversation list

`ConversationListStore` is root-provided and already loading by the time any screen inside the shell renders — `SessionBootstrap` released it after `POST api/users/me` returned.

```ts
import { Component, inject } from '@angular/core';
import { ConversationListStore } from '@core/conversations/conversation-list-store';

@Component({ /* … */ })
export class SomeScreen {
  protected readonly conversations = inject(ConversationListStore);

  //   conversations.entities()      → readonly ConversationDto[], in the server's order
  //   conversations.isPending()     → the query is in flight
  //   conversations.error()         → AppError | null
  //   conversations.isEmpty()       → loaded, and there is genuinely nothing
  //   conversations.hasNoMatches()  → loaded, nothing matched, and a search is why
  //   conversations.hasMore()       → the server has more than has been fetched
  //   conversations.loadingMore()   → a further page is in flight
  //
  //   conversations.search('brief'); conversations.loadMore(); conversations.retry();
}
```

Root-provided rather than route-scoped, because the sidebar outlives every route in the shell and re-fetching the list on each navigation would be visible. The *route-scoped* store in this epic is `ConversationStore` (§7.2), which `Chat` provides.

### 2.2 Adding a per-row action — what US-304 does next

Three seams already exist for it, so the story is additive rather than a refactor:

1. **`ConversationRow` is its own component** precisely so the kebab, the star and per-row pending state hang off one element rather than off markup inline in the sidebar.
2. **`withPendingIds` is not composed yet.** Compose it into `ConversationListStore` — after `withOffsetPagination`, before `withProps`, and always before `withResetOnSignOut` — and read it with `isRowPending(id)`. It is absent today only because nothing has a per-row action ([Frontend Foundation §5.3](frontend-foundation.md#53-withpendingids)).
3. **`refreshRow` is the write path** for a row whose server copy has changed (§4.5). An optimistic rename patches the entity, and a failure patches the old value back.

`PUT api/conversations` replaces the whole conversation, so a rename must echo the current `projectId` — that is US-304's first acceptance criterion and US-307's `toUpdateBody()` helper is where it ends up.

### 2.3 Adding a nav entry

`Sidebar.navItems` is a `computed` over the session, and **only entries whose route exists are rendered**. The board draws five; three of them belong to EP-7, EP-9 and EP-10, and a link that redirects back to `/chat` is worse than no link. Add yours with its own epic:

```ts
{ label: 'Projects', icon: 'bi-folder', link: PROJECTS_ROUTE, restricted: false }
```

`restricted: true` renders the shield glyph frame `3a` puts beside a permission-gated entry; gate the entry itself on the permission, absent rather than disabled, as US-203 does for Admin. The icon name must exist in the sprite manifest or `npm run lint` fails ([Design System §5.3](design-system.md#53-adding-a-glyph)).

## 3. The conversation API, stated once

Everything in this epic reads two routes. Both have a sharp edge.

### 3.1 `GET api/conversations/search`

| Parameter | Client sends | Server behaviour |
| --- | --- | --- |
| `name` | **Omitted** when blank; trimmed otherwise | A `LIKE` filter. An empty `name=` still takes the server down the `LIKE` branch, which is why it is omitted rather than sent empty |
| `skip` | The offset reached so far | — |
| `take` | `SIDEBAR_PAGE_SIZE`, 50 | **Clamped to 1–100** server-side, silently. The client's own `MAX_PAGE_SIZE` mirrors that clamp |
| `isFavorite` | Not sent yet — US-305 and US-703 | `true`/`false`/absent |

**The parameter is `name`. It is never `filter`.** That was the deleted client's named regression: it sent `filter=`, the server bound nothing, and every search returned the unfiltered list looking like a working search that matched everything. A spec asserts the parameter name directly, not just that a request went out.

**Results are ordered by `dateCreated` descending — not `dateModified`.** A rename or a new turn does **not** move a row to the top. The client renders the server's order and offers no sort control, which is the PRD's regime **B**: the API accepts no sort parameter, so sorting the page in hand would put a second sorted run beneath the first ([Frontend Foundation §5.4](frontend-foundation.md#54-withclientquerysource-config)). US-706 is the enabler that would change this.

That ordering is also the *only* reason `prependNewest` is correct (§4.5). If sorting ever moves to `dateModified`, that method becomes a guess and has to go.

### 3.2 `GET api/conversations/{id}` versus `…/{id}/messages`

`ConversationDetailDto` — from `GET api/conversations/{id}` — is the only source of `mcpServerIds`, which US-410 restores into the MCP picker.

> **`GET api/conversations/{id}/messages` reports `projectId`, `modelId` and `isFavorite` as `null` / `null` / `false` regardless of the truth.** It returns a `ConversationDto`-shaped object built from the Cosmos transcript document, which does not carry those three fields. It must never be used to hydrate a project picker, a model picker or a favourite star — only `GET api/conversations/{id}` may. This is the kind of defect that looks like a state bug in the client for a day.

`listFields()` in the store exists for the mirror-image reason: `ConversationStore` legitimately hands the list a `ConversationDetailDto`, and spreading one straight into an entity would leave `mcpServerIds` on some rows and not others, so `entities()` would stop being uniformly `ConversationDto`-shaped.

## 4. `ConversationListStore` — the reference list store

### 4.1 What it composes, and in what order

```ts
export const ConversationListStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),              // name, _started, loadingMore
  withEntities<ConversationDto>(),
  withRequestStatus(),
  withOffsetPagination(SIDEBAR_PAGE_SIZE),
  withProps(/* http, toasts, url, signedOut$, _querySuperseded$ */),
  withComputed(/* hasQuery, isEmpty, hasNoMatches */),
  withMethods(/* _runQuery, _loadNextPage, and the public surface */),
  withHooks({ onInit, onDestroy }),
  withResetOnSignOut(),                 // last. Always.
);
```

That is [Frontend Foundation §5.7](frontend-foundation.md#57-composition-order)'s order, with the state-contributing features first and `withResetOnSignOut` last. **`withClientQuery` is deliberately not composed** — regime B offers no sort and the search is a server parameter rather than a client filter. **`withPendingIds` is deliberately not composed yet** — §2.2.

Three pieces of local state carry their own reasons:

| Key | Why it is not derivable |
| --- | --- |
| `name` | The `name=` filter in force. Empty means unfiltered |
| `_started` | A release sentinel. `_`-prefixed, so `@ngrx/signals` strips it from the public store type — it is not something a screen should render or reason about |
| `loadingMore` | Separate from `requestStatus` on purpose: a further page must not collapse the loaded rows back to a skeleton |

### 4.2 The declarative load, and the flag `SessionBootstrap` flips

The store issues its query from **one** place — a `rxMethod` bound in `onInit` to a computation:

```ts
withHooks({
  onInit(store) {
    store._runQuery(() => (store._started() ? store.name() : null));
  },
  onDestroy(store) {
    store._querySuperseded$.complete();
  },
});
```

`_runQuery` filters `null` out, so nothing is requested until `_started` flips; after that, every change to `name` refetches with no second call site. `search(name)` is a `patchState` and nothing more.

**Why `ensureLoaded()` only sets a flag.** A signal-valued `rxMethod` argument needs an **injection context** to bind its subscription to. `SessionBootstrap` is an injectable service calling into the store from an `async` method — there is no injection context there, and binding one to the root injector would leak the subscription past the store's own lifetime. So the bootstrap flips a boolean and the wired computation does the rest:

```ts
async ensureSession(): Promise<boolean> {
  if (!(await this._session.ensureLoaded())) return false;

  void this._models.ensureLoaded();
  void this._mcps.ensureLoaded();
  this._conversations.ensureLoaded();   // ← releases the query; not awaited

  return true;
}
```

It is released **after** `POST api/users/me` resolves rather than alongside it, for the same reason the catalogs are: the session call is what warms the API's own permission cache, and it is what establishes that a token exists at all ([Authentication and Session §7.2](authentication-and-session.md#72-sessionbootstrap--the-sequencing-is-the-point)). It is not awaited — the sidebar has a skeleton, an empty state and an error panel of its own, and holding the startup shell for a list would trade a rendered app for a spinner.

**Why `rxMethod` + `switchMap` here, when `SessionStore` uses a promise and a generation counter.** Those stores exist to be `await`ed by a guard. This one is driven by typing, where a slow response to `"ang"` must not paint over a fast response to `"angular"` — and cancelling the loser is precisely what `switchMap` does.

**`retry()` passes a static value, not a state change**, because a plain value needs no injection context and `switchMap` still cancels whatever the wired query had in flight:

```ts
retry(): void {
  _runQuery(store.name());
}
```

### 4.3 Two operators that are absent on purpose

**No `debounceTime`.** [`SearchInput`](design-system.md#76-form-navigation-and-layout) already debounces 300 ms and emits only the committed value, so a second debounce would add 300 ms of latency for nothing. Add one here only if a caller ever drives `search()` from raw keystrokes.

**No `distinctUntilChanged`.** It looks free and it would break two paths:

- `retry()` deliberately pushes the *same* name a second time. That is the whole operation.
- After sign-out resets the store, the very next query from the next user is frequently the identical one — the empty string.

The signal computation in `onInit` only emits when `_started` or `name` actually change, so for the wired path the operator would be redundant anyway. It has no upside and two failure modes.

### 4.4 The two cancellation rules, and the bug they close together

A "Load more" is a **second pipeline**. `switchMap` on the query pipeline cancels the query's own request and nothing else, so a page can land against a result set it does not belong to in two different ways. Both are closed, and neither closes the other.

**Rule 1 — `_querySuperseded$` cancels a page a later query overtook.**

```ts
_querySuperseded$: new Subject<void>(),   // in withProps, completed in onDestroy
```

The query pipeline fires it in its `tap`, before the request goes out; the load-more request is `takeUntil(merge(signedOut$, _querySuperseded$))`. Without it, a page from the previous result set is appended beneath the new one and its `setPage` advances `skip` past a window that no longer exists.

**Rule 2 — `loadMore()` refuses to start while a query is in flight.**

```ts
loadMore(): void {
  if (!store.isPending() && !store.loadingMore() && store.hasMore()) {
    _loadNextPage();
  }
}
```

`isPending()` is the load-bearing half. `_querySuperseded$` cannot help a page started *after* a query was already on the wire: that request carries the old `skip` against the new result set, and when it lands after the query's `setFirstPage` it appends a window from the middle of a list that no longer exists — **leaving a hole the user can never scroll to, with `hasMore()` reading false.** The rows look plausible. Nothing errors.

Three smaller choices in the same pipelines:

- **`exhaustMap`, not `switchMap`, on `_loadNextPage`.** A second press while a page is in flight is a double-click, not a request for the page after next.
- **`addEntities`, not `setEntities`.** A concurrent insert can shift the window and repeat a row; the copy already on screen wins.
- **`finalize` clears `loadingMore`.** It runs on cancellation too — which is exactly the case where the flag would otherwise stick, since a superseded page reaches neither `next` nor `error`.

And `takeUntil(signedOut$)` sits on both requests, because clearing state is not cancelling work ([Frontend Foundation §5.5](frontend-foundation.md#55-withresetonsignout--compose-it-last)).

### 4.5 `refreshRow` versus `prependNewest`

Two write paths, and they are not interchangeable.

| Method | For | Behaviour on a row the list does not hold |
| --- | --- | --- |
| `refreshRow(conversation)` | A row already held whose server copy changed — the title the server generates after a first turn (US-409), an edit from the conversation header (US-304, US-305) | **Ignored** |
| `prependNewest(conversation)` | A conversation *just created* by this session (US-401) | Prepends; already-held ids are skipped |

**Why an unheld row is ignored rather than inserted.** The list is a *window* onto a server-ordered result set. A conversation the window does not contain could be anywhere in that order — page three, or filtered out by the current search — and inserting it at either end would be a guess that survives until the next fetch, leaving a row in a position the server never put it in.

**Why prepending is nevertheless correct for a new one.** The server orders by `dateCreated` **descending** (§3.1), so a row created a moment ago is at server position 0 by construction. `skip` and `totalCount` both advance with it, so the window the next "Load more" asks for still lines up, and if the server returns the same row in that page, `addEntities` no-ops on the id.

Both narrow their argument through `listFields()` (§3.2).

### 4.6 The states the sidebar renders

The store exposes enough to keep five surfaces distinct, which is what stops the deleted client's habit of conflating "empty" with "failed" with "not loaded yet":

| Surface | Condition | Component |
| --- | --- | --- |
| Skeleton | `isPending() && entities().length === 0` (`isFirstLoad`) | `<app-skeleton>` × 6 |
| Failed | `error()` | `<app-error-panel>` with `canRetry(error)` |
| Nothing matched | `hasNoMatches()` — loaded, empty, and a search is why | `<app-empty-state>` with a **Clear search** action |
| Nothing at all | `isEmpty()` — loaded and genuinely empty | `<app-empty-state>` with **New conversation** |
| Rows, possibly truncated | otherwise; plus a **Load more** row while `hasMore()` | `<app-conversation-row>` |

**The skeleton stands in only for the *first* page.** A search that narrows an already-populated list keeps its rows on screen with the field marked busy, because replacing them with grey bars on every keystroke is the flicker this distinction exists to avoid.

**A failed *further* page raises a toast; a failed *first* page fills the panel.** `setError` on a load-more failure would swap perfectly valid loaded rows for an error panel and lose them. The store therefore calls `_toasts.fromError(...)` there and `setError(...)` only in the query pipeline.

**"Load more" states the truncation rather than hiding it.** The list is one page of a server-ordered set with no sort available; an undisclosed cut-off reads as "this is all of them" when it is the first fifty of more. The button stays focusable while its page is in flight — `aria-disabled`, not `disabled`, so focus is not dropped mid-interaction — and `loadMore()` ignores the extra press anyway.

Selection is `routerLinkActive` with `{ exact: true }` plus `ariaCurrentWhenActive="page"`, so the visual state and the announced one are **one fact** rather than two that can disagree. `exact` matters: without it `/chat/a` would light up while `/chat/b` is open.

### 4.7 The one thing that is not extracted yet

The `_started` trio — `ensureLoaded`, the `null` arm of `_runQuery`, and the `onInit` computation — is the obvious candidate for a sixth feature in `core/state/`. It stays inline on the skill's own rule: two stores sharing a shape is a coincidence, three is a feature. **The extraction trigger is the third store that needs a bootstrap-released load**, not the second.

## 5. The Shell

### 5.1 A lazy layout route

```ts
{
  path: '',
  loadComponent: () => import('@features/shell/shell').then((m) => m.Shell),
  children: [ /* chat, admin */ ],
}
```

**A layout route rather than markup in `App`**, because the four unguarded routes — `/auth`, `/login-failed`, `/session-error`, `/signed-out` — and the forbidden page are all deliberately chrome-free, and a shell in `App` would put a sidebar around every one of them. `App` is now almost empty: the sign-out interstitial, the router outlet, and the permanently mounted toast region.

**Lazy on purpose.** The signed-in chrome is roughly **24 kB** of sidebar, tooltip, search field, ridgeline and two stores, none of which the four auth routes render. The fetch costs nobody anything: `sessionGuard` has already awaited a network round trip by the time this route resolves. Both the shell and the chat route are lazy, which is also what leaves EP-6 room for the markdown renderer inside the same budget (§9).

This deletes the temporary `.dev-shell-bar` that hosted `<app-user-footer>` through EP-2. Note the consequence recorded in [Authentication and Session §11.5](authentication-and-session.md#115-cross-tab): `AuthService` is constructed eagerly through `provideAppInitializer`, which is what keeps cross-tab sign-out working now that no always-rendered component injects it.

### 5.2 The skip link, and why its click is handled

The skip link is the first focusable element in the signed-in app, so a keyboard user reaches the screen without tabbing the whole conversation list.

```html
<a class="skip-link" href="#main-content" (click)="skipToMain($event)">Skip to main content</a>
```

**The click is handled rather than left to the browser.** `PathLocationStrategy` listens for `hashchange` as well as `popstate`, so a bare `href="#main-content"` would send the router to `/chat#main-content`, re-run the guards, and leave the fragment in the URL for the rest of the session. The `href` stays for the affordance and for a middle-click.

`<main>` carries `tabindex="-1"`, without which the browser scrolls to the target and leaves focus on `<body>` — and the next Tab restarts at the top of the page. Its `:focus-visible` ring is kept deliberately: focus lands there only from that link, so the ring is the confirmation the link worked.

### 5.3 Two widths, two template branches

Frame `3a` is the 260 px panel; frame `3b` is the 60 px icon strip. They are **separate branches of an `@if`**, not one branch with labels hidden — which is how the design bundle itself writes them. A 44 px square target with a tooltip is a different control from a 36 px row with a visible label, not the same control with its text removed.

The strip keeps the theme control and sign out as icon buttons rather than dropping them to the avatar the board draws: a collapsed sidebar is a **persisted** state, so hiding them would make signing out require expanding the sidebar first. `<app-user-footer>` gained a `compact` input for that, which also carries the display name off-screen (`visually-hidden`) instead of dropping it, since the avatar stays `aria-hidden` in both widths.

The collapse control's glyph is this application's choice. The boards state that it "lives inside the sidebar" (frame `3f`) without drawing one, so it is `bi-chevron-left` / `bi-chevron-right` — a chevron pointing the way the panel will move, and both already in the 74-glyph sprite, so no manifest change was needed. `aria-expanded` sits on the control that changes the state, in both branches.

The strip's search button **expands and then focuses the field**, through `afterNextRender` with an explicit injector: expanding alone would leave the user to find and click the field they just asked for, and the field does not exist in the DOM until the expanded branch is drawn. `SearchInput.focusField()` exists for that caller, so nothing reaches into markup the component owns.

Only the conversation list scrolls. `flex: 1` with `min-height: 0` on the list is what confines it — without the zero minimum a flex child refuses to shrink below its content and the whole sidebar scrolls instead, taking the brand, the actions and the footer with it (US-302's fourth criterion). There is deliberately **no** `overflow: hidden` on the sidebar host, because the collapsed strip's tooltip flyout is positioned outside its 60 px box.

### 5.4 The width transition, withheld for exactly one render

```ts
afterNextRender(() => this._animated.set(true));
```

`UiStore` restores the collapsed preference during construction, before anything paints, so the sidebar's *first* frame is already the right width. Without this flag the 200 ms transition would then run on that first frame and a returning user would watch their sidebar animate from 260 px to 60 px on every load — an animation of a state change that never happened.

No pre-paint script is needed for the width the way `index.html` needs one for the theme ([Design System §3](design-system.md#3-theming-and-the-pre-paint-contract)): the startup shell covers the page until `bootstrapApplication` resolves, so the store is constructed before any of the app is painted.

The transition itself is 200 ms from the one motion scale, whose `--t-slow` names the sidebar width as its example, and the global reduced-motion block suppresses it. The layout is **flex, not grid**, because `flex-basis` and `width` are plain interpolable lengths where animating `grid-template-columns` depends on both track lists staying interpolable.

## 6. `UiStore` — a preference about the browser, not the user

```ts
export const UiStore = signalStore(
  { providedIn: 'root' },
  withState<UiState>(() => ({ sidebarCollapsed: readStoredCollapsed() })),
  withMethods(/* setSidebarCollapsed, toggleSidebar */),
);
```

**It deliberately does not compose `withResetOnSignOut`**, and that is the one thing to know before editing it. Every other store in the app composes it last; this one must not, because US-205 requires the sidebar preference to be one of exactly **two** things that survive sign-out, alongside the theme. Clearing it would put the next person at this machine into a layout they did not choose — and would contradict the build gate that enforces the same rule from the other side.

The rule behind that: `localStorage` holds only what is about the **machine, not the person**. `PREFERENCE_KEYS.sidebar` (`egpt.sidebar`) is one of the two allowlisted keys, and `scripts/check-forbidden-apis.mjs` fails the build on a `localStorage.setItem` anywhere outside [`core/storage/local-preferences.ts`](../../enterprise-gpt-ui/src/app/core/storage/local-preferences.ts) ([Authentication and Session §11.6](authentication-and-session.md#116-what-survives-and-how-that-is-enforced)).

Anything other than the `collapsed` marker — unset, corrupted, or storage the browser refuses to open — reads as expanded, which is the state that shows the user where everything is.

It is a store rather than a one-field service because US-910's device-local project pins land here too.

## 7. The Chat Route

### 7.1 One `UrlMatcher` for `/chat` and `/chat/{conversationId}`

```ts
{
  matcher: chatMatcher,
  loadComponent: () => import('@features/chat/chat').then((m) => m.Chat),
  title: 'Chat — Enterprise GPT',
}
```

**This is the most consequential decision in the epic for the stories that follow it.** The router's default `shouldReuseRoute` compares `future.routeConfig === curr.routeConfig`. Two sibling routes — `path: 'chat'` and `path: 'chat/:conversationId'` — are two different configs, so moving between them **destroys and recreates the component**. One config means the same move changes a parameter instead.

That is exactly what US-401 needs: it creates a conversation around the first prompt and updates the URL with `Location.replaceState` while the answer is already streaming into the page. A remount there would discard the composer's contents, the stream, and the transcript built so far.

```ts
export function chatMatcher(segments: UrlSegment[]): UrlMatchResult | null {
  const [first, second, ...rest] = segments;

  if (first?.path !== 'chat' || rest.length > 0) return null;
  if (second === undefined) return { consumed: [first] };

  return CONVERSATION_ID.test(second.path)
    ? { consumed: [first, second], posParams: { [CONVERSATION_ID_PARAM]: second } }
    : null;
}
```

A second segment that is not a GUID returns `null` rather than matching with a nonsense parameter, so `/chat/garbage` falls through to the wildcard instead of issuing `GET api/conversations/garbage` and rendering a 404 panel for a typo.

The parameter reaches the component through `withComponentInputBinding()`:

```ts
readonly conversationId = input<string>();
```

It is set back to `undefined` when the parameter disappears, which is how navigating from `/chat/{id}` to `/chat` **closes** the conversation without a remount.

### 7.2 `ConversationStore` — route-scoped, and the store US-401 extends

Provided by `Chat` rather than `providedIn: 'root'`, so its lifetime is the chat screen's — a lifetime that, thanks to the matcher, spans `/chat` and `/chat/{id}` without interruption.

```ts
constructor() {
  // An injection context, which a signal-valued reactive method requires: it binds the
  // subscription to this component rather than to the root injector.
  this.conversation.open(this.conversationId);
}
```

Four things in it are worth carrying forward:

- **It reads `GET api/conversations/{id}`, never `…/messages`** — §3.2.
- **The `null` case is handled *inside* `switchMap`, not filtered ahead of it.** Filtering would leave the request for the conversation *just closed* still running, because `switchMap` would never see the value that cancels it. The null arm returns `EMPTY`, which cancels it.
- **`name` falls back to the sidebar's copy** while the detail request is in flight (`_list.entityMap()[id]?.name`), so opening a conversation from the list shows its title immediately instead of flashing a placeholder for a round trip. It is empty only for a deep link to a row the list has not loaded — where the skeleton sits *inside* the `<h1>` rather than replacing it, so the document is never without a level-1 heading.
- **On success it calls `_list.refreshRow(conversation)`**, keeping the sidebar in step with a name the server changed out of band. That is the seam US-409 uses.

US-401 (send), US-408 (busy) and US-410 (restore model and MCP selection) all extend this store rather than adding another.

### 7.3 The empty state (US-303), and what was deferred

"New conversation" is a **`routerLink` to `/chat`** — a link, not a button. There is no `POST api/conversations` to suppress, because nothing is created until the first prompt (US-401). That is the whole story: an abandoned idea leaves nothing in the sidebar.

The centred wordmark and "How can I help you today?" are built. Two things about it are deliberate:

- **The suggested prompt chips are deferred to US-401**, agreed with the product owner on 2026-08-11. A chip's only behaviour is to seed the composer's text, and the composer is EP-4 — shipping them now would put four controls on screen that visibly do nothing. The same reasoning settled the criterion's phrase "renders an empty composer": the composer arrives whole in US-401–403 rather than as an inert box.
- **Nothing moves focus on arrival.** This is the application's landing screen rather than a state transition, and taking focus would jump a keyboard user past the shell's skip link on every cold start — the opposite of the rule the auth pages follow, where focus *is* moved because each of those is a transition ([Authentication and Session §4.2](authentication-and-session.md#42-the-five-routes)).

`asPageHeading` switches the prompt between `<h1>` and `<p>`: once a conversation is open, the 52 px header's conversation name is the page heading and a second `<h1>` would compete with it. The header itself is **absent rather than empty** when nothing is open, which is frame `1a`; its favourite star and kebab belong to US-308.

## 8. `canRetry` — whether to offer the control at all

New in `core/errors/error-message.ts`, alongside `shouldNotify`, `userMessage` and `traceLine` ([Frontend Foundation §4.7](frontend-foundation.md#47-turning-an-error-into-words)):

```ts
<app-error-panel [error]="error" [canRetry]="canRetry(error)" (retried)="retry()" />
```

It answers a **different question** from `retry-policy.ts`'s `isTransientRetriable`, which decides whether the interceptor may silently replay a GET. This is the broader, slower question of whether to render the button at all: a 500 or a dropped connection is worth another go, while a 403, a 404 or a missing grant will answer identically however many times it is asked — and a Retry that cannot work reads as a broken button.

Both surfaces in this epic use it. The sidebar's panel offers no Retry for a 403 on the list; the chat route's offers none for a 404, which means the conversation does not exist **or** belongs to someone else — the API deliberately does not distinguish the two, and neither does the copy.

It is backed by the same exhaustive `as const satisfies Record<AppErrorKind, boolean>` table the other three use, so a new `AppError` arm cannot be added without deciding. The `http` arm is decided by status instead — `>= 500 || 408` — because it also covers a routing 404 that is not going to start existing.

## 9. Bundle and budgets

| | Initial raw | Initial transfer | Budget (warn / error) |
| --- | --- | --- | --- |
| End of EP-2 (US-205) | 637.10 kB | 153.38 kB | 650 kB / 720 kB |
| **End of EP-3 (US-303)** | **643.62 kB** | **158.05 kB** | **660 kB / 720 kB** |

EP-3 cost **6.52 kB** of initial graph — the two root stores, the conversation DTOs and `canRetry` — because the chrome itself is behind the lazy shell route and the chat screen behind its own. The warning threshold moved 650 → 660 kB to keep headroom above the new baseline; the **failure** threshold is unchanged at 720 kB, which is the number that matters. The `styles` budget is unchanged at 65 / 80 kB.

The gate that follows is [the build order's gate at US-601](../prd/enterprise-ui-rebuild-build-order.md): `ngx-markdown` + marked + Prism are still unpaid against this baseline. Both routes being lazy is what leaves the transcript renderer somewhere to go that is not the initial chunk.

## 10. Testing

The suite is **610 specs** across 55 files, with `npm run lint` and `npm run build` green alongside.

| Area | Spec | Notable cases |
| --- | --- | --- |
| The list store | `conversation-list-store` (20) | `name` omitted on the first load and never sent as `filter`; nothing issued until the bootstrap releases it; the newest query winning over one in flight; a narrowed search replacing rather than appending; the offset advancing by what actually arrived; a second Load more ignored; **Load more refused while the result set beneath it is being replaced**; a page dropped after a fresh search superseded it; a failed further page keeping its rows and raising a toast; `refreshRow` ignoring a row it does not hold; `prependNewest` keeping `skip` and `totalCount` in step; detail-only fields kept out of the entities; the store emptying on sign-out and reloading for the next user |
| The shell and sidebar | `shell` (22) | The skip link ahead of everything in the tab order; skeleton versus empty state versus no-matches versus error panel; the list scrolling on its own; Load more offered only while the server says there is more; the search field reaching the store after the debounce; the Admin entry absent for a non-administrator; the strip's expand-and-focus; the collapse surviving a reload; the width transition withheld for the first render; every icon-only control in the strip named |
| The chat route | `chat` (13) | The matcher on all four shapes, including refusing `/chat/garbage`; no header on the bare route; **the component instance surviving a move between `/chat` and `/chat/{id}`**; the request cancelled when the id goes away; the name borrowed from the sidebar while the detail is in flight; a 404 offering no Retry; the sidebar row refreshed from the detail response |
| Layout preference | `ui-store` (5) | Expanded when nothing is stored; an unrecognised stored value treated as expanded; the one allowlisted key; **the preference surviving sign-out** |

[`src/testing/conversations.ts`](../../enterprise-gpt-ui/src/testing/conversations.ts) carries the fixtures. Two details in it are load-bearing: ids are sequential with **no modulo**, because a repeating id collapses entities silently (`setAllEntities` keeps one, `addEntities` no-ops) and then trips NG0955 on `@for … track id`; and `conversationPage()` computes `pageSize` and `currentPage` the way the server does, so a store cannot pass against an envelope the API never sends.

```bash
# from enterprise-gpt-ui/
npm test        # Vitest, single run — 610 specs
npm run lint    # ESLint + check:icons + check:forbidden + check:tokens
npm run build   # budgets, then check-initial-chunk.mjs
```

> **Verification status.** As with EP-2, none of this has been exercised against a live API or a real Entra tenant — the API does not start in this environment. The gates above are green and every behaviour here is asserted against `HttpTestingController` and `RouterTestingHarness`.

## 11. What is not here yet

| Missing | Owner | Notes |
| --- | --- | --- |
| Rename, favourite, delete, move-to-project on a row | US-304, US-305, US-306, US-307 | `ConversationRow` is the element they hang off; compose `withPendingIds` with the first of them (§2.2) |
| The conversation header's star and kebab | US-308 | The 52 px header exists and shows the name; two of its four kebab items route through US-307, which is scheduled in P5 |
| The composer, the transcript, and the streamed turn | EP-4 | `ConversationStore` is what US-401, US-408 and US-410 extend |
| The suggested prompt chips on the empty state | US-401 | Deferred deliberately (§7.3) |
| The Conversations, Projects and Documents nav entries | EP-7, EP-9, EP-10 | Each arrives with its epic; `navItems` renders only routes that exist |
| The **Favorite projects** section between the nav and the list | US-910 | Its pins are device-local until the backend enabler US-909 lands, and a section that has to say so has to exist first |
| `isFavorite` on the search request | US-305, US-703 | The parameter is already in the API (§3.1) |
| Server-side sort on the list | US-706 | Until then the list is regime B: server order, no sort control |

## 12. Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Every search returns the full list | The request sent `filter=` instead of `name=`. The server binds nothing and answers unfiltered (§3.1) |
| A search feels sluggish by about a third of a second | A second `debounceTime` was added in the store. `SearchInput` already debounces (§4.3) |
| Retry on the sidebar's error panel does nothing | Something added `distinctUntilChanged`, which swallows the deliberate duplicate (§4.3) |
| Rows appear that the user cannot scroll to, and Load more has vanished | A page landed against a result set it does not belong to. Check both rules in §4.4 are intact |
| The list is empty after signing in as a second user | Only if `withResetOnSignOut` moved off the end of the store, or `takeUntil(signedOut$)` came off the request ([Frontend Foundation §5.5](frontend-foundation.md#55-withresetonsignout--compose-it-last)) |
| A conversation renamed elsewhere does not update in the sidebar | `refreshRow` ignores a row the list does not hold — check it is loaded, not that the call failed (§4.5) |
| Nothing is ever requested for the list | `ensureLoaded()` was not called, or was moved ahead of the awaited session in `SessionBootstrap` (§4.2) |
| A new conversation lands in the wrong place in the list | `prependNewest` is correct only while the server orders by `dateCreated` descending (§3.1) |
| The composer's contents are lost the moment the first prompt creates a conversation | The chat route was split into two configs, so the router remounts. It must stay one `matcher` route (§7.1) |
| `/chat/{id}` renders the empty state | The id is not a GUID, so the matcher declined and the wildcard sent the URL to `/chat` (§7.1) |
| A project or model picker reads as unset on a conversation that has both | Something hydrated it from `GET api/conversations/{id}/messages` (§3.2) |
| The sidebar animates shut on every page load | The `afterNextRender` flag that withholds the transition was removed (§5.4) |
| The sidebar collapse is forgotten after signing out | `withResetOnSignOut` was composed into `UiStore`. It must not be (§6) |
| The skip link puts `#main-content` in the URL and re-runs the guards | Its click handler was removed; `PathLocationStrategy` listens for `hashchange` (§5.2) |
| The collapsed strip's tooltip is clipped | An `overflow: hidden` was added to the sidebar host. The flyout is positioned outside the 60 px box (§5.3) |
| The whole sidebar scrolls instead of the list | The `min-height: 0` came off the list's flex chain (§5.3) |

## 13. Key files

| Concern | File |
| --- | --- |
| The reference list store | [`core/conversations/conversation-list-store.ts`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-list-store.ts) |
| Layout preferences | [`core/ui/ui-store.ts`](../../enterprise-gpt-ui/src/app/core/ui/ui-store.ts) |
| The signed-in frame | [`features/shell/shell.ts`](../../enterprise-gpt-ui/src/app/features/shell/shell.ts) |
| Sidebar, both widths | [`features/shell/sidebar/sidebar.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.ts), [`sidebar.html`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.html) |
| One row | [`features/shell/sidebar/conversation-row.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/conversation-row.ts) |
| The chat URL matcher | [`features/chat/chat-route.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat-route.ts) |
| The open conversation | [`features/chat/conversation-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/conversation-store.ts) |
| Chat screen and empty state | [`features/chat/chat.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat.ts), [`chat-empty-state.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat-empty-state.ts) |
| Route table | [`src/app/app.routes.ts`](../../enterprise-gpt-ui/src/app/app.routes.ts) |
| Bootstrap sequencing | [`core/session/session-bootstrap.ts`](../../enterprise-gpt-ui/src/app/core/session/session-bootstrap.ts) |
| Conversation DTOs, and the `/messages` warning | [`domain/api/conversation.ts`](../../enterprise-gpt-ui/src/app/domain/api/conversation.ts) |
| Retry policy for a person, not the interceptor | [`core/errors/error-message.ts`](../../enterprise-gpt-ui/src/app/core/errors/error-message.ts) |
| Fixtures | [`src/testing/conversations.ts`](../../enterprise-gpt-ui/src/testing/conversations.ts) |
| The API side of the list | [`Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs), [`ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs) |
| Related reference | [Frontend Foundation](frontend-foundation.md), [Authentication and Session](authentication-and-session.md), [Design System](design-system.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md), [Conversation usage and favorites](../conversations/usage-and-favorites.md) |
