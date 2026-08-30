# Shell and Navigation

How the rebuilt Angular client at `enterprise-gpt-ui/` frames a signed-in user: the layout route that owns the chrome, the sidebar that lists their conversations, the store behind that list, and the chat route that serves both `/chat` and `/chat/{conversationId}` from one configuration.

Audience: a developer about to add a per-row action to the conversation list, a second list store anywhere in the app — US-701's [Conversation Library](conversation-library.md) is the first one written against this shape, and the worked example to copy — or a screen inside the shell. Read [Frontend Foundation](frontend-foundation.md) first for the store features every store here composes, and [Authentication and Session](authentication-and-session.md) for the guards this shell sits inside. Bare `§` references below are to sections of _this_ page.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference.

## 1. Overview

EP-3 turns the guarded route tree into an application with a shape. Three stories landed together, and US-302 carried the shell with it — no story owned the frame itself, and the sidebar has to live somewhere:

| Story      | What it delivers                                                                       |
| ---------- | -------------------------------------------------------------------------------------- |
| **US-302** | The conversation list, `ConversationListStore`, and the shell and sidebar that host it |
| **US-301** | Collapse to a 60 px icon strip, remembered across reloads in `UiStore`                 |
| **US-303** | The empty chat state a new conversation opens on — which creates nothing               |

**`ConversationListStore` is the reference list store.** Fifteen more are written against its shape, and the PRD's risk table requires it be reviewed as a _pattern decision_ before any of them exists. §4 is therefore the longest section here and the one to read before writing a store; four of its choices are decisions rather than details, and each one looks removable until you know what it prevents.

Seven things are worth knowing before you touch this epic:

1. **The list loads declaratively.** One `rxMethod` is wired in `onInit` to a computation over two pieces of state; nothing else calls it. `SessionBootstrap` only flips a flag (§4.2).
2. **Two separate rules stop a stale page landing in a fresh result set**, and both are needed — a subject that cancels a superseded "Load more", and a refusal to start one while a query is in flight (§4.4).
3. **`refreshRow` and `prependNewest` are not the same operation**, and a row the list does not hold is deliberately ignored rather than inserted (§4.5).
4. **One route configuration serves `/chat` and `/chat/{id}`**, through a `UrlMatcher`, so the component is reused rather than remounted — which is what US-401 needs to create a conversation mid-turn (§7.1).
5. **The shell is lazy on purpose**, keeping ~24 kB of signed-in chrome off the four unguarded auth routes (§5.1).
6. **`UiStore` does not compose `withResetOnSignOut`.** The sidebar preference is about the browser, not the user, and US-205 requires it to survive sign-out (§6).
7. **The shell owns three viewport modes, and all sidebar policy with them** — US-1403 made `Sidebar` a controlled component, because the same chevron persists a width at desktop and opens a transient overlay at tablet (§5.5).

### 1.1 Where each piece lives

