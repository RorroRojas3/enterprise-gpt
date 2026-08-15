# Conversation Library

The `/conversations` screen: a searchable, paged, filterable table of everything a user has asked, the app's **second** conversation list store, and the URL contract that makes a filtered list shareable, the back button meaningful, and a deep link one request rather than two.

Audience: a developer adding the one control this screen still owes (US-706's sort), adding a second route-scoped list anywhere in the app, or debugging a search that will not stick, a page that leaves a hole, or a bulk delete that half-happens. Read [Shell and Navigation](shell-and-navigation.md) first for `ConversationListStore` — the reference store this one copies — [Conversation Actions](conversation-actions.md) for the shared favourite, move and delete flows it invokes, and [Frontend Foundation](frontend-foundation.md) for the composable features all of them compose. Bare `§` references below are to sections of _this_ page.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference.

## 1. Overview

**The server-side `name=` search already existed.** US-302 built it for the sidebar, and no story here changed or extended it. What EP-7 delivered is the screen the sidebar had been deliberately withholding a nav entry for, the URL contract underneath it, and — across four stories that each look small on their own — the paging, filtering, selection and honesty the frame draws around it.

| Story | What it delivers |
| --- | --- |
| **US-701** | Frame `4a`'s conversations screen, a route-scoped `ConversationLibraryStore`, the `?name=` URL contract, and the sidebar nav entry that was absent until a route existed to point it at |
| **US-702** | Paging: the toolbar's "Showing N of M" counter, a **Load more** control in the table's footer slot, and the two guards that stop a page landing in a result set it does not belong to |
| **US-703** | The favourites filter, as a second URL parameter (`?favorites=1`), a toolbar toggle and a per-row star — plus the eviction rule that takes an unstarred row out of a favourites-only list |
| **US-704** | Row selection, toolbar select-all, the floating bulk bar and `DELETE api/conversations/bulk` behind a confirmation, with an optimistic removal that restores on failure |
| **US-705** | The order **stated** rather than inferred: a muted "Newest first" carrying its explanation, and tests that lock the absence of any sort control |
| **US-307** | Not an EP-7 story, and the one that finished frame `4a`: the project chip on a row and the per-row kebab, both waiting on a project surface that did not exist until EP-9 (§5.2) |

Six decisions shape everything here, and each looks removable until you know what it prevents:

1. **The screen gets a store of its own**, not the root `ConversationListStore`. That is US-701's one architectural decision, and §4.1 is the whole of it.
2. **The URL is the source of truth** for both parameters, the controls are a view of it, and the store caches the rows it produced. One writer, one reader path — after which the back button restoring the previous result set is a property of the router rather than code anyone wrote (§3).
3. **Refining a term replaces the history entry; adding or removing one pushes.** Both extremes fail in their own way, and this is the only rule that fails neither. The favourites toggle reuses it, and both of its transitions push (§3.1).
4. **A row leaving the list is one operation, not two.** It shifts `skip` _and_ `totalCount`, and it cancels any page already on the wire — `_dropRow` is the single place that does both, and every departure route goes through it (§4.4).
5. **The bulk delete request lives in the root `ConversationActionsStore`**, not in this store. `rxMethod` binds its subscription to the declaring injector, and this one dies with its route (§4.6).
6. **The order is stated, not inferred.** No endpoint here accepts a sort parameter, so the toolbar says "Newest first" and explains why, rather than leaving the reader to deduce it from a missing control (§5.1). US-706 replaces the statement with a control.

### 1.1 Where each piece lives

| Concern | Where |
| --- | --- |
| The screen | [`features/conversations/conversations.ts`](../../enterprise-gpt-ui/src/app/features/conversations/conversations.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/conversations/conversations.html), [`.scss`](../../enterprise-gpt-ui/src/app/features/conversations/conversations.scss) |
| The route-scoped store | [`features/conversations/conversation-library-store.ts`](../../enterprise-gpt-ui/src/app/features/conversations/conversation-library-store.ts) |
| The bulk-delete confirmation | [`features/conversations/delete-conversations-dialog.ts`](../../enterprise-gpt-ui/src/app/features/conversations/delete-conversations-dialog.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/conversations/delete-conversations-dialog.html) |
| The two parameter names, the history rule, the order statement | [`features/conversations/conversations-route.ts`](../../enterprise-gpt-ui/src/app/features/conversations/conversations-route.ts) |
| The bulk delete request, the favourite toggle, the project move, `pendingIds` | [`core/conversations/conversation-actions-store.ts`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-actions-store.ts) — [Conversation Actions](conversation-actions.md) |
| The name behind a row's `projectId`, and the picker its kebab opens | [`core/projects/project-lookup-store.ts`](../../enterprise-gpt-ui/src/app/core/projects/project-lookup-store.ts), [`shared/projects/project-picker/`](../../enterprise-gpt-ui/src/app/shared/projects/project-picker/) — [Projects §9](projects.md#9-the-project-picker-us-307) |
| The lazy route and its title | [`src/app/app.routes.ts`](../../enterprise-gpt-ui/src/app/app.routes.ts) |
| `CONVERSATIONS_ROUTE` | [`core/auth/auth-routes.ts`](../../enterprise-gpt-ui/src/app/core/auth/auth-routes.ts) |
| The nav entry | [`features/shell/sidebar/sidebar.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.ts) |
| The list this one is _not_ | [`core/conversations/conversation-list-store.ts`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-list-store.ts) — the reference store ([Shell and Navigation §4](shell-and-navigation.md)) |
| The events that keep the two in step | [`core/events/conversation-events.ts`](../../enterprise-gpt-ui/src/app/core/events/conversation-events.ts) |
| The table, the bulk bar, the search field, the empty state | [`shared/data/data-table/`](../../enterprise-gpt-ui/src/app/shared/data/data-table/), [`shared/data/bulk-action-bar/`](../../enterprise-gpt-ui/src/app/shared/data/bulk-action-bar/), [`shared/form/search-input/`](../../enterprise-gpt-ui/src/app/shared/form/search-input/), [`shared/feedback/empty-state/`](../../enterprise-gpt-ui/src/app/shared/feedback/empty-state/) |

## 2. Quick start

### 2.1 Reading the library

The store is provided by the `Conversations` component, so it exists only while the route does:

```ts
protected readonly library = inject(ConversationLibraryStore);
protected readonly actions = inject(ConversationActionsStore);

//   library.entities()        → readonly ConversationDto[], in the server's order
//   library.name()            → the term the rows on screen belong to
//   library.favorites()       → the filter the rows on screen belong to
//   library.isPending()       → a *query* is in flight (the rows are being replaced)
//   library.loadingMore()     → a *page* is in flight (the rows are being appended to)
//   library.error()           → AppError | null
//   library.isEmpty()         → loaded, and there is genuinely nothing
//   library.hasNoMatches()    → loaded, nothing matched, and a term is why
//   library.hasNoFavorites()  → loaded, nothing matched, and only the filter is on
//   library.isFirstLoad()     → pending with nothing on screen — a skeleton is honest
//   library.hasMore()         → from withOffsetPagination; drives the Load more control
//   library.countSummary()    → "Showing 25 of 312 conversations"
//   library.selectedIds()     → the checked rows, pruned to what is still loaded
//   library.selectedCount() / .hasSelection() / .allLoadedSelected() / .someLoadedSelected()
//   library.selectAllNote()   → "Select-all covers the 25 loaded rows only"
//
//   library.bindQuery(computation);  library.retry();  library.loadMore();
//   library.setSelection(ids);  library.toggleSelectAll();  library.clearSelection();
//   library.deleteSelected();
```

There is no `search(term)` and no `setFavorites(on)`, and that absence is the design: **the only way to change the query is to navigate** (§3).

### 2.2 The two URL parameters

| Parameter | Written as | History | On the wire |
| --- | --- | --- | --- |
| `?name=` | The trimmed term, or the key **removed** when it is empty | Replaces while a term is refined; pushes when one is added or removed | `name=` when non-empty, omitted otherwise — never `filter=` |
| `?favorites=` | Exactly `1`, or the key **removed** | Always pushes: both of its transitions are an add or a remove | `isFavorite=true`, and **never** `isFavorite=false` |

Three things about that table are decisions rather than incidentals.

**Both parameters go out on one request.** `bindQuery` takes a single `ConversationLibraryQuery { name, favorites }` computation, so a deep link carrying both issues one query rather than an unfiltered request followed by a filtered one. Binding them as two `rxMethod`s would fire a second query for every change to either.

**`favorites=1`, not the API's own `isFavorite=true`.** The URL is a contract with the reader, not a proxy for the request underneath it. `isFavorite=false` is a meaningful value on the wire — "only the ones I have _not_ starred" — that this screen has no control for and no way back out of, so a URL that could spell it would invite a link the client cannot honour. `searchUrl` is the one place the translation happens, and it only ever sets `true`.

**Both inputs are defensively transformed.** The router writes `undefined` when a key leaves the URL, so `name` trims and falls back to `''`, and a hand-edited `?name=%20%20` means unfiltered. `favorites` reads `value === FAVORITES_ON`, so `?favorites=yes` is off rather than putting the toolbar and the request out of step.

Adding a third parameter is additive, and the seams are already in place: `queryParamsHandling: 'merge'` on `_applyQuery` means it survives the other two and they survive it; `shouldReplaceHistory` takes a before and an after rather than reading the screen, so a discrete toggle reuses it by passing its own two values; and `searchUrl` is the one place a request parameter is added. Bind the new input the same way — one `input()` fed by `withComponentInputBinding()`, folded into the query object handed to `bindQuery` as a **computation** — and never navigate from an effect over it (§3).

## 3. The URL is the source of truth

The loop is deliberately one-directional, and each hop is somewhere different:

```text
user types   →  SearchInput debounce (300 ms)  →  (searched)  ┐
user toggles →  Favourites button              →  (click)     ├→ _applyQuery → router.navigate
                                                              ┘        ↓
   rows ← store ← bindQuery(() => ({ name, favorites })) ← input()s ← withComponentInputBinding()
```

`Conversations.name` and `Conversations.favorites` are `input()`s bound from the query string by `withComponentInputBinding()`. `_applyQuery` is the **only** writer, and it writes to the router, never to the store — `onSearched`, `toggleFavorites`, `clearSearch` and `clearFavorites` all go through it.

Three consequences fall out of that rather than being implemented:

- **A filtered list is shareable**, because both filters are in the address bar.
- **The back button restores the previous result set**, because the router restores the parameters and the parameters are what the store runs.
- **A cleared filter removes its key** rather than leaving `?name=` or `?favorites=` behind — `queryParams: { name: null }`.

**`bindQuery` takes a computation, not a value**, and that is what makes a deep link _one_ request. `rxMethod` subscribes through an effect, so it reads the parameters the router has already bound; handing over `{ name: name(), favorites: favorites() }` would have fired an unfiltered request first and a filtered one after it.

**The loop cannot feed itself.** A navigation changes `name`, which re-seeds `SearchInput`'s `linkedSignal`; its debounce then compares the new text against the value it already holds, finds them equal, and emits nothing. Two rules keep that true, and both are stated in the component:

- **Bind `[value]` one-way, never `[(value)]`.**
- **Never navigate from an effect over `name()`** — only from the user's own gesture.

### 3.1 Refining replaces; adding or removing pushes

```ts
export function shouldReplaceHistory(previous: string, next: string): boolean {
  return previous !== "" && next !== "";
}
```

Both extremes fail US-701's third criterion in their own way. **Always replacing** means the back button leaves the screen instead of restoring the unfiltered list. **Always pushing** walks the reader back through their own typing one debounce at a time — `quarterl`, `quarter`, `quart`, and so on.

Pushing at exactly the two transitions a reader thinks of as a state change — filter on, filter off — gives `/conversations` → `?name=quarterly` → back → `/conversations`, and makes _clearing_ a search undoable too.

**The favourites toggle never replaces.** A discrete on/off is always the add-or-remove transition this function exists to push at — one of its two arguments would always be empty — so `toggleFavorites` passes `false` outright, and back undoes turning the filter on as well as turning it off.

## 4. The second store

[`ConversationLibraryStore`](../../enterprise-gpt-ui/src/app/features/conversations/conversation-library-store.ts) is provided on the component, so it is created by the route that shows it and destroyed with it.

### 4.1 Why it is not the root `ConversationListStore`

They query the same endpoint. They are not the same list, and four separate things say so:

| | Why sharing fails |
| --- | --- |
| **Frame `4a` draws both search fields at once** | The sidebar is on screen beside this table, so shared state would retype the sidebar's field and narrow its rows as the user typed here |
| **`withOffsetPagination` holds exactly one offset** | It cannot serve a 50-row sidebar and a 25-row table at the same time. `LIBRARY_PAGE_SIZE` is 25 to frame `4a`'s own count and `SIDEBAR_PAGE_SIZE` is 50 because a sidebar should rarely need a second page — two numbers chosen for two different jobs |
| **The favourites filter would narrow the sidebar** | With **no control there to undo it**, which is a dead end the user cannot get out of. US-703 shipped that filter, so this is now a fact rather than a forecast |
| **The URL is authoritative for _this route_** | A root store outlives the route, so a deep link would leave the sidebar filtered after navigating on to `/chat` |

### 4.2 What it copies, and the one thing it drops

It copies the reference store's shape _and its reasons_, which is the point of having a reference store at all:

- **`rxMethod` + `switchMap`** for the query, because typing drives it and a slow `"ang"` must not paint over a fast `"angular"`.
- **No `debounceTime`**, because `SearchInput` already debounces 300 ms and emits only committed values; a second one would add latency for nothing.
- **No `distinctUntilChanged`**, because the signal source only emits on a real change and `retry()` pushes the same query again on purpose.
- **`takeUntil(signedOut$)` on the inner request**, because `withResetOnSignOut` clears state and does **not** cancel work ([Frontend Foundation §5.5](frontend-foundation.md#55-withresetonsignout--compose-it-last)).
- **`withResetOnSignOut` composed last**, as that feature requires.
- **`name` and `favorites` are written when the request _starts_, not when it commits**, so the empty state names the query that produced it rather than the one still being typed. The component reads `library.name()` for frame `4b`'s heading, never its own input.

What it does **not** copy is the `_started` release sentinel. The sidebar's list exists before any screen asks for it, so `SessionBootstrap` has to release it; this one is created by the route that shows it and always has a query to run — even if that query is the empty term with the filter off.

### 4.3 Paging (US-702)

A **query** replaces the rows; a **page** appends to them. Keeping those apart is the whole of `loadingMore` as a separate flag from `isPending`: a second page must not blank a table the reader is already looking at, and a narrowing search must not silently become an append.

`_loadNextPage` is a second `rxMethod` with `exhaustMap` — a second press while a page is on the wire is a double-click, not a request for the page after it, and `skip` has not moved yet, so letting it through would fetch the same window twice. It patches with `addEntities` (not `setAllEntities`) and `setPage`, which advances `skip` by what actually came back rather than by `take`, so a short page cannot leave a gap.

Two details are worth stating outright:

- **A failed page raises a toast; it does not `setError`.** The rows already on screen are still valid, and swapping the table for an error panel would throw away a result set the reader can still use. A failed _first query_ still gets the panel, because those rows belong to a query that failed.
- **`loadMore()` guards on all three of `!isPending() && !loadingMore() && hasMore()`.** `isPending` is the load-bearing one. `_querySuperseded$` cancels a page a _later_ query overtook, but it cannot help a page started _after_ a query was already in flight: that request carries the old `skip` against the new result set, and when it lands after `setFirstPage` it appends a window from the middle of a list that no longer exists.

The toolbar's counter comes from `countSummary`, whose loaded half counts the rows actually on screen rather than reading `skip`. The two agree after a page, but a row deleted from anywhere decrements both counters, and counting rows keeps the string honest whichever path moved them.

### 4.4 A row leaving: `_dropRow` is the single place

Every way a row can leave this list — a delete from the sidebar, a delete from here, an unstar under the favourites filter — funnels through `_dropRow`, because a departure is two operations that must not be separated:

1. **Both counters shift down.** `totalCount`, because "Showing N of M" would otherwise claim a row that is gone; and `skip`, because the server no longer counts the row either. Leaving `skip` where it was makes the next page start one row late.
2. **`_querySuperseded$` fires**, cancelling any Load more already on the wire. That request's URL was built against the pre-removal offset, so once `skip` moves down beneath it the response appends the _next_ window — and the row that slid into the gap is fetched by neither request. That is a permanent one-row hole with the counter reading as if nothing were missing. Dropping the page is the cheaper loss: the reader can press Load more again.

`takeRows` (the bulk path) does exactly the same two things for a set of rows. The `Subject` is completed in `onDestroy`, so the route leaving does not leave a live subject behind.

### 4.5 Selection is linked state, not plain state

`selectedIds` lives in `withLinkedState` over the entity `ids`, reconciled against its own previous value:

- A **new query** replaces `ids` wholesale, and the reconciliation keeps nothing — a fresh result set starts unselected.
- A **Load more** grows `ids`, and every previously checked row is still in it, so the selection survives paging.
- A row **leaving by any route** falls out of `ids` and therefore out of the selection, with no second call site.

Written as plain state, each of those would need its own line to prune the set, and the one that got forgotten would leave the bulk bar counting rows that are not there. The trade is that `patchState` compares by reference, so every updater here returns a **new** `Set` — the same rule `withPendingIds` and `row-selection.ts` state for the same reason.

`toggleSelectAll` covers the **loaded** rows and no further; there is no endpoint that acts on a whole search. `selectAllNote` says so in the bar, with the count substituted, which is US-704's fourth criterion.

### 4.6 Bulk delete: why the request is not in this store

`deleteSelected()` does the one thing only this screen can do, and delegates the rest:

```ts
const removed = takeRows(ids);                       // optimistic, here
store._actions.deleteMany(ids, () => restoreRows(removed));   // the request, in core/
```

**The request lives in the root `ConversationActionsStore`.** `rxMethod` binds its subscription to the injector that declares it, and this store dies with its route — so a reader who confirmed a delete and then clicked a sidebar row would have had the request torn down mid-flight, with nothing deleted, nothing restored and nothing said. `deleteMany` also removes the sidebar's own rows, dispatches `conversationEvents.deleted` per id on the 204, and raises **one** toast for the batch rather than one per row.

Because `features → core` is the permitted direction and not the reverse, the rollback travels back as a **callback**: a store in `core/` cannot inject a route-scoped one, so `onRollback()` is how this screen's own optimistic removal gets undone.

Two failure-path details are load-bearing, and both were found in review:

- **The sidebar restore is LIFO.** `removeRow` captures each index against the list _as it stands at that moment_, so a batch of removals shrinks it one at a time and only a reversed restore is their inverse. Front-to-back would splice each row against a list that has already grown: `[A, B, C]` minus `[A, B]` comes back as `[B, A, C]`.
- **`restoreRows` refuses a stale restore.** `_queryGeneration` is bumped by every query, and a restore whose generation no longer matches is dropped rather than splicing the old query's rows into the new one's. When it does restore, it re-selects the rows, so Retry is one press.

The two restores run in **opposite directions**, and that asymmetry is not an oversight. `takeRows` captures every index in one pass against one list, so replaying them **ascending** is correct — each splice lands against indices the earlier ones have already restored. `ConversationListStore.removeRow` is called once per id and captures each index against the list as it stood at that moment, so only replaying them **backwards** undoes them.

`DELETE api/conversations/bulk` needs its body inside the options object — `store._http.delete<void>(url, { body: { conversationIds } })`. `HttpClient.delete` drops a body passed any other way, and the endpoint binds `[FromBody]`, so the server would see an empty id list and answer 400.

The server filters ids that are missing, foreign or already deleted and answers 204 either way, so there is no partial outcome to model.

### 4.7 Staying in step with the sidebar

Through the existing `conversationEvents`, **server-confirmed facts only**, which is the whole contract of that event group ([Shell and Navigation](shell-and-navigation.md), [Conversation Actions](conversation-actions.md)):

- `updated` patches a row **this** store holds, and ignores one it does not. A rename or a favourite performed from the sidebar has to land here too, or the two lists disagree about a row they both show.
- **With the favourites filter on, an `updated` carrying `isFavorite: false` evicts the row** instead of patching it. The row no longer matches the query that produced it; `updateEntity` in place would leave a hollow star sitting inside a favourites-only list with the counter already disagreeing, and refetching instead would discard every page the reader had loaded.
- `deleted` removes the row through `_dropRow` (§4.4).

Optimistic patches and their rollbacks stay inside `ConversationListStore` and inside this store; an event observer never has to undo one.

## 5. The screen

Frame `4a`: a title, a toolbar, the card-surfaced `DataTable`, and a floating bulk bar over the bottom.

### 5.1 The toolbar

Five controls, left to right: the search field, the **Favourites** toggle, **Select all**, then the order statement and the counter pushed to the far end.

- **The search field's accessible name is "Search conversations by name"**, deliberately _not_ the sidebar's "Search conversations". Both fields are on screen together at this width, and a rotor listing two identically-named controls says nothing about which is which.
- **The Favourites toggle carries `aria-pressed`.** Its text never changes, so the state has to be carried somewhere a screen reader reads. It is spelt the British way, matching the sidebar kebab and the chat header star rather than the board's US "Favorites" — a reader hearing "Favorites" from this button and "Unfavourite Helios" from the row star beside it is the worse of the two inconsistencies.
- **Select-all is here, not in the table header.** `DataTable` is told `[headerSelectAll]="false"` for it: two controls bound to one state is a defect, and below 768 px there is no header row to hold one at all.
- **The counter is `aria-hidden`, and the same string is handed to `DataTable`'s `summary` input.** The table already owns a polite live region; duplicating the string here would have a screen reader read the count twice on every search, and two independently-derived counts would eventually disagree. It is withheld before the first response — "Showing 0 of 0" is not information — and beside an error panel, where it would keep describing a result set the panel exists to stop showing. A _narrowing_ search keeps it, alongside the rows it still describes.
- **The order is stated (US-705).** A muted "Newest first" carries the explanation as visually-hidden text, and an info button repeats it as a `Tooltip` flyout for the reader who is looking rather than listening (shown on focus as well as hover, dismissed by Escape — WCAG SC 1.4.13). The button has its own short `aria-label`, "Why this order", so a screen reader does not announce the whole sentence as a button name. `ORDER_EXPLANATION` in `conversations-route.ts` is the anchor US-706 replaces with a real control.
- **The toolbar wraps.** The board draws one row at 1440 px, but five controls have a combined intrinsic width well past a 360 px viewport and only the search field can shrink (WCAG SC 1.4.10 Reflow). Below 768 px the flyout and the counter are dropped, and the order statement is **clipped rather than `display: none`d** — hiding it would take the explanation out of the accessibility tree with it, leaving US-705's criterion unmet on every phone rather than merely unseen.

### 5.2 The table

Five columns: the name (a link into the chat), the project chip, the model, the star, and the row kebab. The name and the model are US-701's and the star is US-703's; the **chip and the kebab are US-307's**, and they are what completes frame `4a`.

- **The project chip is absent for a standalone conversation, not an em dash.** "No project" is a real state rather than missing data, which is what the model column's dash means — "the catalogue does not know this id". A project past `ProjectLookupStore`'s drain ceiling is absent too: naming it is impossible, and a dash would say "none", which would be wrong ([Projects §9.1](projects.md#91-the-lookup-behind-it-and-why-a-failed-drain-keeps-its-pages)).
- **The chip takes the card branch's `badges` slot, not `meta`.** It is a labelled token, which is what that slot is styled for; the model is a bare secondary string, which is what `meta` is for. A slot takes any number of columns, so this cost nothing structurally.
- **The kebab offers the same five actions the sidebar row does**, routed through the same `ConversationActionsStore` flows — nothing here reimplements a flow. "Remove from project" is absent rather than inert on a standalone row, and "Move to project" opens one picker mounted **outside** the table, fed the row's id and the kebab captured at the gesture, so a re-render of the rows underneath cannot destroy the panel the reader is using ([Conversation Actions §7](conversation-actions.md#7-move-into-or-out-of-a-project-us-307)).
- **The star renders on the server's confirmation, not optimistically.** This store owns its own entities and `ConversationActionsStore` cannot patch them; a row past the sidebar's first 50 is not in the list it _can_ patch, so an optimistic flip here would have no rollback behind it. `actions.pendingIds()` goes to `DataTable`'s `pendingIds` and to the button, and the busy state carries the feedback for the round trip. The kebab's items ride the same flag, and `openPicker` refuses a pending row outright rather than opening a panel whose every choice would be refused.
- **The star is `aria-disabled`, never natively `disabled`.** The native attribute blurs the button the instant it is pressed, dropping a keyboard reader onto `<body>` for the whole request. `toggleFavorite` already refuses re-entry on the pending id, so the guard is real either way.
- **No `aria-pressed` on the star.** Its label already carries the direction ("Unfavourite Helios"), and a control that both names its action and reports a pressed state is announced twice over. The toolbar toggle is the opposite case, and gets `aria-pressed` for it.
- **Selection exists below the breakpoint too.** `DataTable`'s card branch puts the row checkbox in `CardRow`'s `[cardRowLead]` slot, so a caller that sets `selectable` does not get a table on a laptop and an unselectable list on a phone.
- **The error panel replaces the table**, rather than sitting beside it. The rows on screen belong to a query that failed, so leaving them up would present a stale result set as if it were the answer.
- **The skeleton is for a first load only.** `isFirstLoad` is pending _with nothing on screen_; a search that narrows an existing list keeps its rows and marks the field busy instead — the same distinction the sidebar draws.
- **Row columns are fields, not template literals.** `provideCheckNoChangesConfig({ exhaustive: true })` compares bindings between passes, and an array or an arrow allocated in the template is a new value on every check.
- **A model with no catalogue entry renders an em dash.** `ConversationDto` carries only `modelId`, so the name is looked up in a `Map` built once per catalogue change rather than scanned per row, per check. `projectName` is the same shape against `ProjectLookupStore.nameOf`, differing only in what it returns for a miss.

### 5.3 Load more, and the projection trap

The control sits in `DataTable`'s `[tableFooter]` slot, and **the `@if` is inside the projected `div`, never around it**:

```html
<div tableFooter class="conversations__more">
  @if (library.hasMore()) { … }
</div>
```

Content projection matches static nodes. An `@if` _around_ the element compiles to an `ng-template` that carries no `tableFooter` attribute, so `DataTable` — which has no catch-all slot — drops it silently: no error, no control, nothing to search for. The wrapper collapsing rather than the slot emptying is also why `.table-footer` keys its gap on `:has(> :empty)`.

**The control disappears when everything is loaded** rather than rendering disabled, which is US-702's third criterion and the board's own note — a disabled control still reads as "there is more, but not for you". While a page is on the wire it stays present and carries `aria-disabled` (both halves of `loadMore()`'s guard), so it cannot look operable while a press would be a silent no-op.

### 5.4 Three empty states, because they are three situations

| State | Renders | Action |
| --- | --- | --- |
| `hasNoMatches()` — a term is why | Frame `4b`: the ridgeline motif, a heading naming the term, "Search covers conversation names only." | **Clear search** |
| `hasNoFavorites()` — only the filter is on | "No favourite conversations" / "Star a conversation and it will appear here." | **Show all conversations** |
| Neither | "No conversations yet" / "Ask something and your conversation will appear here." | **New conversation** |

`hasNoMatches` deliberately keys on the _term_ rather than on "any query": a favourites-only filter with nothing behind it is a different situation, with a different way out, and frame `4b`'s heading has no term to quote.

### 5.5 Focus, in four places

Focus is the one thing a signal cannot express declaratively, so it is handled imperatively. Two of the four are direct — the reader's own click destroys the button they are standing on, so `clearSearch()` and `clearFavorites()` move focus in the same gesture. The other two happen when a **request** settles, so both first check that focus **actually fell through** (nothing focused, `<body>`, or a disconnected node): a reader who clicked into the search field while the page was on the wire must not be yanked out of it.

| When | Where focus goes | Why it needs handling |
| --- | --- | --- |
| Clearing a search (from the field or from frame `4b`) | The search field | The Clear button removes itself the moment rows come back |
| Clearing the favourites filter from its empty state | The search field | Same shape: the button lives inside a state that is about to be replaced |
| The last page landing | The last row, falling back to the field | Load more removes itself, leaving the reader standing on a node that no longer exists. The _last_ row rather than the first new one, because that is where the vanished button was |
| A star evicting its own row under the filter | The search field | The rows shift under an eviction, so "the next one" is not a place the reader chose |

The two effects that drive the last two rows are both guarded against firing for the wrong reason: the paging effect waits on `loadingMore` settling and returns early while `hasMore()` is still true (a reader paging through must not have focus moved off the control they are pressing), and the eviction effect clears its recorded id when the row is still present and no request is pending — meaning the request failed or the guard refused it — so it cannot fire against some unrelated later removal. The search field is the fallback in both because it is the only control outside every branch of the template.

`DeleteConversationsDialog` performs the same repair on its confirm path, falling back to `#main-content` because confirming destroys the bulk bar the modal would otherwise return focus to.

### 5.6 The bulk bar and its confirmation

`BulkActionBar` supplies frame `4a`'s pill, the "N selected" copy and the Clear action; only the red **Delete selected** button is projected. It sits **outside** the error branch, because a failed _page_ leaves the rows and the selection intact and a reader who had rows checked should still be able to act on them. The bar is `position: fixed`, so nothing downstream can rescue it from overflowing a narrow viewport: it now wraps, centres, and caps at `calc(100vw - 24px)`, with the note — the longest and least urgent child — taking the second line before the count or the actions do.

`DeleteConversationsDialog` is a dumb component (`count`, `open`, `confirmed`) rather than a store consumer, unlike its single-row sibling: it has one invoker rather than two features that must not import each other, and the same "delete N selected rows" shape is what a project's conversations tab (US-908) will want. It counts rather than names — eight quoted titles is not a sentence anyone reads — states what goes (messages) and what stays (uploaded files), and lands initial focus on **Cancel**, because Enter on a freshly opened destructive confirm must not destroy anything.

## 6. Route, nav entry and bundle

```text
''  canActivate: [authGuard]
 └─ ''  canActivate: [sessionGuard]
      └─ ''  loadComponent: Shell                     ← lazy
           ├─ matcher: chatMatcher  loadComponent: Chat
           ├─ /conversations        loadComponent: Conversations   ← lazy, US-701
           └─ /admin  canMatch: [adminCanMatch]  loadChildren
```

Lazy like every other shell child: the screen and its store are code a reader who only ever opens `/chat` never runs. `CONVERSATIONS_ROUTE` lives beside the other route constants in `core/auth/auth-routes.ts`, and the sidebar's nav entry (`bi-clock-history`) appears now that there is a route to point it at — the sidebar renders **only entries whose route exists**, and a link that redirects back to `/chat` is worse than no link ([Shell and Navigation §2.3](shell-and-navigation.md#23-adding-a-nav-entry)).

EP-7's four remaining stories added nothing to the initial graph — every line of them is behind the lazy route, and the one new dependency-shaped thing (`DeleteConversationsDialog`) rides the same chunk. The initial bundle measures **663.45 kB raw / 163.11 kB transfer**, level with the US-701 baseline, with the 670 kB warning line and the 720 kB error ceiling untouched ([Answer Rendering §6](answer-rendering.md#6-the-bundle-gate-resolved)).

## 7. Testing

**79 specs across the screen's two files** — 46 in `conversations.spec.ts`, 33 in `conversation-library-store.spec.ts` — plus three added to `shared/data/data.spec.ts` for the kit's new inputs, inside the suite's 1153 as EP-7 closed. US-307 later added three more to `conversations.spec.ts`: the project chip named from the lookup store, **no chip for a project past the drain ceiling**, and the row kebab offering the five shared actions.

| Area | Spec | Notable cases |
| --- | --- | --- |
| The history rule | `conversations` (3) | Pushing when a filter goes **on**, pushing when it comes **off**, replacing while a term is refined — the function is pure, so all three are one-liners |
| Searching | `conversations` (3) | The debounce elapsing, then `name=` and **never `filter=`**; the parameter omitted entirely when the term is cleared; and **the sidebar's own list left alone**, which is §4.1 asserted rather than described |
| The URL | `conversations` (4) | The term reflected so the list is shareable; **exactly one request for a link opened cold**; the back button restoring the prior list; and no history entry stacked per keystroke |
| The empty states | `conversations` (3) | Frame `4b` naming the term; clearing from that state and **focus landing back in the field**; and a different state with a different action for a reader who has none at all |
| The rest of the screen | `conversations` (4) | Each row linking to its conversation; the model named, and the em dash when it cannot be; the skeleton on a first load and **never on a refinement**; and a failure offering a retry instead of the table |
| Order honesty (US-705) | `conversations` (3) | **No sort control anywhere**; the order _stated_ with its explanation reachable; and **no client-side reordering of a partially loaded list** across a Load more |
| Favourites (US-703) | `conversations` (7) | The filter in the URL and `isFavorite=true` on the wire; back restoring the unfiltered list; a search and the filter surviving each other; a value the screen never writes ignored; the row star routed through the shared action and marked busy; **a row dropped when its star is cleared**; and the favourites-only empty state with its own way out |
| Bulk delete (US-704) | `conversations` (9) | The pill bar's count and reach; select-all in the toolbar, not the header; the indeterminate middle state; confirm-then-one-request; cancelling leaving everything alone; **every row restored behind one toast**; **the sidebar's rows going too**; **the sidebar's rows coming back in the order they were in**; and clearing from the bar |
| Paging (US-702) | `conversations` (10) | The counter against the envelope total; **one count, handed to the table's live region rather than announced twice**; the next page appended in server order; the control removed rather than disabled; busy while a page is on the wire **and during a narrowing search**; **focus to the last row when the control removes itself**; focus left on the control while more remains; focus left where the reader put it; and the counter withheld on first load and beside an error |
| The store — query | `conversation-library-store` (5) | The first page requested and `filter=` never sent; an in-flight query **superseded rather than raced**; the query the rows belong to; an empty result told apart from an empty library; retry clearing the error |
| The store — paging | `conversation-library-store` (9) | Rows counted against the total; a page appended in server order; `take` never past the server's clamp **whatever the envelope echoes**; **a page dropped when a row is deleted out from under its offset**; a page dropped on sign-out; `hasMore` closing; a second press ignored; **a failed page keeping its rows and toasting**; and a page dropped when a newer query replaced its result set |
| The store — favourites | `conversation-library-store` (6) | `isFavorite=true` and **never `false`**; both parameters on one request; the filter carried on to the next page; **a row dropped when the server confirms it is no longer a favourite**; the same row _kept_ when the filter is off; and an empty favourites filter told apart from an empty search |
| The store — bulk delete | `conversation-library-store` (8) | Select-all's reach and its note; every id on one request with the rows removed at once; each deletion announced only on the 204; **every row put back, re-selected, behind one toast**; **a restore refused into a result set the reader replaced**; the selection pruned to the rows still loaded; the selection kept across a Load more; and a delete with nothing selected ignored |
| The store — events and sign-out | `conversation-library-store` (5) | A row patched, an unheld row ignored, a deletion taking both counters down, an unheld deletion leaving them alone; and sign-out emptying the store **and dropping a response already on the wire** |
| The shared kit | `data` (3 new) | The header select-all dropped on request **while keeping the track it sits in**; selection working below the breakpoint, where there is no header; and a paginated caller replacing the table's own count |

```bash
# from enterprise-gpt-ui/
npm test        # Vitest, single run — 1153 specs
npm run lint    # ESLint + icon, forbidden-API and token checks
npm run build   # budgets, then check-initial-chunk.mjs
```

> **Verification status.** As with every epic before it, none of this has been exercised against a live API. The gates above are green and every behaviour here is asserted against `HttpTestingController` and `RouterTestingHarness`.

## 8. Deliberately not here

Recorded in the [build order](../prd/enterprise-ui-rebuild-build-order.md)'s interim-behaviours table and marked in the source with a comment naming the story — so it does not quietly become permanent.

| Missing from frame `4a` | Owner | Notes |
| --- | --- | --- |
| ~~The **project chip** on a row, and the **per-row kebab**~~ | US-307 | Shipped (§5.2). It waited on the project surface the chip names — `ConversationDto` carries a `projectId` and no name, so there was nothing to render until a root project list existed |
| **Any sort control** | US-706 | Not an omission but the subject of a shipped story: US-705 _states_ the order instead, because no endpoint in this API accepts a sort parameter and a control that reordered one loaded page would put a second sorted run under the first at the next Load more. US-706 replaces `ORDER_EXPLANATION` and its two toolbar elements with the control |

The screen's **row selection and the project moves deliberately do not meet.** There is no "move the selected rows into a project" bulk action, because `PUT api/conversations` is per-conversation and the only bulk route the API has is the delete; offering one would be N requests behind a control that looks like one.

## 9. Troubleshooting

| Symptom | Cause |
| --- | --- |
| Every search returns the full list | The request sent `filter=` instead of `name=`. The server binds nothing and answers unfiltered — the deleted client's named regression ([Shell and Navigation §3.1](shell-and-navigation.md#31-get-apiconversationssearch)) |
| Typing here narrows the sidebar too | The screen was pointed at the root `ConversationListStore`. It needs its own (§4.1) |
| Two requests fire when a filtered link is opened cold | `bindQuery` was handed values instead of a computation, so it ran unfiltered before the router's parameters arrived (§3) |
| The field fights the user, or the term resets as they type | `[(value)]` was used on `SearchInput`, or something navigates from an effect over `name()`. The loop is one-directional by construction (§3) |
| The back button leaves the screen instead of restoring the list | `shouldReplaceHistory` is returning `true` for the on/off transitions — it must replace only while a term is being _refined_ (§3.1) |
| Back steps through the term one letter at a time | The opposite: every navigation is pushing. Same function (§3.1) |
| Frame `4b` names a term the rows do not belong to | The heading was read from the component's `name()` rather than from `library.name()`, so it shows what is being typed rather than what produced the result (§4.2) |
| **Rows deleted in bulk vanish from the table but are still in the sidebar** | The request was declared on `ConversationLibraryStore`. `rxMethod` binds its subscription to the declaring injector, and this store dies with its route — leaving here mid-flight tears the request down with nothing deleted and nothing said. It belongs in `ConversationActionsStore.deleteMany` (§4.6) |
| **The sidebar's rows come back in the wrong order after a failed bulk delete** | The restore ran front-to-back. `removeRow` captures each index against a list that is one row shorter each time, so only a **LIFO** restore is their inverse (§4.6) |
| A failed bulk delete restored nothing at all | By design when the reader searched mid-flight: `restoreRows` refuses a restore whose `_queryGeneration` no longer matches, rather than splicing the old query's rows into the new one's. The toast still reported the failure (§4.6) |
| `DELETE api/conversations/bulk` answers 400 with an empty id list | The body was passed outside the options object. `HttpClient.delete` drops it, and the endpoint binds `[FromBody]` (§4.6) |
| **A permanent one-row hole appears in the next page** | A row left the list without firing `_querySuperseded$`, so a page built against the pre-removal offset landed after `skip` moved down. Both halves belong together — that is what `_dropRow` is (§4.4) |
| After deleting a row, the next page starts one row late | The other half of the same defect: the `skip` decrement came off. Both counters have to move (§4.4) |
| Rows appear that cannot be scrolled to, and Load more has vanished | A page landed against a result set it does not belong to. Check `loadMore()` still guards on `isPending()` as well as `loadingMore()` (§4.3) |
| The whole table is replaced by an error panel after a failed **page** | `_loadNextPage`'s error arm called `setError` instead of raising a toast. The loaded rows are still valid (§4.3) |
| The skeleton flashes on every keystroke, or a Load more blanks the table | Something conflated `loadingMore` with `isPending`, or replaced `isFirstLoad` with `isPending`. A query replaces rows; a page appends to them (§4.3) |
| **Load more never renders, with no error anywhere** | The `@if` was put around the projected `div` instead of inside it. It compiles to an `ng-template` with no `tableFooter` attribute and `DataTable` drops it silently (§5.3) |
| A screen reader announces the count twice, with two different numbers | The toolbar counter lost its `aria-hidden`, or a second count was derived instead of handing `countSummary()` to `DataTable`'s `summary` (§5.1) |
| **Focus falls to `<body>` after Load more, or after a star evicts its own row** | One of the two focus effects was removed or its fall-through check was dropped. Both move focus only when it actually fell through, and the paging one waits until `hasMore()` is false (§5.5) |
| The order explanation is unavailable on a phone | The mobile rule used `display: none` on `.conversations__order`. It must **clip**: `display: none` takes the visually-hidden explanation out of the accessibility tree with it (§5.1) |
| A star flips only after the round trip | By design. The library owns its own entities, `ConversationActionsStore` cannot patch them, and an optimistic flip would have no rollback behind it; the busy state carries the feedback (§5.2) |
| A row's project cell is empty for a conversation that is in a project | `ProjectLookupStore` could not name it — past the 500-project drain ceiling, or the drain failed before reaching it. Rendering nothing is correct; an em dash would claim "no project" (§5.2) |
| The project picker vanishes while it is open | It was mounted inside the row template, so a re-render of the rows destroyed it. One picker for the table, mounted outside it, fed a captured row id (§5.2) |
| An unstarred row stays in a favourites-only list, wearing a hollow star | The `updated` handler patched in place instead of evicting. Under the filter the row no longer matches the query that produced it (§4.7) |
| The bulk bar counts rows that are not on screen | The selection was moved off `withLinkedState`, so nothing prunes it when rows leave (§4.5) |
| A checkbox looks dead — clicking changes nothing | A selection updater mutated the `Set` and wrote the same reference back. `patchState` compares by reference, so there is no signal write at all (§4.5) |
| The list is empty after signing in as a second user | `withResetOnSignOut` moved off the end of the store, or `takeUntil(signedOut$)` came off the request (§4.2) |

## 10. Key files

| Concern | File |
| --- | --- |
| The screen | [`features/conversations/conversations.ts`](../../enterprise-gpt-ui/src/app/features/conversations/conversations.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/conversations/conversations.html), [`.scss`](../../enterprise-gpt-ui/src/app/features/conversations/conversations.scss) |
| The store | [`features/conversations/conversation-library-store.ts`](../../enterprise-gpt-ui/src/app/features/conversations/conversation-library-store.ts) |
| The bulk-delete confirmation | [`features/conversations/delete-conversations-dialog.ts`](../../enterprise-gpt-ui/src/app/features/conversations/delete-conversations-dialog.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/conversations/delete-conversations-dialog.html) |
| The parameter names, the history rule, the order statement | [`features/conversations/conversations-route.ts`](../../enterprise-gpt-ui/src/app/features/conversations/conversations-route.ts) |
| The bulk delete request and the favourite toggle | [`core/conversations/conversation-actions-store.ts`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-actions-store.ts) |
| The table and the bulk bar | [`shared/data/data-table/data-table.ts`](../../enterprise-gpt-ui/src/app/shared/data/data-table/data-table.ts), [`shared/data/bulk-action-bar/bulk-action-bar.ts`](../../enterprise-gpt-ui/src/app/shared/data/bulk-action-bar/bulk-action-bar.ts) |
| Route table | [`src/app/app.routes.ts`](../../enterprise-gpt-ui/src/app/app.routes.ts) |
| Route constants | [`core/auth/auth-routes.ts`](../../enterprise-gpt-ui/src/app/core/auth/auth-routes.ts) |
| The nav entry | [`features/shell/sidebar/sidebar.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.ts) |
| The API side of the list | [`Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs), [`ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs) |
| Related reference | [Shell and Navigation](shell-and-navigation.md), [Conversation Actions](conversation-actions.md), [Frontend Foundation](frontend-foundation.md), [Design System](design-system.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md) |
