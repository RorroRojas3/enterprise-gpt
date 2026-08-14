# Conversation Library

The `/conversations` screen: a searchable table of everything a user has asked, the app's **second** conversation list store, and the URL contract that makes a filtered list shareable, the back button meaningful, and a deep link one request rather than two.

Audience: a developer building the rest of EP-7 on top of it (US-702's paging, US-703's favourites filter, US-704's bulk delete), adding a second route-scoped list anywhere in the app, or debugging a search that will not stick. Read [Shell and Navigation](shell-and-navigation.md) first for `ConversationListStore` — the reference store this one copies — and [Frontend Foundation](frontend-foundation.md) for the composable features both of them compose. Bare `§` references below are to sections of _this_ page.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference.

## 1. Overview

**The server-side `name=` search already existed.** US-302 built it for the sidebar, and this story neither changed nor extended it. What US-701 delivered is the screen the sidebar had been deliberately withholding a nav entry for, and the URL contract underneath it.

| Story      | What it delivers                                                                                                                                                                        |
| ---------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **US-701** | Frame `4a`'s conversations screen, a route-scoped `ConversationLibraryStore`, the `?name=` URL contract, and the sidebar nav entry that was absent until a route existed to point it at |

Four decisions shape everything here:

1. **The screen gets a store of its own**, not the root `ConversationListStore`. That is the story's one architectural decision, and §4.1 is the whole of it.
2. **The URL is the source of truth**, the field is a view of it, and the store caches the rows it produced. One writer, one reader path — after which the back button restoring the previous result set is a property of the router rather than code anyone wrote (§3).
3. **Refining a term replaces the history entry; adding or removing one pushes.** Both extremes fail in their own way, and this is the only rule that fails neither (§3.1).
4. **Rows carry the name and the model, and nothing else.** Every other control frame `4a` draws belongs to a later story and is absent with a comment naming it — including a sort control, which stays absent until the server can honour one (§8).

### 1.1 Where each piece lives

| Concern                                       | Where                                                                                                                                                                                                                                                                                            |
| --------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| The screen                                    | [`features/conversations/conversations.ts`](../../enterprise-gpt-ui/src/app/features/conversations/conversations.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/conversations/conversations.html), [`.scss`](../../enterprise-gpt-ui/src/app/features/conversations/conversations.scss) |
| The route-scoped store                        | [`features/conversations/conversation-library-store.ts`](../../enterprise-gpt-ui/src/app/features/conversations/conversation-library-store.ts)                                                                                                                                                   |
| The query-parameter name and the history rule | [`features/conversations/conversations-route.ts`](../../enterprise-gpt-ui/src/app/features/conversations/conversations-route.ts)                                                                                                                                                                 |
| The lazy route and its title                  | [`src/app/app.routes.ts`](../../enterprise-gpt-ui/src/app/app.routes.ts)                                                                                                                                                                                                                         |
| `CONVERSATIONS_ROUTE`                         | [`core/auth/auth-routes.ts`](../../enterprise-gpt-ui/src/app/core/auth/auth-routes.ts)                                                                                                                                                                                                           |
| The nav entry                                 | [`features/shell/sidebar/sidebar.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.ts)                                                                                                                                                                                         |
| The list this one is _not_                    | [`core/conversations/conversation-list-store.ts`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-list-store.ts) — the reference store ([Shell and Navigation §4](shell-and-navigation.md))                                                                                      |
| The events that keep the two in step          | [`core/events/conversation-events.ts`](../../enterprise-gpt-ui/src/app/core/events/conversation-events.ts)                                                                                                                                                                                       |
| The table, the search field, the empty state  | [`shared/data/data-table/`](../../enterprise-gpt-ui/src/app/shared/data/data-table/), [`shared/form/search-input/`](../../enterprise-gpt-ui/src/app/shared/form/search-input/), [`shared/feedback/empty-state/`](../../enterprise-gpt-ui/src/app/shared/feedback/empty-state/)                   |

## 2. Quick start

### 2.1 Reading the library

The store is provided by the `Conversations` component, so it exists only while the route does:

```ts
protected readonly library = inject(ConversationLibraryStore);

//   library.entities()      → readonly ConversationDto[], in the server's order
//   library.name()          → the term the rows on screen belong to
//   library.isPending()     → a query is in flight
//   library.error()         → AppError | null
//   library.isEmpty()       → loaded, and there is genuinely nothing
//   library.hasNoMatches()  → loaded, nothing matched, and a search is why
//   library.isFirstLoad()   → pending with nothing on screen — a skeleton is honest
//
//   library.bindQuery(nameSignal);  library.retry();
```

There is no `search(term)` method, and that absence is the design: **the only way to change the query is to navigate** (§3).

### 2.2 Adding a filter that lives in the URL — what US-703 does next

Three seams already exist for it, so the story is additive:

1. **`queryParamsHandling: 'merge'`** is already on the navigation, so a favourites parameter survives a search and a search survives it.
2. **`shouldReplaceHistory` takes a before and an after** rather than reading the screen, so a discrete on/off toggle reuses it by passing its own two values.
3. **The store's `searchUrl` builder is the one place a parameter is added**, and `isFavorite` is already documented in the API table ([Shell and Navigation §3.1](shell-and-navigation.md#31-get-apiconversationssearch)).

Bind the new input the same way — one `input()` fed by `withComponentInputBinding()`, handed to the store as a **signal** — and never navigate from an effect over it (§3).

## 3. The URL is the source of truth

The loop is deliberately one-directional, and each hop is somewhere different:

```text
user types  →  SearchInput debounce (300 ms)  →  (searched)  →  router.navigate({ name })
                                                                        ↓
      rows ← store ← bindQuery(name signal) ← input('name') ← withComponentInputBinding()
```

`Conversations.name` is an `input()` bound from `?name=` by `withComponentInputBinding()`, trimmed in its transform — the router writes `undefined` when the key leaves the URL, and a hand-edited `?name=%20%20` has to mean unfiltered. `onSearched` is the **only** writer, and it writes to the router, never to the store.

Three consequences fall out of that rather than being implemented:

- **A filtered list is shareable**, because the filter is in the address bar.
- **The back button restores the previous result set**, because the router restores the parameter and the parameter is what the store runs.
- **A cleared search removes the key** rather than leaving `?name=` behind — `queryParams: { name: null }`.

**`bindQuery` takes the input signal, not its value**, and that is what makes a deep link _one_ request. `rxMethod` subscribes through an effect, so it reads the term the router has already bound; handing over `name()` would have fired an unfiltered request first and a filtered one after it.

**The loop cannot feed itself.** A navigation changes `name`, which re-seeds `SearchInput`'s `linkedSignal`; its debounce then compares the new text against the value it already holds, finds them equal, and emits nothing. Two rules keep that true, and both are stated in the component:

- **Bind `[value]` one-way, never `[(value)]`.**
- **Never navigate from an effect over `name()`** — only from the user's own gesture.

### 3.1 Refining replaces; adding or removing pushes

```ts
export function shouldReplaceHistory(previous: string, next: string): boolean {
  return previous !== "" && next !== "";
}
```

Both extremes fail the story's third criterion in their own way. **Always replacing** means the back button leaves the screen instead of restoring the unfiltered list. **Always pushing** walks the reader back through their own typing one debounce at a time — `quarterl`, `quarter`, `quart`, and so on.

Pushing at exactly the two transitions a reader thinks of as a state change — filter on, filter off — gives `/conversations` → `?name=quarterly` → back → `/conversations`, and makes _clearing_ a search undoable too.

## 4. The second store

[`ConversationLibraryStore`](../../enterprise-gpt-ui/src/app/features/conversations/conversation-library-store.ts) is provided on the component, so it is created by the route that shows it and destroyed with it.

### 4.1 Why it is not the root `ConversationListStore`

They query the same endpoint. They are not the same list, and four separate things say so:

|                                                     | Why sharing fails                                                                                                                                                                                                                                      |
| --------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **Frame `4a` draws both search fields at once**     | The sidebar is on screen beside this table, so shared state would retype the sidebar's field and narrow its rows as the user typed here                                                                                                                |
| **`withOffsetPagination` holds exactly one offset** | It cannot serve a 50-row sidebar and a 25-row table at the same time. `LIBRARY_PAGE_SIZE` is 25 to frame `4a`'s own count and `SIDEBAR_PAGE_SIZE` is 50 because a sidebar should rarely need a second page — two numbers chosen for two different jobs |
| **US-703's `isFavorite` would narrow the sidebar**  | With **no control there to undo it**, which is a dead end the user cannot get out of                                                                                                                                                                   |
| **The URL is authoritative for _this route_**       | A root store outlives the route, so a deep link would leave the sidebar filtered after navigating on to `/chat`                                                                                                                                        |

### 4.2 What it copies, and the one thing it drops

It copies the reference store's shape _and its reasons_, which is the point of having a reference store at all:

- **`rxMethod` + `switchMap`**, because typing drives it and a slow `"ang"` must not paint over a fast `"angular"`.
- **No `debounceTime`**, because `SearchInput` already debounces 300 ms and emits only committed values; a second one would add latency for nothing.
- **No `distinctUntilChanged`**, because the signal source only emits on a real change and `retry()` pushes the same term again on purpose.
- **`takeUntil(signedOut$)` on the inner request**, because `withResetOnSignOut` clears state and does **not** cancel work ([Frontend Foundation §5.5](frontend-foundation.md#55-withresetonsignout--compose-it-last)).
- **`withResetOnSignOut` composed last**, as that feature requires.
- **`name` is written when the request _starts_, not when it commits**, so frame `4b`'s heading names the term that produced the empty result rather than the one still being typed. The component reads `library.name()` for that heading, never its own input.

What it does **not** copy is the `_started` release sentinel. The sidebar's list exists before any screen asks for it, so `SessionBootstrap` has to release it; this one is created by the route that shows it and always has a term to run — even if that term is the empty string.

### 4.3 Staying in step with the sidebar

Through the existing `conversationEvents`, **server-confirmed facts only**, which is the whole contract of that event group ([Shell and Navigation](shell-and-navigation.md), [Conversation Actions](conversation-actions.md)):

- `updated` patches a row **this** store holds, and ignores one it does not. A rename or a favourite performed from the sidebar has to land here too, or the two lists disagree about a row they both show.
- `deleted` removes the row and takes **both counters** down with it — `totalCount`, because US-702's "Showing N of M" would otherwise claim a row that is gone, and `skip`, because the server no longer counts the row either. Leaving `skip` where it was would make the next page start one row late, and the row that slid into the gap would never be fetched.

Optimistic patches and their rollbacks stay inside `ConversationListStore`, which is why this store needs no knowledge of them.

## 5. The screen

Frame `4a`: a title, a toolbar, then the card-surfaced `DataTable`.

- **The search field's accessible name is "Search conversations by name"**, deliberately _not_ the sidebar's "Search conversations". Both fields are on screen together at this width, and a rotor listing two identically-named controls says nothing about which is which.
- **The error panel replaces the table**, rather than sitting beside it. The rows on screen belong to a query that failed, so leaving them up would present a stale result set as if it were the answer.
- **The skeleton is for a first load only.** `isFirstLoad` is pending _with nothing on screen_; a search that narrows an existing list keeps its rows and marks the field busy instead — the same distinction the sidebar draws.
- **Two empty states, because they are two situations.** `hasNoMatches` renders frame `4b` — the ridgeline motif, a heading naming the term, the line "Search covers conversation names only.", and a **Clear search** action. A reader with no conversations at all gets "No conversations yet" and a **New conversation** link instead.
- **Clearing the search hands focus back to the field.** The Clear button removes itself the moment rows come back, and focus would otherwise fall to `<body>` inside a list the reader then has to find again. The field lives outside every branch of the template, so it is always connected.
- **Row columns are fields, not template literals.** `provideCheckNoChangesConfig({ exhaustive: true })` compares bindings between passes, and an array or an arrow allocated in the template is a new value on every check.
- **A model with no catalogue entry renders an em dash.** `ConversationDto` carries only `modelId`, so the name is looked up in a `Map` built once per catalogue change rather than scanned per row, per check.

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

The initial bundle finishes at **663.46 kB raw / 163.36 kB transfer**, with the warning line re-stated 665 → **670 kB** and the 720 kB error ceiling untouched ([Answer Rendering §6](answer-rendering.md#6-the-bundle-gate-resolved)).

## 7. Testing

28 specs across two files, inside the suite’s 1099.

| Area                   | Spec                              | Notable cases                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| ---------------------- | --------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The history rule       | `conversations` (3 of 18)         | Pushing when a filter goes **on**, pushing when it comes **off**, replacing while a term is refined — the function is pure, so all three are one-liners                                                                                                                                                                                                                                                                                        |
| Searching              | `conversations` (3 of 18)         | The debounce elapsing, then `name=` and **never `filter=`**; the parameter omitted entirely when the term is cleared; and **the sidebar's own list left alone**, which is §4.1 asserted rather than described                                                                                                                                                                                                                                  |
| The URL                | `conversations` (4 of 18)         | The term reflected so the list is shareable; **exactly one request for a link opened cold**; the back button restoring the prior list; and no history entry stacked per keystroke                                                                                                                                                                                                                                                              |
| The empty states       | `conversations` (3 of 18)         | Frame `4b` naming the term; clearing from that state and **focus landing back in the field**; and a different state with a different action for a reader who has none at all                                                                                                                                                                                                                                                                   |
| The rest of the screen | `conversations` (5 of 18)         | Each row linking to its conversation; the model named, and the em dash when it cannot be; the skeleton on a first load and **never on a refinement**; a failure offering a retry instead of the table; and **no sort control anywhere**, which is US-705's criterion asserted early                                                                                                                                                            |
| The store              | `conversation-library-store` (10) | The first page requested and `filter=` never sent; an in-flight query **superseded rather than raced**; the term the rows belong to; an empty result told apart from an empty library; retry clearing the error; the four event cases — a row patched, an unheld row ignored, a deletion taking both counters down, and an unheld deletion leaving them alone; and sign-out emptying the store **and dropping a response already on the wire** |

```bash
# from enterprise-gpt-ui/
npm test        # Vitest, single run — 1099 specs across 90 files
npm run lint    # ESLint + icon, forbidden-API and token checks
npm run build   # budgets, then check-initial-chunk.mjs
```

> **Verification status.** As with every epic before it, none of this has been exercised against a live API. The gates above are green and every behaviour here is asserted against `HttpTestingController` and `RouterTestingHarness`.

## 8. Deliberately not here

Each of these is recorded in the [build order](../prd/enterprise-ui-rebuild-build-order.md)'s interim-behaviours table and marked in the template with a comment naming its story — so none of them quietly becomes permanent.

| Missing from frame `4a`                                      | Owner                                                    | Notes                                                                                                                                                                                                            |
| ------------------------------------------------------------ | -------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The "Showing N of M conversations" counter and **Load more** | US-702                                                   | The store already reads the envelope's `totalCount` through `withOffsetPagination`; only the first page is fetched                                                                                               |
| The **Favorites** filter                                     | US-703                                                   | `isFavorite` is already an API parameter, and US-305 sets the flag it would filter on. §2.2 is the seam                                                                                                          |
| Row checkboxes, select-all, and the bulk delete bar          | US-704                                                   | `DataTable` already has a `selectable` input; nothing on this screen sets it                                                                                                                                     |
| A per-row star and kebab                                     | US-703, and the story that gives the library row actions | The kebab's flows exist already in `ConversationActionsStore` ([Conversation Actions](conversation-actions.md))                                                                                                  |
| The project chip on a row                                    | US-307                                                   | Which is also the last outstanding row action in EP-3                                                                                                                                                            |
| **Any sort control**                                         | US-705, then US-706                                      | Not an omission but the subject of its own story: no endpoint in this API accepts a sort parameter, so the list is server order (regime **B**) and offering a control that reordered one page would be dishonest |

## 9. Troubleshooting

| Symptom                                                         | Cause                                                                                                                                                                                                                       |
| --------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Every search returns the full list                              | The request sent `filter=` instead of `name=`. The server binds nothing and answers unfiltered — the deleted client's named regression ([Shell and Navigation §3.1](shell-and-navigation.md#31-get-apiconversationssearch)) |
| Typing here narrows the sidebar too                             | The screen was pointed at the root `ConversationListStore`. It needs its own (§4.1)                                                                                                                                         |
| Two requests fire when a filtered link is opened cold           | `bindQuery` was handed `name()` instead of the `name` signal, so it ran unfiltered before the router's value arrived (§3)                                                                                                   |
| The field fights the user, or the term resets as they type      | `[(value)]` was used on `SearchInput`, or something navigates from an effect over `name()`. The loop is one-directional by construction (§3)                                                                                |
| The back button leaves the screen instead of restoring the list | `shouldReplaceHistory` is returning `true` for the on/off transitions — it must replace only while a term is being _refined_ (§3.1)                                                                                         |
| Back steps through the term one letter at a time                | The opposite: every navigation is pushing. Same function (§3.1)                                                                                                                                                             |
| Frame `4b` names a term the rows do not belong to               | The heading was read from the component's `name()` rather than from `library.name()`, so it shows what is being typed rather than what produced the result (§4.2)                                                           |
| Focus falls to the page body after clearing a search            | `clearSearch()` no longer calls `focusField()`. The Clear button removes itself as soon as rows return (§5)                                                                                                                 |
| The skeleton flashes on every keystroke                         | Something replaced `isFirstLoad` with `isPending`. A refinement keeps its rows and marks the field busy (§5)                                                                                                                |
| A row renamed from the sidebar does not update here             | The `conversationEvents` handlers were removed, or the event is being raised optimistically rather than on the server's confirmation (§4.3)                                                                                 |
| After deleting a row, the next page starts one row late         | The `skip` decrement came off the `deleted` handler. Both counters have to move (§4.3)                                                                                                                                      |
| The list is empty after signing in as a second user             | `withResetOnSignOut` moved off the end of the store, or `takeUntil(signedOut$)` came off the request (§4.2)                                                                                                                 |

## 10. Key files

| Concern                                 | File                                                                                                                                                                                                                                             |
| --------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| The screen                              | [`features/conversations/conversations.ts`](../../enterprise-gpt-ui/src/app/features/conversations/conversations.ts)                                                                                                                             |
| The store                               | [`features/conversations/conversation-library-store.ts`](../../enterprise-gpt-ui/src/app/features/conversations/conversation-library-store.ts)                                                                                                   |
| The parameter name and the history rule | [`features/conversations/conversations-route.ts`](../../enterprise-gpt-ui/src/app/features/conversations/conversations-route.ts)                                                                                                                 |
| Route table                             | [`src/app/app.routes.ts`](../../enterprise-gpt-ui/src/app/app.routes.ts)                                                                                                                                                                         |
| Route constants                         | [`core/auth/auth-routes.ts`](../../enterprise-gpt-ui/src/app/core/auth/auth-routes.ts)                                                                                                                                                           |
| The nav entry                           | [`features/shell/sidebar/sidebar.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.ts)                                                                                                                                         |
| The API side of the list                | [`Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs), [`ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs)    |
| Related reference                       | [Shell and Navigation](shell-and-navigation.md), [Conversation Actions](conversation-actions.md), [Frontend Foundation](frontend-foundation.md), [Design System](design-system.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md) |