| Concern                                                | Where                                                                                                                                                                                                                                      |
| ------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| The conversation list, and the reference store pattern | [`core/conversations/conversation-list-store.ts`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-list-store.ts)                                                                                                           |
| Layout preferences that survive sign-out               | [`core/ui/ui-store.ts`](../../enterprise-gpt-ui/src/app/core/ui/ui-store.ts)                                                                                                                                                               |
| The signed-in frame                                    | [`features/shell/shell.ts`](../../enterprise-gpt-ui/src/app/features/shell/shell.ts), [`shell.html`](../../enterprise-gpt-ui/src/app/features/shell/shell.html), [`shell.scss`](../../enterprise-gpt-ui/src/app/features/shell/shell.scss) |
| The sidebar, all three modes                           | [`features/shell/sidebar/sidebar.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.ts), [`sidebar.html`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.html)                                            |
| The mobile navbar, and the slot a screen fills         | [`features/shell/mobile-navbar/`](../../enterprise-gpt-ui/src/app/features/shell/mobile-navbar/), [`core/ui/shell-chrome.ts`](../../enterprise-gpt-ui/src/app/core/ui/shell-chrome.ts)                                                     |
| One conversation row                                   | [`features/shell/sidebar/conversation-row.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/conversation-row.ts)                                                                                                                 |
| The chat route's URL matcher                           | [`features/chat/chat-route.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat-route.ts)                                                                                                                                               |
| The open conversation                                  | [`features/chat/conversation-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/conversation-store.ts)                                                                                                                               |
| The chat screen and its empty state                    | [`features/chat/chat.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat.ts), [`chat-empty-state.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat-empty-state.ts)                                                               |
| Route table, matcher wiring, lazy boundaries           | [`src/app/app.routes.ts`](../../enterprise-gpt-ui/src/app/app.routes.ts)                                                                                                                                                                   |
| Releasing the list load after the session              | [`core/session/session-bootstrap.ts`](../../enterprise-gpt-ui/src/app/core/session/session-bootstrap.ts)                                                                                                                                   |
| The API shapes this epic reads                         | [`domain/api/conversation.ts`](../../enterprise-gpt-ui/src/app/domain/api/conversation.ts)                                                                                                                                                 |
| Whether to offer a Retry at all                        | [`core/errors/error-message.ts`](../../enterprise-gpt-ui/src/app/core/errors/error-message.ts) — `canRetry` (§8)                                                                                                                           |
| Test fixtures for conversations and pages              | [`src/testing/conversations.ts`](../../enterprise-gpt-ui/src/testing/conversations.ts)                                                                                                                                                     |

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
           ├─ /conversations  loadComponent: Conversations  ← lazy, added by US-701
           └─ /admin  canMatch: [adminCanMatch]  loadChildren
```

Two placements in that tree are deliberate and easy to undo by accident.

**`/forbidden` is a sibling of the shell, not a child of it.** Frame `6e` is a full-page state with no chrome, and a sidebar around it would offer exactly the navigation the page exists to refuse.

**`adminCanMatch` stays on the route that owns `loadChildren`**, even now that the route sits inside the shell. `canMatch` still runs during URL recognition, so a non-administrator's browser never requests the chunk — moving the guard to the shell route above it would run it before the admin config was reached. See [Authentication and Session §9](authentication-and-session.md#9-administration-withheld-rather-than-hidden-us-203).

## 2. Quick start

### 2.1 Reading the conversation list

`ConversationListStore` is root-provided and already loading by the time any screen inside the shell renders — `SessionBootstrap` released it after `POST api/users/me` returned.

```ts
import { Component, inject } from "@angular/core";
import { ConversationListStore } from "@core/conversations/conversation-list-store";

@Component({/* … */})
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

Root-provided rather than route-scoped, because the sidebar outlives every route in the shell and re-fetching the list on each navigation would be visible. The _route-scoped_ store in this epic is `ConversationStore` (§7.2), which `Chat` provides.

### 2.2 Adding a per-row action — what US-304 does next

Three seams already exist for it, so the story is additive rather than a refactor:

1. **`ConversationRow` is its own component** precisely so the kebab, the star and per-row pending state hang off one element rather than off markup inline in the sidebar.
2. **`withPendingIds` is not composed yet.** Compose it into `ConversationListStore` — after `withOffsetPagination`, before `withProps`, and always before `withResetOnSignOut` — and read it with `isRowPending(id)`. It is absent today only because nothing has a per-row action ([Frontend Foundation §5.3](frontend-foundation.md#53-withpendingids)).
3. **`refreshRow` is the write path** for a row whose server copy has changed (§4.5). An optimistic rename patches the entity, and a failure patches the old value back.

`PUT api/conversations` replaces the whole conversation, so a rename must echo the current `projectId` — that is US-304's first acceptance criterion and US-307's `toUpdateBody()` helper is where it ends up.

### 2.3 Adding a nav entry

`Sidebar.navItems` is a `computed` over the session, and **only entries whose route exists are rendered**. The board draws five, and all five now exist — Conversations arrived with US-701 ([Conversation Library §6](conversation-library.md#6-route-nav-entry-and-bundle)), Projects with US-901 ([Projects §11](projects.md#11-route-nav-entry-and-bundle)) and Documents with US-1002 ([Documents Library §8](documents-library.md#8-route-nav-entry-and-bundle)) — each with its own epic, because a link that redirects back to `/chat` is worse than no link. Add yours the same way:

```ts
{ label: 'Projects', icon: 'bi-folder', link: PROJECTS_ROUTE, restricted: false }
```

`restricted: true` renders the shield glyph frame `3a` puts beside a permission-gated entry; gate the entry itself on the permission, absent rather than disabled, as US-203 does for Admin. The icon name must exist in the sprite manifest or `npm run lint` fails ([Design System §5.3](design-system.md#53-adding-a-glyph)).

## 3. The conversation API, stated once

Everything in this epic reads two routes. Both have a sharp edge.

### 3.1 `GET api/conversations/search`

| Parameter    | Client sends                              | Server behaviour                                                                                                                   |
| ------------ | ----------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------- |
| `name`       | **Omitted** when blank; trimmed otherwise | A `LIKE` filter. An empty `name=` still takes the server down the `LIKE` branch, which is why it is omitted rather than sent empty |
| `skip`       | The offset reached so far                 | —                                                                                                                                  |
| `take`       | `SIDEBAR_PAGE_SIZE`, 50                   | **Clamped to 1–100** server-side, silently. The client's own `MAX_PAGE_SIZE` mirrors that clamp                                    |
| `isFavorite` | **`true` or omitted**, never `false` — the sidebar never sends it; the conversations library sends `true` while its favourites filter is on (US-703) | `true`/`false`/absent. `false` means "only the ones **not** starred", a third state no screen has a control for, so the client never spells it ([Conversation Library §2.2](conversation-library.md#22-the-three-url-parameters)) |

**The parameter is `name`. It is never `filter`.** That was the deleted client's named regression: it sent `filter=`, the server bound nothing, and every search returned the unfiltered list looking like a working search that matched everything. A spec asserts the parameter name directly, not just that a request went out.

**Results are ordered by `dateCreated` descending — not `dateModified`.** A rename or a new turn does **not** move a row to the top. `GET api/conversations/search` has accepted `sort=` and `dir=` since US-706, 2026-08-20 ([Conversation Usage and Favourites §5.6](../conversations/usage-and-favorites.md#56-ordering--sort-and-dir-us-706)) — the conversations library sends the reader's choice through it ([Conversation Library §5.1](conversation-library.md#51-the-toolbar)) — but **this store deliberately sends neither.** The sidebar is a fixed recency list with no control of its own to offer an order from; the reader who wants another order has `/conversations`, where the select lives. Omitting both parameters, as this store always has, reproduces the pre-US-706 order byte-for-byte, so nothing here changed behaviourally when the endpoint gained the capability.

That ordering is also the _only_ reason `prependNewest` is correct (§4.5). If sorting ever moves to `dateModified`, that method becomes a guess and has to go.

### 3.2 `GET api/conversations/{id}` versus `…/{id}/messages`

`ConversationDetailDto` — from `GET api/conversations/{id}` — is the only source of `mcpServerIds`, which US-410 restores into the MCP picker. It reports `modelId: null` and an empty `mcpServerIds` for a conversation whose first turn has not completed, because the server writes both from the newest usage row; the client therefore **skips** the settings seed on an empty payload rather than treating it as a selection of nothing ([Turn Lifecycle §7.6](../conversations/turn-lifecycle.md#76-restoring-the-model-and-mcp-selection)).

> **`GET api/conversations/{id}/messages` reports `projectId`, `modelId` and `isFavorite` as `null` / `null` / `false` regardless of the truth.** It returns a `ConversationDto`-shaped object built from the Cosmos transcript document, which does not carry those three fields. It must never be used to hydrate a project picker, a model picker or a favourite star — only `GET api/conversations/{id}` may. This is the kind of defect that looks like a state bug in the client for a day.

US-410 gave that route its first consumer — `TurnStore`, replaying the stored messages into the transcript. The trap above is closed by the type rather than by discipline: the client models the response as its own three-field `ChatConversationDto` (`id`, `name`, `messages`), so the misleading fields are not reachable through it. Message `role` arrives as a **number**, not a string ([Turn Lifecycle §7.4](../conversations/turn-lifecycle.md#74-the-message-shape-has-two-sharp-edges)).

`listFields()` in the store exists for the mirror-image reason: `ConversationStore` legitimately hands the list a `ConversationDetailDto`, and spreading one straight into an entity would leave `mcpServerIds` on some rows and not others, so `entities()` would stop being uniformly `ConversationDto`-shaped.

## 4. `ConversationListStore` — the reference list store

### 4.1 What it composes, and in what order

```ts
export const ConversationListStore = signalStore(
  { providedIn: "root" },
  withState(initialState), // name, _started, loadingMore
  withEntities<ConversationDto>(),
  withRequestStatus(),
  withOffsetPagination(SIDEBAR_PAGE_SIZE),
  withProps(/* http, toasts, url, signedOut$, _querySuperseded$ */),
  withComputed(/* hasQuery, isEmpty, hasNoMatches */),
  withMethods(/* _runQuery, _loadNextPage, and the public surface */),
  withHooks({ onInit, onDestroy }),
  withResetOnSignOut(), // last. Always.
);
```

That is [Frontend Foundation §5.7](frontend-foundation.md#57-composition-order)'s order, with the state-contributing features first and `withResetOnSignOut` last. **`withClientQuery` is deliberately not composed** — this store offers no sort of its own (§3.1) and the search is a server parameter rather than a client filter. **`withPendingIds` is deliberately not composed yet** — §2.2.

Three pieces of local state carry their own reasons:

| Key           | Why it is not derivable                                                                                                                                |
| ------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `name`        | The `name=` filter in force. Empty means unfiltered                                                                                                    |
| `_started`    | A release sentinel. `_`-prefixed, so `@ngrx/signals` strips it from the public store type — it is not something a screen should render or reason about |
| `loadingMore` | Separate from `requestStatus` on purpose: a further page must not collapse the loaded rows back to a skeleton                                          |

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

- `retry()` deliberately pushes the _same_ name a second time. That is the whole operation.
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

`isPending()` is the load-bearing half. `_querySuperseded$` cannot help a page started _after_ a query was already on the wire: that request carries the old `skip` against the new result set, and when it lands after the query's `setFirstPage` it appends a window from the middle of a list that no longer exists — **leaving a hole the user can never scroll to, with `hasMore()` reading false.** The rows look plausible. Nothing errors.

Three smaller choices in the same pipelines:

- **`exhaustMap`, not `switchMap`, on `_loadNextPage`.** A second press while a page is in flight is a double-click, not a request for the page after next.
- **`addEntities`, not `setEntities`.** A concurrent insert can shift the window and repeat a row; the copy already on screen wins.
- **`finalize` clears `loadingMore`.** It runs on cancellation too — which is exactly the case where the flag would otherwise stick, since a superseded page reaches neither `next` nor `error`.

And `takeUntil(signedOut$)` sits on both requests, because clearing state is not cancelling work ([Frontend Foundation §5.5](frontend-foundation.md#55-withresetonsignout--compose-it-last)).

### 4.5 `refreshRow` versus `prependNewest`

Two write paths, and they are not interchangeable.

| Method                        | For                                                                                                                                                              | Behaviour on a row the list does not hold |
| ----------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------- |
| `refreshRow(conversation)`    | A row already held whose server copy changed — the title the server generates after a first turn (US-409), an edit from the conversation header (US-304, US-305) | **Ignored**                               |
| `prependNewest(conversation)` | A conversation _just created_ by this session (US-401)                                                                                                           | Prepends; already-held ids are skipped    |

**Why an unheld row is ignored rather than inserted.** The list is a _window_ onto a server-ordered result set. A conversation the window does not contain could be anywhere in that order — page three, or filtered out by the current search — and inserting it at either end would be a guess that survives until the next fetch, leaving a row in a position the server never put it in.

**Why prepending is nevertheless correct for a new one.** The server orders by `dateCreated` **descending** (§3.1), so a row created a moment ago is at server position 0 by construction. `skip` and `totalCount` both advance with it, so the window the next "Load more" asks for still lines up, and if the server returns the same row in that page, `addEntities` no-ops on the id.

Both narrow their argument through `listFields()` (§3.2).

### 4.6 The states the sidebar renders

The store exposes enough to keep five surfaces distinct, which is what stops the deleted client's habit of conflating "empty" with "failed" with "not loaded yet":

| Surface                  | Condition                                                | Component                                          |
| ------------------------ | -------------------------------------------------------- | -------------------------------------------------- |
| Skeleton                 | `isPending() && entities().length === 0` (`isFirstLoad`) | `<app-skeleton>` × 6                               |
| Failed                   | `error()`                                                | `<app-error-panel>` with `canRetry(error)`         |
| Nothing matched          | `hasNoMatches()` — loaded, empty, and a search is why    | `<app-empty-state>` with a **Clear search** action |
| Nothing at all           | `isEmpty()` — loaded and genuinely empty                 | `<app-empty-state>` with **New conversation**      |
| Rows, possibly truncated | otherwise; plus a **Load more** row while `hasMore()`    | `<app-conversation-row>`                           |

**The skeleton stands in only for the _first_ page.** A search that narrows an already-populated list keeps its rows on screen with the field marked busy, because replacing them with grey bars on every keystroke is the flicker this distinction exists to avoid.

**A failed _further_ page raises a toast; a failed _first_ page fills the panel.** `setError` on a load-more failure would swap perfectly valid loaded rows for an error panel and lose them. The store therefore calls `_toasts.fromError(...)` there and `setError(...)` only in the query pipeline.

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

**Lazy on purpose.** The signed-in chrome is roughly **24 kB** of sidebar, tooltip, search field, ridgeline and two stores, none of which the four auth routes render. The fetch costs nobody anything: `sessionGuard` has already awaited a network round trip by the time this route resolves. Both the shell and the chat route are lazy, which is what let EP-6 put the whole markdown renderer inside the chat chunk rather than the initial budget (§9).

This deletes the temporary `.dev-shell-bar` that hosted `<app-user-footer>` through EP-2. Note the consequence recorded in [Authentication and Session §11.5](authentication-and-session.md#115-cross-tab): `AuthService` is constructed eagerly through `provideAppInitializer`, which is what keeps cross-tab sign-out working now that no always-rendered component injects it.

### 5.2 The skip link, and why its click is handled

The skip link is the first focusable element in the signed-in app, so a keyboard user reaches the screen without tabbing the whole conversation list.

```html
<a class="skip-link" href="#main-content" (click)="skipToMain($event)"
  >Skip to main content</a
>
```

**The click is handled rather than left to the browser.** `PathLocationStrategy` listens for `hashchange` as well as `popstate`, so a bare `href="#main-content"` would send the router to `/chat#main-content`, re-run the guards, and leave the fragment in the URL for the rest of the session. The `href` stays for the affordance and for a middle-click.

`<main>` carries `tabindex="-1"`, without which the browser scrolls to the target and leaves focus on `<body>` — and the next Tab restarts at the top of the page. Its `:focus-visible` ring is kept deliberately: focus lands there only from that link, so the ring is the confirmation the link worked.

### 5.3 Two widths, two template branches

Frame `3a` is the 260 px panel; frame `3b` is the 60 px icon strip. They are **separate branches of an `@if`**, not one branch with labels hidden — which is how the design bundle itself writes them. A 44 px square target with a tooltip is a different control from a 36 px row with a visible label, not the same control with its text removed.

The strip keeps the theme control and sign out as icon buttons rather than dropping them to the avatar the board draws: a collapsed sidebar is a **persisted** state, so hiding them would make signing out require expanding the sidebar first. `<app-user-footer>` gained a `compact` input for that, which also carries the display name off-screen (`visually-hidden`) instead of dropping it, since the avatar stays `aria-hidden` in both widths.

The collapse control's glyph is this application's choice. The boards state that it "lives inside the sidebar" (frame `3f`) without drawing one, so it is `bi-chevron-left` / `bi-chevron-right` — a chevron pointing the way the panel will move, and both already in the 74-glyph sprite, so no manifest change was needed. `aria-expanded` sits on the control that changes the state, in both branches.

The strip's search button **expands and then focuses the field**, through `afterNextRender` with an explicit injector: expanding alone would leave the user to find and click the field they just asked for, and the field does not exist in the DOM until the expanded branch is drawn. `SearchInput.focusField()` exists for that caller, so nothing reaches into markup the component owns. Since US-1403 it emits `searchRequested` and the shell decides what "expand" means at this width (§5.5); the focus half stays here, because only this component knows which of its branches holds the field.

Toggling swaps two mutually exclusive branches, which **destroys the button that was just pressed** and drops focus to `<body>`, where the next Tab restarts at the top of the page. Focus therefore follows to the control that replaced it, after the render that creates it. That is harmless at desktop and a dead end without it at tablet, where this chevron is how the overlay is dismissed — which is also why `focusCollapseControl()` is public: the shell needs it after a backdrop dismissal, and the alternative, querying `.strip__button` by document order, picks a class four other controls share.

Only the conversation list scrolls. `flex: 1` with `min-height: 0` on the list is what confines it — without the zero minimum a flex child refuses to shrink below its content and the whole sidebar scrolls instead, taking the brand, the actions and the footer with it (US-302's fourth criterion). There is deliberately **no** `overflow: hidden` on the sidebar host: the collapsed strip's tooltip flyout is positioned outside this column, and during the 200ms expand the contents reflow at the animating width instead of overflowing, which is what a sidebar animation looks like anyway.

### 5.4 The width transition, withheld for exactly one render

```ts
afterNextRender(() => this._animated.set(true));
```

`UiStore` restores the collapsed preference during construction, before anything paints, so the sidebar's _first_ frame is already the right width. Without this flag the 200 ms transition would then run on that first frame and a returning user would watch their sidebar animate from 260 px to 60 px on every load — an animation of a state change that never happened.

No pre-paint script is needed for the width the way `index.html` needs one for the theme ([Design System §3](design-system.md#3-theming-and-the-pre-paint-contract)): the startup shell covers the page until `bootstrapApplication` resolves, so the store is constructed before any of the app is painted.

The transition itself is 200 ms from the one motion scale, whose `--t-slow` names the sidebar width as its example, and the global reduced-motion block suppresses it. The layout is **flex, not grid**, because `flex-basis` and `width` are plain interpolable lengths where animating `grid-template-columns` depends on both track lists staying interpolable.

### 5.5 Three viewport modes (US-1403)

Since US-1403 the shell owns three layout modes, and **all sidebar policy with them**. What each mode renders, and the app-wide picture — the breakpoint record, the overlay clamp, safe areas, what the other screens do — is [Responsive layout](responsive-layout.md). What belongs here are the four decisions a reader of the shell will meet in the code.

```ts
protected readonly mode = computed<ShellMode>(() =>
  this._isMobile() ? 'mobile' : this._isTablet() ? 'tablet' : 'desktop',
);
```

**Both queries false means `desktop`, and the ordering is load-bearing.** The test fake answers `false` for every query a spec has not set, so "nothing configured" has to resolve to the mode the existing suite was written against.

**`Sidebar` is now a controlled component.** It takes a `mode` input and emits `toggled` and `searchRequested`; it no longer injects `UiStore`. It cannot: the same chevron means "persist a width" at desktop and "open a transient overlay" at tablet, and only the shell can see which. Leaving the policy in the sidebar would mean that component deciding a question it cannot see the inputs to. Its third mode, `overlay`, is the 260px panel **without** the collapse chevron — inside the mobile drawer there is no collapsed state to go to, and the shell's own close button already sits where the chevron would.

**The persisted `egpt.sidebar` preference is read and written at desktop only.** At tablet the strip is the viewport's default rather than the user's choice — frame `3g`'s own caption says so — and expanding there must not silently change the width the user will meet on their laptop.

**The overlay flag is a `linkedSignal` on the shell, not a field on `UiStore`.**

```ts
private readonly _overlayOpen = linkedSignal<ShellMode, boolean>({
  source: this.mode,
  computation: () => false,
});
```

Two things are being decided there. It is on the shell because **every write to `UiStore` goes to `localStorage`**, and this is a gesture rather than a preference; keeping it here also keeps it off the initial bundle graph, which the root-scoped store is on. And it is a `linkedSignal` rather than a plain signal reset from an `effect` because a rotation or a resize is not a second gesture, and carrying the overlay across a breakpoint would change what it _is_ — at mobile it is a modal dialog whose focus returns to a hamburger that does not exist at tablet. An effect would work, but its reset would land one pass _after_ `mode()` changed, and `tabletOverlayOpen()` reads both synchronously: for that window a drawer open at mobile would read as a tablet overlay and `<main>` would take `inert`. Deriving it makes the reset atomic with the mode read, so no ordering has to hold.

**There is exactly one `<app-sidebar>` declaration, rendered at one of two outlets.** Both of `Sidebar`'s branches emit `<nav id="sidebar-primary" aria-label="Primary">`, so a second instance would put two elements with one id and two navigations with one name in the document — `duplicate-id` at serious impact, and two "Primary" landmarks for anyone navigating by them. One `<ng-template>` and an `ngTemplateOutlet` is what makes that structural; two call sites guarded by two conditions is two places for a later edit to break it. The drawer's copy is additionally `@if`-guarded, because `Offcanvas`'s `<dialog>` is unconditionally in the DOM and its projected content would otherwise exist at every width, open or not.

The skip link picked up one consequence. It is a sibling of `<main>` rather than a descendant, so it is not inerted with it — and while the tablet overlay is up, following it would try to focus an inert element and do nothing at all, silently. It therefore dismisses the overlay first, then focuses `<main>` on the render that removes the attribute: writing the signal only _schedules_ change detection, so focusing in the same task would still hit the inert element.

### 5.6 `ShellChrome` — what a routed screen puts in the mobile navbar

Below 768px the shell draws frame `1d`'s 54px bar, and it is the only way to reach the sidebar at that width. Its hamburger is the shell's, because the sidebar is; its **title and trailing controls belong to the routed screen** — chat publishes its favourite star and conversation kebab, `AdminLayout` publishes the title "Admin" and nothing else.

`core/ui/shell-chrome.ts` is that seam, and it is the same seam `COMPOSER_HOST` is: the two halves live in `features/shell` and `features/chat`, dependencies run one way, and neither feature may import the other — so the contract sits below both. It carries a `title` and a `TemplateRef`, is provided **at the `Shell` component** so it rides the lazy chunk rather than the initial graph, and is injected `{ optional: true }` because every route spec renders these screens with no shell around them. A screen that publishes **must** `clear()` on destroy, or the chat kebab outlives the chat route and appears over the projects screen.

This retires the last of the three criteria deferred out of US-308 in EP-3.

### 5.7 Favourite projects in the sidebar (US-910)

Frame `Sidebar` lines 115–116: a second rule and a **Favorite projects** section between the nav and the conversation list, expanded by default and collapsible. `SidebarFavorites` (`features/shell/sidebar/favorite-projects.ts`) owns it, for the reason `ConversationRow` is its own component — it holds state (which nodes are expanded) and `sidebar.scss` was already at its per-component budget when this section arrived. `SidebarProjectRow` is one favourited project and its disclosure; `SidebarProjectConversations` is the conversations under an expanded one.

**The rows read the server flag, not a device-local pin.** US-910's first criterion described `localStorage` pins under a "Pinned on this device — pins don't sync." notice, for the world where the backend enabler (US-909) had not landed. US-909 shipped in the same batch, so that state was never live for a build and the notice was never written — the same call US-908's own interim ceiling made when its enabler (US-907) landed first. Pins were also the wrong place for this story to reach `UiStore` for: `local-preferences.ts` and the store built on it hold facts about the **browser**, and a list of project ids is a fact about the **user**.

**The rows cost no request of their own.** They come from `ProjectLookupStore.favorites` — a `computed` filter over the root lookup `SessionBootstrap` already releases for the picker and the project chips (§9.1 of [Projects (UI)](projects.md#91-the-lookup-behind-it-and-why-a-failed-drain-keeps-its-pages)) — rather than a dedicated `?isFavorite=true` request. One source of truth beats a second copy of rows that would need to be kept in step with every rename, delete and toggle. **Since US-706, the lookup itself drains `sort=favorite&dir=desc`** rather than the server's default order, which is what makes this section **complete within the 500-project ceiling rather than best-effort**: every starred project sorts inside the drain's first pages, so a favourite is missing only if the reader has starred more than 500 projects — the one case the picker's own `ceilingNotice` still names, since a second wording for the same limit would drift from it. The whole section — the strip's star included — is **absent rather than empty** when nothing is starred, the repo's rule for an affordance with nothing to act on.

**Three siblings, not one nested `<a>`**, on `SidebarProjectRow`: the anchor opens the project, a disclosure button opens the node's conversations, and a kebab offers unfavourite, rename and delete. An interactive element inside an `<a>` is invalid HTML and splits the accessible name — the same reasoning `ConversationRow` already follows. `SidebarProjectConversations` **provides its own `ProjectConversationsStore` per expanded node**, mounted behind the row's `@if`, so several nodes can be open at once and a collapsed one costs nothing; it reads the same `core/projects/` store the project detail screen's Conversations tab uses (§10.1 of [Projects (UI)](projects.md#101-one-filtered-request-and-the-criterion-that-never-shipped)), which is why that store lives in `core/` rather than under `features/projects/detail/` — `features/shell` may not import `features/projects`.

**Unfavourite is deliberately not optimistic**, unlike the conversation star. `ProjectActionsStore` is root-scoped in `core/` and has no method channel into `ProjectLookupStore`, which lives beside it but is a different store; the only pre-flight event available would be `projectEvents.updated` carrying a fabricated `ProjectDto` — and `ProjectStore` on the detail screen adopts that DTO whole, which would blank the standing instructions US-904 exists to keep. So a fourth project event, `projectEvents.favorited`, carries `{ id, isFavorite }` on the 204 only, and every observer — this store, the grid, the detail header — waits for it rather than guessing. The round trip is covered by the pending-id flag instead, exactly as every other project write in this store is.

**Focus is rescued across the round trip.** Unfavourite or delete from a sidebar row destroys the `<li>` holding the menu trigger focus was returned to, roughly a round trip after the click — `Menu`'s own rescue cannot help, because it is destroyed along with the row. `SidebarFavorites` records which project is being evicted *before* the request goes out, and an `effect` watching both the lookup store's entities and the actions store's pending ids moves focus to the section heading once the row is confirmed gone. When that was the **last** favourite, the section itself goes with the row, so there is nothing left to focus locally — the component emits `lastFavoriteRemoved` instead, and the sidebar focuses its search field, the control the section used to sit beneath.

The card star (US-901's deferred criterion) and the project detail header's star (frame `4e`, deferred out of US-904) both turned on in the same batch, reading and writing the same flag and event through `ProjectActionsStore.toggleFavorite`. `ProjectCard`'s star switched from a native `[disabled]` to `[attr.aria-disabled]` while it was touched: the store sets the pending id synchronously with the click, and a native `disabled` blurred the button the reader had just pressed, dropping them onto `<body>` for the round trip.

**The collapsed 60px strip** gains a `bi-star` entry, tooltipped, below the nav rules — present only while `hasFavorites()` is true, on the same absent-rather-than-inert rule as the section itself. Pressing it expands the sidebar and hands focus to the revealed section's heading through `SidebarFavorites.reveal()`, the same `afterNextRender`-with-injector shape the search button already uses to expand-then-focus (§5.3).

## 6. `UiStore` — a preference about the browser, not the user

```ts
export const UiStore = signalStore(
  { providedIn: "root" },
  withState<UiState>(() => ({ sidebarCollapsed: readStoredCollapsed() })),
  withMethods(/* setSidebarCollapsed, toggleSidebar */),
);
```

**It deliberately does not compose `withResetOnSignOut`**, and that is the one thing to know before editing it. Every other store in the app composes it last; this one must not, because US-205 requires the sidebar preference to be one of exactly **two** things that survive sign-out, alongside the theme. Clearing it would put the next person at this machine into a layout they did not choose — and would contradict the build gate that enforces the same rule from the other side.

The rule behind that: `localStorage` holds only what is about the **machine, not the person**. `PREFERENCE_KEYS.sidebar` (`egpt.sidebar`) is one of the two allowlisted keys, and `scripts/check-forbidden-apis.mjs` fails the build on a `localStorage.setItem` anywhere outside [`core/storage/local-preferences.ts`](../../enterprise-gpt-ui/src/app/core/storage/local-preferences.ts) ([Authentication and Session §11.6](authentication-and-session.md#116-what-survives-and-how-that-is-enforced)).

Anything other than the `collapsed` marker — unset, corrupted, or storage the browser refuses to open — reads as expanded, which is the state that shows the user where everything is.

**Since US-1403 it is read and written at desktop only** (§5.5). The tablet chevron opens a transient overlay held on the shell instead, because every write here goes to `localStorage` and a gesture on a tablet must not change the width the same user meets on their laptop.

It is a store rather than a one-field service on the strength of that second allowlisted key alone: US-910's favourite-projects section (§5.7) was expected to land device-local pins here, but US-909 shipped in the same batch and the pins were never built — the section reads the server flag through `ProjectLookupStore` instead, and `UiStore` gained nothing from that story.

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

  if (first?.path !== "chat" || rest.length > 0) return null;
  if (second === undefined) return { consumed: [first] };

  return CONVERSATION_ID.test(second.path)
    ? {
        consumed: [first, second],
        posParams: { [CONVERSATION_ID_PARAM]: second },
      }
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
- **The `null` case is handled _inside_ `switchMap`, not filtered ahead of it.** Filtering would leave the request for the conversation _just closed_ still running, because `switchMap` would never see the value that cancels it. The null arm returns `EMPTY`, which cancels it.
- **`name` falls back to the sidebar's copy** while the detail request is in flight (`_list.entityMap()[id]?.name`), so opening a conversation from the list shows its title immediately instead of flashing a placeholder for a round trip. It is empty only for a deep link to a row the list has not loaded — where the skeleton sits _inside_ the `<h1>` rather than replacing it, so the document is never without a level-1 heading.
- **On success it calls `_list.refreshRow(conversation)`**, keeping the sidebar in step with a name the server changed out of band. That is the seam US-409 uses.

US-401 (send), US-409 (the generated name) and US-410 (restore model and MCP selection) all extended this store rather than adding another. Two things came with them: a **silent** `refreshDetail` that updates the header and the sidebar row without going pending — `open` would blank the header and flash the stale name back — and the seed into `TurnSettingsStore` on the detail response ([Turn Lifecycle §7.6, §7.8](../conversations/turn-lifecycle.md#76-restoring-the-model-and-mcp-selection)).

### 7.3 The empty state (US-303), and what was deferred

"New conversation" is a **`routerLink` to `/chat`** — a link, not a button. There is no `POST api/conversations` to suppress, because nothing is created until the first prompt (US-401). That is the whole story: an abandoned idea leaves nothing in the sidebar.

The centred wordmark and "How can I help you today?" are built. Two things about it are deliberate:

- **The suggested prompt chips are deferred to US-401**, agreed with the product owner on 2026-08-11. A chip's only behaviour is to seed the composer's text, and the composer is EP-4 — shipping them now would put four controls on screen that visibly do nothing. The same reasoning settled the criterion's phrase "renders an empty composer": the composer arrives whole in US-401–403 rather than as an inert box.
- **Nothing moves focus on arrival.** This is the application's landing screen rather than a state transition, and taking focus would jump a keyboard user past the shell's skip link on every cold start — the opposite of the rule the auth pages follow, where focus _is_ moved because each of those is a transition ([Authentication and Session §4.2](authentication-and-session.md#42-the-five-routes)).

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

|                             | Initial raw   | Initial transfer | Budget (warn / error) |
| --------------------------- | ------------- | ---------------- | --------------------- |
| End of EP-2 (US-205)        | 637.10 kB     | 153.38 kB        | 650 kB / 720 kB       |
| End of EP-3 (US-303)        | 643.62 kB     | 158.05 kB        | 660 kB / 720 kB       |
| End of EP-4/EP-5 (US-501)   | 648.75 kB     | 157.96 kB        | 660 kB / 720 kB       |
| After EP-6's US-601/602/606 | 660.24 kB     | 161.01 kB        | **665 kB / 720 kB**   |
| **After EP-5 and US-603**   | **661.36 kB** | **161.19 kB**    | 665 kB / 720 kB       |

EP-3 cost **6.52 kB** of initial graph — the two root stores, the conversation DTOs and `canRetry` — because the chrome itself is behind the lazy shell route and the chat screen behind its own. The warning threshold moved 650 → 660 kB to keep headroom above the new baseline; the **failure** threshold is unchanged at 720 kB, which is the number that matters. The `styles` budget is unchanged at 65 / 80 kB.

**The gate at US-601 is closed, and both routes being lazy is what closed it.** The transcript renderer went behind the lazy chat route rather than into the initial graph: the whole 124.8 kB markdown stack rides the `chat` chunk (48.60 → 179.47 kB) and none of it is initial. The ~11.5 kB the initial graph did grow by is Angular's own `DomSanitizerImpl`, which `ngx-markdown`'s `MarkdownService` drags into a chunk shared with initial code, plus ~1.6 kB of global CSS — which is why the **warning** line moved 660 → 665 kB while the ceiling stayed at 720 kB. Full accounting in [Answer Rendering §6](answer-rendering.md#6-the-bundle-gate-resolved).

EP-5's four stories and US-603 then cost **1.12 kB** of initial graph between them, and **no threshold moved**. All of it is global CSS for the code-block chrome — `_markdown.scss` is in the `styles.scss` chain, so it is initial however lazy the renderer is, and the `styles` bundle went 62.08 → 63.19 kB against its unchanged 65 / 80 kB budget. Every component and reducer those five stories added rides the `chat` chunk, which grew 179.47 → 198.51 kB.

## 10. Testing

The suite was **610 specs** across 55 files when EP-3 landed — 1750 in jsdom today, plus 32 browser audits in the separate `test-a11y` target — with `npm run lint` and `npm run build` green alongside.

| Area                  | Spec                           | Notable cases                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| --------------------- | ------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The list store        | `conversation-list-store` (20) | `name` omitted on the first load and never sent as `filter`; nothing issued until the bootstrap releases it; the newest query winning over one in flight; a narrowed search replacing rather than appending; the offset advancing by what actually arrived; a second Load more ignored; **Load more refused while the result set beneath it is being replaced**; a page dropped after a fresh search superseded it; a failed further page keeping its rows and raising a toast; `refreshRow` ignoring a row it does not hold; `prependNewest` keeping `skip` and `totalCount` in step; detail-only fields kept out of the entities; the store emptying on sign-out and reloading for the next user |
| The shell and sidebar | `shell` (22)                   | The skip link ahead of everything in the tab order; skeleton versus empty state versus no-matches versus error panel; the list scrolling on its own; Load more offered only while the server says there is more; the search field reaching the store after the debounce; the Admin entry absent for a non-administrator; the strip's expand-and-focus; the collapse surviving a reload; the width transition withheld for the first render; every icon-only control in the strip named                                                                                                                                                                                                             |
| The chat route        | `chat` (13)                    | The matcher on all four shapes, including refusing `/chat/garbage`; no header on the bare route; **the component instance surviving a move between `/chat` and `/chat/{id}`**; the request cancelled when the id goes away; the name borrowed from the sidebar while the detail is in flight; a 404 offering no Retry; the sidebar row refreshed from the detail response                                                                                                                                                                                                                                                                                                                          |
| Layout preference     | `ui-store` (5)                 | Expanded when nothing is stored; an unrecognised stored value treated as expanded; the one allowlisted key; **the preference surviving sign-out**                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| The three modes       | `shell-responsive` (10)        | The tablet strip defaulting **whatever the stored preference says**; the overlay never writing that preference; `inert` on the main content while it is up; a backdrop press collapsing it and returning focus to the strip; the navbar with no sidebar in the document until the drawer opens; opening from the hamburger, focusing the close control and returning focus to it; the sidebar's chevron suppressed inside the drawer; the phone frame on a **cold load**; the overlay dismissed when the viewport crosses a breakpoint |
| The navbar slot       | `mobile-navbar` (4)            | The product name until a screen publishes one; published controls rendered, and none when none is published; the hamburger named and saying what it opens; the brand text kept out of the heading outline |
| The frame, in Chromium | `shell.a11y` (13)             | axe at three widths x two themes with `region` **enabled** — the one audit that renders real landmarks; no horizontal overflow at the same six; one navigation landmark whatever the width ([Accessibility audit](accessibility-audit.md)) |

[`src/testing/conversations.ts`](../../enterprise-gpt-ui/src/testing/conversations.ts) carries the fixtures. Two details in it are load-bearing: ids are sequential with **no modulo**, because a repeating id collapses entities silently (`setAllEntities` keeps one, `addEntities` no-ops) and then trips NG0955 on `@for … track id`; and `conversationPage()` computes `pageSize` and `currentPage` the way the server does, so a store cannot pass against an envelope the API never sends.

```bash
# from enterprise-gpt-ui/
npm test        # Vitest, single run
npm run lint    # ESLint + check:icons + check:forbidden + check:tokens
npm run build   # budgets, then check-initial-chunk.mjs
```

> **Verification status.** As with EP-2, none of this has been exercised against a live API or a real Entra tenant — the API does not start in this environment. The gates above are green and every behaviour here is asserted against `HttpTestingController` and `RouterTestingHarness`.

## 11. What is not here yet

Written at the end of EP-3; the first four rows have since been struck through by the stories that owned them.

| Missing                                                        | Owner                  | Notes                                                                                                                                                                       |
| -------------------------------------------------------------- | ---------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| ~~Rename, favourite, delete on a row~~                         | US-304, US-305, US-306, US-307 | Shipped — [Conversation Actions](conversation-actions.md). US-307's move and remove closed the row kebab: it now carries all five actions frame `3a` draws               |
| ~~The conversation header's star and kebab~~                   | US-308, US-305, US-307, US-1403 | Shipped; the star arrived with US-305, the two project items with US-307, and the sub-768 px collapse with US-1403 — the controls move into frame `1d`'s navbar through `ShellChrome` (§5.6) rather than disappearing with the bar that held them |
| ~~The composer, the transcript, and the streamed turn~~        | EP-4, EP-5, EP-6       | Shipped — [Turn Lifecycle](../conversations/turn-lifecycle.md) and [Answer Rendering](answer-rendering.md). EP-4 closed with US-408–US-413; EP-5 and EP-6 have stories left |
| ~~The suggested prompt chips on the empty state~~              | US-401                 | Shipped there, as US-303 deferred them (§7.3)                                                                                                                               |
| ~~The Conversations nav entry~~                                | US-701                 | Shipped with the route it points at — [Conversation Library](conversation-library.md)                                                                                       |
| ~~The Projects and Documents nav entries~~                     | EP-9, EP-10            | Shipped with their routes — Projects with US-901 ([Projects](projects.md)), Documents with US-1002 ([Documents Library](documents-library.md)); `navItems` renders only routes that exist |
| ~~The **Favorite projects** section between the nav and the list~~ | US-910                 | Shipped, 2026-08-20, alongside its enabler US-909 — reading the server flag rather than the device-local pins the criterion described for the case US-909 landed second (§5.7)                                                       |
| ~~`isFavorite` on the search request~~                         | US-703                 | Shipped on the conversations library, not the sidebar — [Conversation Library §2.2](conversation-library.md#22-the-three-url-parameters). The sidebar still has no control to undo such a filter, so it still never sends it |
| ~~Server-side sort on the list~~                                   | US-706                 | Shipped, 2026-08-20 — but not here: `GET api/conversations/search` accepts `sort=`/`dir=` now, and the `/conversations` library sends them, but this store still sends neither, deliberately — the sidebar is a fixed recency list, and the reader who wants another order has `/conversations` (§3.1) |

## 12. Troubleshooting

| Symptom                                                                             | Likely cause                                                                                                                                                                                             |
| ----------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Every search returns the full list                                                  | The request sent `filter=` instead of `name=`. The server binds nothing and answers unfiltered (§3.1)                                                                                                    |
| A search feels sluggish by about a third of a second                                | A second `debounceTime` was added in the store. `SearchInput` already debounces (§4.3)                                                                                                                   |
| Retry on the sidebar's error panel does nothing                                     | Something added `distinctUntilChanged`, which swallows the deliberate duplicate (§4.3)                                                                                                                   |
| Rows appear that the user cannot scroll to, and Load more has vanished              | A page landed against a result set it does not belong to. Check both rules in §4.4 are intact                                                                                                            |
| The list is empty after signing in as a second user                                 | Only if `withResetOnSignOut` moved off the end of the store, or `takeUntil(signedOut$)` came off the request ([Frontend Foundation §5.5](frontend-foundation.md#55-withresetonsignout--compose-it-last)) |
| A conversation renamed elsewhere does not update in the sidebar                     | `refreshRow` ignores a row the list does not hold — check it is loaded, not that the call failed (§4.5)                                                                                                  |
| Nothing is ever requested for the list                                              | `ensureLoaded()` was not called, or was moved ahead of the awaited session in `SessionBootstrap` (§4.2)                                                                                                  |
| A new conversation lands in the wrong place in the list                             | `prependNewest` is correct only while the server orders by `dateCreated` descending (§3.1)                                                                                                               |
| The composer's contents are lost the moment the first prompt creates a conversation | The chat route was split into two configs, so the router remounts. It must stay one `matcher` route (§7.1)                                                                                               |
| `/chat/{id}` renders the empty state                                                | The id is not a GUID, so the matcher declined and the wildcard sent the URL to `/chat` (§7.1)                                                                                                            |
| A project or model picker reads as unset on a conversation that has both            | Something hydrated it from `GET api/conversations/{id}/messages` (§3.2)                                                                                                                                  |
| The sidebar animates shut on every page load                                        | The `afterNextRender` flag that withholds the transition was removed (§5.4)                                                                                                                              |
| The sidebar collapse is forgotten after signing out                                 | `withResetOnSignOut` was composed into `UiStore`. It must not be (§6)                                                                                                                                    |
| Expanding the sidebar on a tablet changes its width on the desktop next time        | The tablet chevron reached `UiStore`. The persisted preference is desktop-only; the overlay is the shell's own `linkedSignal` (§5.5)                                                                     |
| The tablet overlay stays open across a rotation, or `<main>` is inert on a phone     | `_overlayOpen` was changed from a `linkedSignal` to a signal reset from an `effect`, which lands one pass late (§5.5)                                                                                    |
| axe reports `duplicate-id` on `sidebar-primary`, or two "Primary" landmarks          | A second `<app-sidebar>` was declared, or the drawer's `@if` guard came off — the `<dialog>` is always in the DOM (§5.5)                                                                                 |
| The chat kebab appears over the projects screen                                      | A screen published into `ShellChrome` and did not `clear()` on destroy (§5.6)                                                                                                                            |
| The skip link puts `#main-content` in the URL and re-runs the guards                | Its click handler was removed; `PathLocationStrategy` listens for `hashchange` (§5.2)                                                                                                                    |
| The collapsed strip's tooltip renders in the wrong place                            | `Tooltip`'s flyout is `position: fixed`, placed by the directive from the host's measured box on every render — it no longer depends on the sidebar host's `overflow` or a CSS offset ([Design System §7.2](design-system.md#72-overlays--sharedoverlay))                                             |
| The whole sidebar scrolls instead of the list                                       | The `min-height: 0` came off the list's flex chain (§5.3)                                                                                                                                                |

## 13. Key files

| Concern                                        | File                                                                                                                                                                                                                                                                                                                                      |
| ---------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The reference list store                       | [`core/conversations/conversation-list-store.ts`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-list-store.ts)                                                                                                                                                                                                          |
| Layout preferences                             | [`core/ui/ui-store.ts`](../../enterprise-gpt-ui/src/app/core/ui/ui-store.ts)                                                                                                                                                                                                                                                              |
| The signed-in frame                            | [`features/shell/shell.ts`](../../enterprise-gpt-ui/src/app/features/shell/shell.ts)                                                                                                                                                                                                                                                      |
| Sidebar, three modes                           | [`features/shell/sidebar/sidebar.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.ts), [`sidebar.html`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.html)                                                                                                                                           |
| The mobile navbar and its seam                  | [`features/shell/mobile-navbar/`](../../enterprise-gpt-ui/src/app/features/shell/mobile-navbar/), [`core/ui/shell-chrome.ts`](../../enterprise-gpt-ui/src/app/core/ui/shell-chrome.ts)                                                                                                                                                    |
| The breakpoints                                | [`shared/layout/breakpoints.ts`](../../enterprise-gpt-ui/src/app/shared/layout/breakpoints.ts), [`src/styles/_breakpoints.scss`](../../enterprise-gpt-ui/src/styles/_breakpoints.scss)                                                                                                                                                    |
| One row                                        | [`features/shell/sidebar/conversation-row.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/conversation-row.ts)                                                                                                                                                                                                                |
| Favourite projects section, row and nested conversations (US-910) | [`features/shell/sidebar/favorite-projects.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/favorite-projects.ts), [`project-row.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/project-row.ts), [`project-conversations.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/project-conversations.ts) |
| The chat URL matcher                           | [`features/chat/chat-route.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat-route.ts)                                                                                                                                                                                                                                              |
| The open conversation                          | [`features/chat/conversation-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/conversation-store.ts)                                                                                                                                                                                                                              |
| Chat screen and empty state                    | [`features/chat/chat.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat.ts), [`chat-empty-state.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat-empty-state.ts)                                                                                                                                                              |
| Route table                                    | [`src/app/app.routes.ts`](../../enterprise-gpt-ui/src/app/app.routes.ts)                                                                                                                                                                                                                                                                  |
| Bootstrap sequencing                           | [`core/session/session-bootstrap.ts`](../../enterprise-gpt-ui/src/app/core/session/session-bootstrap.ts)                                                                                                                                                                                                                                  |
| Conversation DTOs, and the `/messages` warning | [`domain/api/conversation.ts`](../../enterprise-gpt-ui/src/app/domain/api/conversation.ts)                                                                                                                                                                                                                                                |
| Retry policy for a person, not the interceptor | [`core/errors/error-message.ts`](../../enterprise-gpt-ui/src/app/core/errors/error-message.ts)                                                                                                                                                                                                                                            |
| Fixtures                                       | [`src/testing/conversations.ts`](../../enterprise-gpt-ui/src/testing/conversations.ts)                                                                                                                                                                                                                                                    |
| The API side of the list                       | [`Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs), [`ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs)                                                                                             |
| Related reference                              | [Conversation Library](conversation-library.md), [Frontend Foundation](frontend-foundation.md), [Authentication and Session](authentication-and-session.md), [Design System](design-system.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md), [Conversation usage and favorites](../conversations/usage-and-favorites.md) |
