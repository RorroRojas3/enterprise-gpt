# Administration

The `/admin` area: a layout route carrying the rail its tabs hang off, and the **two** tabs behind it. A server-paged **user directory** with a create modal, a permission-editing drawer and a deactivate confirmation; and an unpaginated **model catalog** with a create-and-edit dialog, a set-as-default row action and a retire confirmation. Each tab splits its state across **two** stores, and that seam is the most consequential thing here. The area replaces the placeholder that had stood since US-203, whose only job was to give `adminCanMatch` a real chunk to withhold.

Audience: a developer adding the third admin tab (US-1208's MCP servers), building a numbered pager or a client-filtered table anywhere else in the app, or debugging a rejection that landed on the wrong surface. Read [Conversation Library](conversation-library.md) first for the list-screen conventions the directory copies — the URL contract, the route-scoped store, the empty states — [Authentication and Session §9](authentication-and-session.md#9-administration-withheld-rather-than-hidden-us-203) for why this chunk is withheld rather than hidden, and [Frontend Foundation](frontend-foundation.md) for the composable store features and the `core/errors` conventions everything here composes. Bare `§` references below are to sections of _this_ page.

The server side is the authority on everything past the request, and this page links into it rather than restating it:

- [User Management](../users/user-management.md) — §2 for the directory routes and their failure modes, §6 for what `PUT` does to a permission set, §7 for the two invariants §9 below renders, and §8 for what deactivation does **not** do.
- [Model Management](../models/model-management.md) — §2 for the catalog routes and their wire shapes, §5 for the validation the form mirrors, §6 for the single-default invariant §10 below refetches to show, and §7 for what a soft-deleted model keeps.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference.

## 1. Overview

Five stories. The first four were taken in their numbered order because they all write the same three files; US-1207 followed, on the second tab, touching none of them. Between them they also land the part of US-1209 that frame `5a` draws as part of the users screen.

| Story               | What it delivers                                                                                                                                                                                     |
| ------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **US-1201**         | The `AdminLayout` route and its 190px rail, and the directory: one search field over `?name=`, `?page=` and `?size=`, a numbered pager with a 25/50/100 select, avatar initials, one badge per grant |
| **US-1202**         | The add-user modal — a work email and a permission checklist, and nothing else, because `CreateUserActionDto` has nothing else                                                                       |
| **US-1203**         | A 420px right offcanvas editing permissions, stating before Save that the whole set is being replaced, with MCP-derived rows lock-marked and still operable                                          |
| **US-1204**         | A deactivate confirmation, an optimistic and position-faithful row removal, and the refusal a caller gets on their own row                                                                           |
| **US-1207**         | The models tab: the whole catalog in one request, filtered in memory, an eleven-field create-and-edit dialog, a Set as default row action, and a retire confirmation (§10)                           |
| **part of US-1209** | The layout route, the rail marking the open tab from the router, and the `/admin` → `/admin/users` redirect. What is left to US-1209 is the other two tabs and the criteria spanning all four        |

Nine decisions shape the directory, and each looks removable until you know what it prevents:

1. **There is no Last active column, and the edit panel's identity line is one clause short.** `UserDto` carries no timestamp of any kind, and the entity's `DateModified` moves only when an administrator edits the row — never when the user signs in. Shipped absent rather than filled with a plausible-looking value (§4.5).
2. **`AdminUsersStore` does not compose `withOffsetPagination`.** That feature models a running "Load more" offset; a numbered pager needs `skip = (page − 1) × pageSize` and a page that _replaces_ its rows. The page window lives inline (§4.2).
3. **Two stores, one root and one route-scoped.** `UserActionsStore` owns every request because they outlive the screen; `AdminUsersStore` owns the rows because they belong to the route. They talk through `adminEvents`, and the rollback for an optimistic removal travels back as a callback (§5).
4. **A failed request names its own flow.** `rejectForm` takes the flow identity rather than reading `formMode()`, because a surface can be force-closed mid-flight and the other one opened before the response lands (§6.5).
5. **Four rejections reach the create form's email field and only three are validation problems.** The fourth is the directory's 404, which carries no `errors` dictionary at all (§6.2).
6. **The checklist reads `GET api/permissions`, not `/all`.** `/all` also returns deactivated permissions, and the server answers a grant of one with a 404 — a checkbox that could only ever fail (§6.3).
7. **MCP-derived permissions are lock-marked but operable.** The API permits granting them, and disabling them would remove the only route to MCP tool access (§7.4).
8. **Both self-action refusals render inline, in the board's second-person wording, and never as a toast** — recognised by the `errors` key plus the target's identity, never by the server's prose (§9).
9. **Three controls frame `5a` draws stay absent**, each pinned by a spec: no sort control, no row-selection column, no permission filter (§13).

Ten more shape the model catalog, and none of them is a preference:

1. **The form binds eleven fields where frame `5f` draws eight.** `ModelMapper` assigns `isReasoningEnabled` and both `…PricePerMillionTokens` unconditionally on a PUT, so an edit that omitted a price would _clear_ it — and this is the only surface in the app that can set one. Blank submits `null` (unpriced); zero submits `0` (genuinely free) (§10.4).
2. **`adminEvents.modelCatalogChanged` is `type<void>()` and both observers refetch**, unlike the three user events, which carry a DTO and patch in place. A save with `isDefault` demotes a row the response does not describe, and `DELETE` clears `IsDefault` on the row it retires — no payload could carry either (§10.6).
3. **Retired models render muted, with a Status column frame `5e` does not draw**, and their kebab offers no items at all. `GET api/models/all` returns them, and a table that hid them would make Delete look like it did nothing (§10.3).
4. **Regime C, so there is no URL contract** — no `models-route.ts`, no `bindQuery`. This is `withClientQuery`'s first production consumer (§10.2).
5. **"Must be a whole number of tokens." is the client's own rule.** The columns are `decimal(18,2)` and the validator carries only `GreaterThan(0)`; the server would accept `400000.5` (§10.4).
6. **The four numeric fields bind as `type="text"` with `inputmode`**, never `type="number"`, which returns `null` for what it cannot parse — discarding the rejected value and collapsing "blank" into "unparseable" (§10.4).
7. **`formBusy` is set only by the dialog's own submit.** It drives the fields, both buttons _and_ `Modal`'s Escape and backdrop suppression, so a row action setting it would trap the reader in a dialog they opened (§10.5).
8. **No failure closes the dialog.** Eleven fields, and `retryInterceptor` retries neither a POST nor a PUT (§10.5).
9. **`provider.ts` gained Azure AI Foundry.** `Providers.cs` seeds four and the client mirror had three; there is no `GET api/providers`, so the mirror is the only source (§10.7).
10. **The `DEFAULT` pill renders in `--brand`, not frame `5e`'s `--accent`**, which measures 2.74:1 on light `--surface` — below AA, with the border in the same colour, so there is no second channel (§10.3).

### 1.1 Where each piece lives

| Concern                                                                     | Where                                                                                                                                                                                                                                                                                                                            |
| --------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The wire shapes — `UserDto`, `PermissionDto`                                | [`domain/api/user.ts`](../../enterprise-gpt-ui/src/app/domain/api/user.ts) — framework-free                                                                                                                                                                                                                                      |
| The wire shapes — `ModelDto`, `ModelWriteBody`, `isRetired`                 | [`domain/api/model.ts`](../../enterprise-gpt-ui/src/app/domain/api/model.ts) — one body shape for both verbs (§10.4)                                                                                                                                                                                                             |
| The four provider ids, names and tones                                      | [`domain/api/provider.ts`](../../enterprise-gpt-ui/src/app/domain/api/provider.ts) — a mirror of the seeded rows, because there is no `GET api/providers` (§10.7)                                                                                                                                                                |
| The Administrator permission id                                             | [`domain/api/permission-ids.ts`](../../enterprise-gpt-ui/src/app/domain/api/permission-ids.ts) — matched by id, never by name                                                                                                                                                                                                    |
| Every request, and the state of every surface                               | [`core/users/user-actions-store.ts`](../../enterprise-gpt-ui/src/app/core/users/user-actions-store.ts) — root-scoped (§5.1)                                                                                                                                                                                                      |
| The two second-person refusal messages, and how they are recognised         | [`core/users/self-action-messages.ts`](../../enterprise-gpt-ui/src/app/core/users/self-action-messages.ts) (§9)                                                                                                                                                                                                                  |
| The four events, three for the directory and one for the catalog            | [`core/events/admin-events.ts`](../../enterprise-gpt-ui/src/app/core/events/admin-events.ts) (§5.2)                                                                                                                                                                                                                              |
| Every catalog request, and the state of both model surfaces                 | [`core/catalog/model-actions-store.ts`](../../enterprise-gpt-ui/src/app/core/catalog/model-actions-store.ts) — root-scoped (§10.5)                                                                                                                                                                                               |
| The form's value shape, its client-side rules, and the body builder         | [`core/catalog/model-form.ts`](../../enterprise-gpt-ui/src/app/core/catalog/model-form.ts) (§10.4)                                                                                                                                                                                                                               |
| Initials and the deterministic tone                                         | [`shared/avatar/initials.ts`](../../enterprise-gpt-ui/src/app/shared/avatar/initials.ts) — shared with the sidebar footer                                                                                                                                                                                                        |
| The initials circle, and the four-tone ramp that is _not_ in `_tokens.scss` | [`shared/avatar/avatar-initials/`](../../enterprise-gpt-ui/src/app/shared/avatar/avatar-initials/) (§4.4)                                                                                                                                                                                                                        |
| The area's route table and its chrome                                       | [`features/admin/admin.routes.ts`](../../enterprise-gpt-ui/src/app/features/admin/admin.routes.ts), [`admin-layout.ts`](../../enterprise-gpt-ui/src/app/features/admin/admin-layout.ts)                                                                                                                                          |
| The tabs that exist, in the board's order                                   | [`features/admin/admin-tabs.ts`](../../enterprise-gpt-ui/src/app/features/admin/admin-tabs.ts) (§3.2)                                                                                                                                                                                                                            |
| The directory screen                                                        | [`features/admin/users/admin-users.ts`](../../enterprise-gpt-ui/src/app/features/admin/users/admin-users.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/admin/users/admin-users.html)                                                                                                                                   |
| Its route-scoped store                                                      | [`features/admin/users/admin-users-store.ts`](../../enterprise-gpt-ui/src/app/features/admin/users/admin-users-store.ts) (§4.2)                                                                                                                                                                                                  |
| The three parameter names, the size whitelist, the order statement          | [`features/admin/users/admin-users-route.ts`](../../enterprise-gpt-ui/src/app/features/admin/users/admin-users-route.ts) (§4.1)                                                                                                                                                                                                  |
| The three surfaces                                                          | [`user-form-dialog.ts`](../../enterprise-gpt-ui/src/app/features/admin/users/user-form-dialog.ts) (§6), [`user-edit-panel.ts`](../../enterprise-gpt-ui/src/app/features/admin/users/user-edit-panel.ts) (§7), [`deactivate-user-dialog.ts`](../../enterprise-gpt-ui/src/app/features/admin/users/deactivate-user-dialog.ts) (§8) |
| The checkboxes both forms share                                             | [`features/admin/users/permission-checklist.ts`](../../enterprise-gpt-ui/src/app/features/admin/users/permission-checklist.ts) (§7.4)                                                                                                                                                                                            |
| The models screen and its route-scoped store                                | [`features/admin/models/admin-models.ts`](../../enterprise-gpt-ui/src/app/features/admin/models/admin-models.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/admin/models/admin-models.html), [`admin-models-store.ts`](../../enterprise-gpt-ui/src/app/features/admin/models/admin-models-store.ts) (§10.2)             |
| The two model surfaces                                                      | [`model-form-dialog.ts`](../../enterprise-gpt-ui/src/app/features/admin/models/model-form-dialog.ts) (§10.4), [`deactivate-model-dialog.ts`](../../enterprise-gpt-ui/src/app/features/admin/models/deactivate-model-dialog.ts) (§10.6)                                                                                           |
| `400k` and `32,768`, in one fixed locale                                    | [`features/admin/models/model-format.ts`](../../enterprise-gpt-ui/src/app/features/admin/models/model-format.ts) (§10.3)                                                                                                                                                                                                         |
| The pager, the drawer, the badge                                            | [`shared/data/paginator/`](../../enterprise-gpt-ui/src/app/shared/data/paginator/), [`shared/overlay/offcanvas/`](../../enterprise-gpt-ui/src/app/shared/overlay/offcanvas/), [`shared/badge/permission-badge/`](../../enterprise-gpt-ui/src/app/shared/badge/permission-badge/) — all US-106's, built for these frames (§11.2)  |
| Fixtures for specs                                                          | [`src/testing/users.ts`](../../enterprise-gpt-ui/src/testing/users.ts), [`src/testing/catalog.ts`](../../enterprise-gpt-ui/src/testing/catalog.ts)                                                                                                                                                                               |

## 2. Quick start

### 2.1 Reading the directory

`AdminUsersStore` is provided by the `AdminUsers` component, so it exists only while the tab does:

```ts
protected readonly users = inject(AdminUsersStore);     // the rows
protected readonly actions = inject(UserActionsStore);  // every request

//   users.entities()      → readonly UserDto[], in the server's order (last name, first name)
//   users.name()          → the term the rows on screen belong to
//   users.page() / .pageSize() / .totalCount() / .totalPages()
//   users.isPending()     → a query is in flight
//   users.isFirstLoad()   → pending with nothing on screen — a skeleton is honest
//   users.isEmpty()       → loaded, and there is genuinely nothing
//   users.hasNoMatches()  → loaded, nothing matched, and a term is why
//   users.isPastEnd()     → loaded, nothing here, and there are rows on an earlier page
//   users.countSummary()  → "Showing 26–50 of 312 users"
//   users.error()         → AppError | null
//
//   users.bindQuery(computation);  users.retry();  users.refresh();
//   users.takeRow(id);  users.restoreRow(removed);
```

There is no `search(term)`, no `setPage(n)` and no `setPageSize(n)`: **the only way to change the query is to navigate** (§4.1).

### 2.2 The three URL parameters

| Parameter | Written as                                          | History                                                               | On the wire                               |
| --------- | --------------------------------------------------- | --------------------------------------------------------------------- | ----------------------------------------- |
| `?name=`  | The trimmed term, or the key **removed** when empty | Replaces while a term is refined; pushes when one is added or removed | `name=` when non-empty, omitted otherwise |
| `?page=`  | The 1-based page, or the key removed for page 1     | Always pushes — a page change is a discrete transition                | `skip=(page − 1) × pageSize`              |
| `?size=`  | One of 25 / 50 / 100, or the key removed for 25     | Always pushes                                                         | `take=`                                   |

Three things about that table are decisions rather than incidentals.

**All three go out on one request.** `bindQuery` takes a single `AdminUsersQuery { name, page, pageSize }` computation, so a deep link carrying all three issues one query rather than a default request followed by the real one. Binding them as three `rxMethod`s would fire a second query for every change to any of them.

**`size=`, not the API's own `take=`.** The URL is a contract with the reader rather than a proxy for the request underneath it — the same reason the conversations library spells its favourites filter `favorites=1` and not `isFavorite=true` ([Conversation Library §2.2](conversation-library.md#22-the-two-url-parameters)).

**Both numeric inputs are defensively transformed, and `?size=` is whitelisted rather than clamped.** `toPage` reads anything that is not a whole number at or above 1 as page 1 — a hand-edited `?page=0` would otherwise send `skip=-25`, and `?page=abc` a `skip=NaN`. `toPageSize` accepts only the three sizes the select offers, so the control can never display a value the URL contradicts.

A search resets the page (`[PAGE_PARAM]: null`), and so does a size change: a narrowed search rarely has as many pages as the one before it, and row 26 is page 2 at 25 per page and page 1 at 50 — an offset that means something different than it did a moment ago.

### 2.3 Reading the model catalog

`AdminModelsStore` is provided by the `AdminModels` component, so it exists only while the tab does. `ModelActionsStore` is root-scoped and outlives it:

```ts
protected readonly models = inject(AdminModelsStore);   // the rows, and the filter over them
protected readonly actions = inject(ModelActionsStore); // every request, and both surfaces

//   models.entities()     → readonly ModelDto[], the whole catalog, retired rows included
//   models.results()      → what the search field left of it — render these
//   models.query()        → the committed term
//   models.isFirstLoad()  → pending with nothing on screen
//   models.isEmpty()      → loaded, and the catalog is genuinely empty
//   models.hasNoMatches() → loaded, nothing matched, and a term is why
//   models.countSummary() → "Showing 3 of 11 models", or "11 models in the catalog"
//   models.canSort()      → withClientQuery's honesty flag (§10.2)
//
//   models.load();  models.search(term);  models.retry();  models.refresh();
```

There is no `bindQuery` and no route file: **the search term is screen state, not a URL parameter** (§10.2). The mutations all live on the other store — `beginCreate()`, `beginEdit(model)`, `submitForm(value)`, `setAsDefault(model)`, `beginDeactivate(model)`, `confirmDeactivate()` — and none of them patches these rows: they dispatch `adminEvents.modelCatalogChanged`, and the list refetches (§10.6).

### 2.4 Adding an admin tab

Three files, and none of them is the layout. The models tab is the worked example; US-1208's is the same two edits:

```ts
// admin-tabs.ts — the rail entry. Add it in the story that builds the route, never before.
{ id: 'mcps', label: 'MCP servers', icon: 'bi-hdd-network', link: `${ADMIN_ROUTE}/mcps` }

// admin.routes.ts — a sibling child of the layout, providing its own store on its own route.
{ path: 'mcps', component: AdminMcps, title: 'MCP servers — Enterprise GPT' }
```

`AdminLayout` holds **no store**, and no tab may provide one on it: US-1209 requires that opening a tab instantiates that tab's store and no other. The third file is the screen itself, and which of the two on this page it copies is decided by the endpoint: a server-paged route takes `AdminUsers`'s URL contract (§4.1), an unpaginated one takes `AdminModels`'s client filter (§10.2). `GET api/mcps/all` is unpaginated, so US-1208 copies the models tab.

### 2.5 Rules that are not style preferences

- **Never add a rail entry before its route exists.** A link that leads nowhere is the shown-and-disabled affordance US-203 already rejected for the Admin entry itself (§3.2).
- **Never build a user update body without the profile fields.** `UpdateUserActionDto` requires `firstName`, `lastName` and `email` to be non-empty; `{ permissionIds }` alone is a 400 on three required fields, not a partial update (§7.3).
- **Never decide which surface renders a failure by reading `formMode()`.** Pass the flow (§6.5).
- **Never match a self-action refusal on the server's message.** Match the `errors` key and the target's identity (§9).
- **Never disable an MCP-derived permission's checkbox.** The API permits the grant, and disabling it removes the only route to MCP tool access (§7.4).
- **Never let `UserActionsStore` or `ModelActionsStore` be reached from anything eagerly imported.** `providedIn: 'root'` is a DI scope, not a chunk assignment, and the initial graph has well under a kilobyte of headroom (§11.1).
- **Never reinterpret `withOffsetPagination`'s `skip` as a request offset.** Four stores read it as the feature defines it (§4.2).
- **Never assemble a model write body by hand.** Both verbs go through `toModelBody`, because `ModelMapper` assigns every property on a PUT and an omitted price is a cleared one (§10.4).
- **Never set `formBusy` from anything but the model dialog's own submit.** It is also `Modal`'s Escape and backdrop suppression (§10.5).
- **Never filter retired models out of the catalog table.** They are what `DELETE` produces, and hiding them makes the action look inert (§10.3).

## 3. The area: a layout route and a rail

### 3.1 Tabs are children of one layout

```text
/admin  canMatch: [adminCanMatch]  loadChildren      ← the chunk, withheld not hidden
 └─ ''  AdminLayout                                  ← the 190px rail (pills below 768px)
      ├─ ''        → redirect to 'users'
      ├─ 'users'   AdminUsers   providers: [AdminUsersStore]
      └─ 'models'  AdminModels  providers: [AdminModelsStore]
```

Two things follow from real child routes that a signal holding a tab name cannot give: the rail marks the open tab **from the router**, which is what US-1209 asks for and what stops the mark and the content disagreeing; and the browser's back button restores the previous tab for free. `ariaCurrentWhenActive="page"` rides along with the active class, so the mark reaches a screen reader as a navigation state rather than as a colour.

### 3.2 The rail lists only the tabs that exist

`AdminNav.dc.html` draws four entries — Users, Models, MCP servers, Reports. `ADMIN_TABS` holds two. A tab is added by the story that builds its route (US-1208, US-1302), never ahead of it, for the reason US-203 already settled for the Admin entry itself: an affordance that is present and inert is worse than one that is absent. US-1207 added its entry and its route in the same commit, which is the whole rule in one line.

### 3.3 Below 768px

The rail becomes frame `5m`'s scrollable pill strip, through the existing `PillSubnav`. The breakpoint is fixed rather than an input, and it is the same one `DataTable` collapses at — a caller choosing a different width would put the rail and the rows out of step.

## 4. The directory (US-1201)

### 4.1 The URL is the source of truth

Identical in shape to [Conversation Library §3](conversation-library.md#3-the-url-is-the-source-of-truth), down to the shared `shouldReplaceHistory`, which this screen re-exports from `core/navigation/` rather than copying:

```text
user types  →  SearchInput debounce (300 ms)  →  (searched)  ┐
user pages  →  Paginator                      →  (pageChanged) ├→ _applyQuery → router.navigate
user resizes→  Paginator's select             →  (pageSizeChanged) ┘        ↓
   rows ← store ← bindQuery(() => ({ name, page, pageSize })) ← input()s ← withComponentInputBinding()
```

`_applyQuery` is the **only** writer and it writes to the router, never to the store, with `queryParamsHandling: 'merge'` so a page survives a size change and a search survives both. The loop cannot feed itself: a navigation changes `name`, which re-seeds `SearchInput`'s `linkedSignal`, whose debounce then compares the new text against the value it already holds and emits nothing. Two rules keep that true — bind `[value]` one-way, never `[(value)]`, and never navigate from an effect over a bound input.

Only a **search** may replace a history entry, and only while a term is being refined. A page change and a size change always push, because both are the discrete transition `shouldReplaceHistory` exists to push at.

### 4.2 Why `withOffsetPagination` is not composed

This is the store's one structural decision. The feature models a **running offset**: `skip` is how many rows have been loaded, `setPage` advances it by what actually came back, and `hasMore` compares the two — the shape a "Load more" list needs ([Frontend Foundation §5.2](frontend-foundation.md#52-withoffsetpaginationtake--100)).

A numbered pager is a different model. `skip` is `(page − 1) × pageSize`, and a page **replaces** the rows rather than appending to them. Reinterpreting `skip` to mean the request offset would break `hasMore` and `isFullyLoaded` for the four stores that read them as the feature defines them, so the page window lives inline here — and inline rather than as a sixth `core/state/` feature, on the rule `ConversationListStore` already states: two stores sharing a shape is a coincidence, three is a feature. **This is the first.**

Everything else copies `ConversationLibraryStore`, including the reasons: `rxMethod` + `switchMap` because typing drives it and a slow `"an"` must not paint over a fast `"anders"`; no `debounceTime`, because `SearchInput` already debounces 300 ms and emits only committed values; no `distinctUntilChanged`, because the signal source only emits on a real change and `retry()` pushes the same query again on purpose; `takeUntil(signedOut$)` on the inner request, because [`withResetOnSignOut` clears state and does not cancel work](frontend-foundation.md#55-withresetonsignout--compose-it-last); and that feature composed last.

Three details in the query are load-bearing:

- **A blank term is omitted, not sent empty.** `name=` still takes the server down its `LIKE` branch, which is a table scan over every active user for no filter at all.
- **The envelope's `pageSize` is not adopted back into state.** Every size this screen can ask for is inside the server's 1–100 clamp, so the echo is always what was sent — and adopting it would let a response overwrite a size the URL has since changed.
- **`totalPages` is derived, not read from the envelope.** The server computes the same figure from the page size _it_ used; deriving it from the size this store is about to request next keeps the pager and the request in step in the gap between a size change and its response.

### 4.3 Two states a numbered pager has and a Load more list does not

**A page past the end.** A deep link to `?page=99`, or a page emptied by deactivations since the link was shared. `isPastEnd` is its own state because the way out of it is a page change rather than a cleared search, and the template tests it **before** `hasNoMatches`, so a filtered link past the end explains the page instead of blaming a term that was working. `page() > 1` is what keeps the two apart: page 1 of a search with no results is a failed search, whatever the unfiltered directory holds.

**A count with no range to state.** Left unclamped, `countSummary` reads `Showing 2451–312 of 312 users` — visibly, and in the table's live region, right beside the empty state explaining that the page does not exist. Both the store's summary and `Paginator`'s own fall back to `No users on this page — 312 users in total`, and the pager's Previous control is genuinely disabled there rather than enabled and inert.

### 4.4 The table

Four columns — the user, the email, the permissions, the actions — mapped onto `DataTable`'s card slots (`title`, `subtitle`, `badges`, `actions`) so the same rows read below the breakpoint. The actions column carries `headerHidden` rather than an empty header, because the column still needs a name for a screen reader listing the table's columns.

- **The avatar is `aria-hidden`, always.** It abbreviates a name rendered right beside it, and announcing both would read the person twice — once spelled out letter by letter.
- **Its colour is seeded on the user id, not the name**, so a rename does not recolour the row and two people who share a name are still told apart. The four-tone ramp lives in the component's own stylesheet rather than in `_tokens.scss`: this is its only consumer, that file is global, and eight entries emitted in both themes would cost every page of the app for one component on one lazy route. Each tone carries its own ink, because the boards' two pairings disagree and only one of them clears 4.5:1.
- **The name falls back to the email.** `fullName` is legitimately empty for an account Microsoft Graph gave neither a `givenName` nor a `surname` — the API coalesces both to empty strings — and a blank cell beside a filled avatar reads as a rendering fault rather than as missing directory data. The same fallback appears in every toast and in both dialogs.
- **One badge per grant, and "No permissions" when there are none.** The empty set is a real state, not missing data: US-202 already treats a user with zero grants as a valid session.
- **A badge for an MCP-derived permission names its server**, from `permission.name` — no second request. `McpServerService` creates the permission with the server's own name and re-syncs it on rename, and the two share one uniqueness space, which is the invariant that makes the caption free.
- **The signed-in administrator's row is marked `(you)`.** Not decoration: it is the one row whose two actions the server answers differently, and saying so before the refusal beats only after.
- **Both row actions are `aria-disabled`, never natively `disabled`.** The native attribute blurs the button the instant it is pressed, stranding a keyboard reader on `<body>` for the whole round trip. `beginEdit` and `beginDeactivate` both refuse on the pending id, so the guard is real either way.
- **The error panel replaces the table** rather than sitting beside it — the rows on screen belong to a query that failed. A revoked Administrator grant arrives here as `/problems/permission-required`, which `ErrorPanel` already names and correctly refuses to offer a retry for.
- **The count is announced once.** `countSummary()` goes to `DataTable`'s live region, and `Paginator` is told `[liveSummary]="false"` so its identical visible copy does not read the same sentence a second time.
- **The order is stated, as US-705 states it on the conversations library.** A muted "By last name" carries the full explanation as visually-hidden text, with an info button repeating it as a `Tooltip` flyout for the reader who is looking rather than listening. Withheld during a first load and beside an error, where it would describe a result set that is not there.

Three empty states, because they are three situations: past the end (**Go to the first page**), no matches for a term (**Clear search**, with the term quoted from `users.name()` rather than from the component's input, so the heading cannot name a term whose results have not arrived), and a genuinely empty directory.

### 4.5 Last active is absent, and that is recorded

Frame `5a` draws a **Last active** column and frame `5c` a `· last active yesterday` clause. Both are absent, and not because they were forgotten: **`UserDto` carries no timestamp of any kind**, and the entity's `DateModified` moves only when an administrator edits the row — never when the user signs in. Rendering `dateModified` under a "Last active" heading would be a plausible-looking value that is wrong for exactly the users an administrator is looking for.

It is logged in the [build order](../prd/enterprise-ui-rebuild-build-order.md)'s interim-behaviours table and marked in both components with a comment naming the reason, so it does not quietly become permanent. Closing it needs a backend enabler that stamps a sign-in timestamp and exposes it.

## 5. Two stores, and the seam between them

### 5.1 Root for the requests, route-scoped for the rows

| Store              | Scope | Owns                                                                         |
| ------------------ | ----- | ---------------------------------------------------------------------------- |
| `UserActionsStore` | root  | Every request, the permission catalogue, and the state of all three surfaces |
| `AdminUsersStore`  | route | The rows, the page window, and the optimistic removal                        |

The split is the one [Conversation Library §4.6](conversation-library.md#46-bulk-delete-why-the-request-is-not-in-this-store) records, for the same reason: `rxMethod` binds its subscription to the injector that declared it. An administrator who confirms a deactivation and then clicks another admin tab would otherwise have the `DELETE` torn down mid-flight — the row already gone optimistically, nothing deleted, nothing restored and nothing said.

`UserActionsStore` is `providedIn: 'root'` and still rides the lazy admin chunk, because that is a DI scope rather than a chunk assignment; nothing eagerly reachable imports it, and `check-initial-chunk.mjs` confirms the area stays off the initial graph.

Concurrency is chosen per flow, and each choice means something:

| Flow            | Operator     | Why                                                                                                                                                                                     |
| --------------- | ------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| create / update | `exhaustMap` | A second submit while one is in flight is a double-click, not a second user — and a duplicate that slipped through would take the "already exists" 400 the first one had just made true |
| permissions     | `switchMap`  | Reopening a form while the first load is on the wire replaces it, and only the newest answer may fill the checklist                                                                     |
| deactivate      | `mergeMap`   | Deactivations of different rows are independent and may overlap; `exhaustMap` would silently drop the second                                                                            |

Two flags are easy to conflate and are deliberately separate. **`formBusy` tracks the request, not the surface** — a form force-closed mid-flight (a second Escape is uncancelable in Chromium) leaves it true until the request settles, so a reopened form honestly reports that `exhaustMap` is still occupied rather than swallowing the next submit silently, and `cancelForm()` does not clear it. **`pendingIds` tracks one row's own request**, so a save disables that row's two buttons and not forty; both it and the checklist's selection are **replaced, never mutated**, because `patchState` and `model` compare by reference and an in-place `Set` edit performs no signal write at all.

### 5.2 `adminEvents` — server-confirmed facts only

The Events plugin earns its place here on scope, exactly as it does for `projectEvents`: the root store cannot reach into a route-scoped one, and `core/` may not import from `features/`. Four events, each carrying what the server returned:

| Event                 | Fired on                                          | What the observers do                                                                 |
| --------------------- | ------------------------------------------------- | ------------------------------------------------------------------------------------- |
| `userCreated`         | `POST api/users` → 201, the DTO                   | **Re-reads the page** (§5.3)                                                          |
| `userUpdated`         | `PUT api/users/{id}` → 200, the DTO               | Patches the row in place if it holds it, and ignores it otherwise                     |
| `userDeactivated`     | `DELETE api/users/{id}` → 204, the id             | Drops the row if it still holds it — idempotent, since this screen already removed it |
| `modelCatalogChanged` | `POST`, `PUT` or `DELETE` on `api/models`, `void` | **Two** observers, and both refetch the whole catalog (§10.6)                         |

Optimistic patches and their rollbacks stay inside the store that made them; an observer never has to undo one. The fourth event is the odd one out in two ways, both explained in §10.6: it carries no payload, and it is the only one with an observer outside the admin area — the chat picker's root `ModelCatalogStore`, which must not go on offering a model an administrator has just retired. US-1208 adds the MCP server events to this group.

### 5.3 A creation re-reads; an update patches in place

Rows are ordered by last name then first name, so **a new user's slot is the server's to decide** — and on any page but the one it belongs to it must not appear at all. Inserting it locally would put it at whichever end the client guessed, on every page. So `userCreated` re-runs the current query.

`userUpdated` is the opposite call, and for a reason that only holds because of what frame `5c` edits: permissions only, so the sort key cannot have moved. Patching in place is correct here and would not be if the panel ever edited a name.

### 5.4 The optimistic removal, and the callback that undoes it

```ts
const removed = this.users.takeRow(target.id); // optimistic, on the route
this.actions.confirmDeactivate(() => this.users.restoreRow(removed)); // the request, in core/
```

Because `features → core` is the permitted direction and not the reverse, the rollback travels back as a **callback**, exactly as `ConversationActionsStore.deleteMany` does.

Two details are load-bearing:

- **The page is left one row short rather than refetched.** A refetch would repaint a table the administrator is looking at and pull an unrelated row up from the next page, which reads as if the deactivation had touched two people.
- **`restoreRow` refuses a stale restore.** `_queryGeneration` is bumped by every query, and a restore whose generation no longer matches is dropped rather than splicing one search's row into another's result set.

The row also goes back **before** the refusal in §9 is rendered, so the message has something to render on.

## 6. Add a user (US-1202)

### 6.1 A work email and a checklist, and nothing else

No name, role, status, password or confirm-password field, because `CreateUserActionDto` has none of them: the display name and the object id come from Microsoft Graph, and sign-in is Entra's. The row's primary key **is** the directory object id, which is what lets the account provisioned here be the same row the person signs in against later ([User Management §4.2](../users/user-management.md#42-administrator-pre-creation)).

It is the app's third Signal Form and copies `ProjectFormDialog` end to end, including the part that is easy to miss: the server's verdict arrives through a declarative `validate` rule that reports it only while the field still holds the exact (trimmed) address the server rejected. That is what clears the inline message on the next keystroke — Signal Forms has no `setErrors`, so an imperative reset would have nothing to reset. `disabled` is a schema rule rather than a `[disabled]` binding, and there is no literal `maxlength`, because the `[formField]` directive owns both attributes (NG8022).

### 6.2 Four rejections at one field, and only three are validation problems

| Rejection                                        | Shape                       | How it reaches the field                |
| ------------------------------------------------ | --------------------------- | --------------------------------------- |
| Malformed or over-long address                   | 400, `errors.Email`         | `serverMessagesFor(formError, 'email')` |
| The address is already an active user            | 400, `errors.Email`         | Same                                    |
| The address matches more than one directory user | 400, `errors.Email`         | Same                                    |
| **No directory user has this address**           | **404**, no `errors` at all | `formNotFound`, held beside `formError` |

The fourth is the one worth knowing. `POST api/users` resolves the address through Microsoft Graph **before it writes anything**, and answers `/problems/resource-not-found` when the directory has no such user. That problem carries no `errors` dictionary, so it cannot travel the `serverMessagesFor` path — but it is about the email field just the same, and a toast would leave the reader guessing which of the two things they filled in was wrong. Both are gated on `formRejectedEmail`, so both clear on the next edit.

`unmatchedServerMessages` renders anything keyed to a property the form does not model — object-level rules, and the per-element `PermissionIds[0]` key the request validator uses — because a server message the user cannot see is a save that silently fails.

### 6.3 `GET api/permissions`, not `/all`

A deliberate, documented departure from the acceptance criterion. `/all` additionally returns **deactivated** permissions, and `ResolveGrantablePermissionsAsync` answers a grant of one with a 404 — so offering them would be a checkbox that can only ever fail. The plain route is already Administrator-gated and returns exactly the active rows.

The catalogue is loaded **when a form first opens**, not at construction: the store is root-scoped and most sessions never open the administration area at all. A failure renders inline in the checklist with a Try again, and leaves the form usable — a user created with no explicit grants still receives every default.

### 6.4 Nothing is pre-checked

The server grants every `IsDefault` permission **on top of** whatever is sent ([User Management §5](../users/user-management.md#5-default-permissions--the-isdefault-flag)). Seeding the defaults into the checklist would render them as choices an administrator could take away, which this request cannot do. A note under the list says so instead: _"Every default permission is granted as well, whether or not it is checked here."_

### 6.5 A failed request names its own flow

`rejectForm` takes a `FormFlow` — `{ mode: 'create' }` or `{ mode: 'edit', id }` — carried through the request rather than read back off the store. Create and edit share one store, and a surface can be force-closed mid-flight, so by the time a response lands the _other_ form may be up. Deciding from `formMode()` alone produced three distinct defects:

- a save's 404 rendered under a fresh create form's email field,
- a create's 400 rendered in the edit panel's alert region,
- and an unrelated 500 closing a panel whose checkbox edits were unsaved.

For an edit the identity is checked too — reopening the panel on somebody else while a save is in flight is the same mistake in miniature. When the surface is no longer the one on screen, the failure falls through to a toast, and only the request's **own** surface is closed. A validation problem raised that way is toasted **by hand**, because `ToastStore.fromError` silences validation problems on the assumption they render inline, and with no inline to render on there would be nothing to say them.

## 7. Edit permissions (US-1203)

### 7.1 A 420px offcanvas, and deliberately not a Signal Form

`Offcanvas` was built by US-106 **for this frame** — 420px, right-anchored, a sticky footer, focus captured on open and restored to the invoking row's Edit button on close, all of it already in the primitive and already specced ([Design System §7.2](design-system.md#72-overlays--sharedoverlay)). US-1203 added no focus code at all.

The panel is **not** a Signal Form. The only thing it edits is a _set_, and Signal Forms models fields: a checklist would be a field per permission over a model whose keys are server GUIDs, rebuilt every time the catalogue loads. The one message the server can return about it does not belong to a field either — it belongs to a checkbox (§9). The checked set is a `linkedSignal` over `formTarget`, so opening a second row replaces the first's grants, while a `null` target — the moment after a save closes the panel — keeps what is there rather than blanking the boxes while the drawer animates out.

**No fetch is needed to open it**, unlike `ProjectActionsStore.beginEdit`: `GET api/users` already returns the full `UserDto` with its active grants, so the row carries everything the panel needs and everything the PUT has to echo.

### 7.2 The whole set, stated before Save

`PUT api/users/{id}` treats `permissionIds` as the **complete desired set**, not a delta ([User Management §6](../users/user-management.md#6-the-permission-diff-on-put)). A `--warn-bg` panel says so above the checklist:

> **Saving replaces this user's entire permission set.** Unchecking a permission revokes it.

That is not decoration. An administrator who unchecks nothing but forgets to look can still revoke a grant by leaving it unchecked, and nothing after the fact would tell them.

### 7.3 The PUT echoes the profile it does not edit

`UpdateUserActionDto` requires `firstName`, `lastName` and `email` to be non-empty, and frame `5c` edits permissions only — so a body of `{ permissionIds }` alone is a 400 on three required fields, not a partial update. The three are echoed back unchanged from the row. Nothing here consults Microsoft Graph, so the echo is also the only way the profile survives the round trip.

The event carries **the server's own DTO**, with the permission set it actually stored rather than the one that was asked for.

### 7.4 MCP-derived permissions are marked, not disabled

The API permits granting and revoking them — `EnsurePermissionIsCustom` guards only edits to the permission _definition_, not to a grant of it ([User Management §5.1](../users/user-management.md#51-why-administrator-and-mcp-managed-permissions-can-never-be-default)) — the boards draw them checked and interactive, and disabling them would leave **no route to MCP tool access at all**. The lock says the row's definition is its server's; it does not say the grant is frozen.

The caption names the server from `permission.name`, which is exact rather than approximate: `McpServerService` creates the permission with the server's name and re-syncs it on rename, and the two share one uniqueness space. No second request, and nothing to go stale.

`PermissionChecklist` is dumb by construction — permissions in, checked set out — so both forms share the markup without sharing a store, and it carries the two differences the boards genuinely have rather than a preference: frame `5b`'s inline caption ("managed by its MCP server") and frame `5c`'s block caption with a lock glyph ("Managed by the Jira MCP server"). MCP-derived rows sort **last**, because the server orders by name and interleaves the two kinds, while the boards group the server-managed ones at the end where their captions read as a group rather than as four unrelated asides.

## 8. Deactivate (US-1204)

### 8.1 What the confirmation promises

It names the person, styles the destructive action red, and lands initial focus on **Cancel**, so Enter on a freshly opened confirmation revokes nobody's access. The body says what the API actually does, and the note says what it does not:

> **Ada Lovelace** will lose access at their next sign-in, and their permissions are revoked with the account.
>
> An unexpired session keeps working until its token runs out. To cut access off immediately, disable the account in Entra ID as well.

That second sentence is [User Management §8](../users/user-management.md#8-limitation--deactivation-is-not-session-revocation) put in front of the person acting on it. Deactivation blocks the next sign-in and all administrative access; it does not terminate a live session, and a confirmation that implied otherwise would be the client inventing a guarantee.

The confirm is **emitted rather than called on the store**, because only the screen can remove the row and hand back the undo (§5.4). Confirming destroys the row the modal was invoked from, so `Offcanvas`-style focus return has nothing to restore to: the dialog falls back to `#main-content`, and only when focus **actually fell through**, so a reader who clicked elsewhere mid-flight is not yanked back.

### 8.2 What happens next

The modal closes at once, the row leaves at once, and the request settles behind it: a 204 raises one success toast and dispatches `userDeactivated`; any failure puts the row back at the index it came from (§5.4) and then decides between §9's inline refusal and a toast.

## 9. The two refusals a caller causes on their own row

The server refuses two things an administrator can only do to themselves, each a 400 with an `errors` dictionary ([User Management §7](../users/user-management.md#7-safety-invariants)):

| Attempt                                | Route                   | `errors` key    | What the reader sees                                                                                                                 |
| -------------------------------------- | ----------------------- | --------------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| Clearing their own Administrator grant | `PUT api/users/{id}`    | `PermissionIds` | At the Administrator checkbox: _"You can't remove your own Administrator permission — another administrator must make this change."_ |
| Deactivating their own account         | `DELETE api/users/{id}` | `Id`            | Under their own name in the table: _"You can't deactivate your own account."_                                                        |

Four decisions hold both of them together.

**Never a toast.** Frame `5d` is explicit: these are the refusals the reader caused on purpose and can only resolve by asking somebody else, so they render at the control rather than in the corner of the screen. Both regions carry `role="alert"`, which fires when a region is inserted with its content, so the refusal is announced rather than left to be noticed.

**Recognised by key and identity, never by prose.** `isSelfActionRefusal(error, key, isSelf)` tests the `errors` key and whether the target is the caller's own row. The server's message is display text a backend change can reword, and matching it would make this silently stop working rather than fail a test. The identity half is what makes the bare key sufficient — `PermissionIds` is also the subject of the request validator's per-element rule, but FluentValidation keys that one `PermissionIds[0]` — and it also stops a future server-side rule under the same key from being reported to the reader as something they did to themselves. **Any other `PermissionIds` message still surfaces verbatim**, through `unmatchedServerMessages`.

**Worded back at the reader.** The server says "Administrators cannot revoke their own Administrator permission." — accurate, third-person, and about a class of people rather than about the person reading it. The board's wording names the way out, and it is the message the acceptance criterion quotes. The swap is exactly one message in each case; `permissionIds` is filtered out of the unmatched list **only** while its refusal is being rendered at the checkbox, so the same fact is not stated twice in two voices.

**Placement follows the space available.** The self-revocation message sits under the Administrator checkbox, matched by permission **id** rather than by name. The self-deactivation message sits in the row's name cell rather than beside its button, because the actions column is 160px and the sentence would wrap to four lines there — with `aria-describedby` on the Deactivate control tying the two together whichever way the reader meets them.

Two lifecycle details make the deactivation one behave:

- **The Deactivate control is not pre-disabled** on the caller's own row. The criterion requires the server's refusal to be what renders; the `(you)` marker says so before the attempt instead.
- **The refusal is cleared when the directory screen starts fresh**, from `AdminUsers`'s constructor, and again when the same row is tried a second time. `UserActionsStore` outlives the route, and `role="alert"` fires on insertion — without those two calls, leaving the tab and coming back would re-announce a refusal that did not just happen.

## 10. The model catalog (US-1207)

The second tab, frame `5e`: every model the deployment knows about, what it is deployed as, how much context it holds, whether it can call tools — and the three things an administrator can do to one. It is the surface the chat model picker reads from, which is why a mistake here is visible to everybody rather than only to administrators.

### 10.1 The same two-store split, one regime lower

The seam is §5's, unchanged: `ModelActionsStore` is root-scoped and owns every request because they outlive the screen; `AdminModelsStore` is route-scoped and owns the rows. What differs is everything the endpoint decides.

|                  | Directory (US-1201)                                    | Catalog (US-1207)                                    |
| ---------------- | ------------------------------------------------------ | ---------------------------------------------------- |
| Endpoint         | `GET api/users` — server-paged                         | `GET api/models/all` — the whole set, one request    |
| Regime           | **D**: server query, no sort parameter                 | **C**: client filter over a complete set             |
| Query lives in   | The URL (`?name=`, `?page=`, `?size=`)                 | Store state — no route file, no `bindQuery`          |
| Search           | A request per committed term                           | In memory, over the rows already held                |
| After a mutation | `userCreated` re-reads; `userUpdated` patches in place | `modelCatalogChanged` — everything refetches (§10.6) |
| Deleted rows     | Leave the table optimistically                         | **Stay**, muted, with a Status marker (§10.3)        |

The last two rows are the ones that surprise people, and both come from the same fact: a soft-deleted model is still returned by the route this screen reads, and a save can change a row the response does not mention.

### 10.2 Regime C, and `withClientQuery`'s first production consumer

`GET api/models/all` is unpaginated and Administrator-gated, so one request _is_ the whole catalog and a filter over it is exact. That makes this the first store to compose [`withClientQuery`](frontend-foundation.md#54-withclientquerysource-config) in production — US-104 built the feature, and `ProjectListStore` has been deferring it to US-902 because a set drained to a 500-item ceiling is not authoritative and this one always is.

Two of the feature's four config values are shaped by that:

- **`isAuthoritative` is `isFulfilled() || entities().length > 0`, not `isFulfilled` alone.** Every mutation triggers a refetch, which puts the store back into `pending`; on `isFulfilled` alone the set would read as non-authoritative for the length of every save, so US-902's sort control would vanish and come back on each one. **The rows on screen are still the whole catalog while the next copy of it is on the wire.**
- **`incompleteSetReason` is unreachable**, because nothing here can produce a partial set. It says what would have to have gone wrong rather than describing a state the screen can enter.

**There is no URL contract, and that is a decision rather than an omission.** A client-side filter over a complete set is screen state; putting it in the address bar would promise a deep link that reproduces a _server_ result the server was never asked for. So there is no `models-route.ts`, no `bindQuery`, no `_applyQuery` and no `withComponentInputBinding()` on this screen — the one place in the admin area where `AdminUsers` is the wrong template to copy.

The comparators (`name`, `provider`) **are** wired, against a table that offers no sort control, because frame `5e` draws none. That is deliberate groundwork: US-902's sibling story on this tab is then a template change rather than a store change.

Everything else follows `ConversationLibraryStore`: `rxMethod` + `switchMap`, because a refetch can overlap the one a mutation just started and only the newest answer may replace the rows; no `debounceTime`, because `SearchInput` already debounces 300 ms and emits only committed values; `takeUntil(signedOut$)` on the inner request; and `withResetOnSignOut` composed last.

### 10.3 The table, and why a retired model is still in it

Eight columns — model, provider, deployment, context, max output, tools, status, actions — mapped onto `DataTable`'s card slots so the same rows read below the breakpoint.

**Retired models are rendered, not hidden.** `DELETE api/models/{id}` is a soft delete ([Model Management §7](../models/model-management.md#7-soft-delete)) and `models/all` is the only route that returns what it produces. A table that dropped those rows would make Delete look as though it had done nothing — and the model would then be invisible everywhere in the application. So:

- **A Status column frame `5e` does not draw.** The marker is borrowed from frame `5g`'s active/inactive dot. It has to exist somewhere, because the frame's table has no other way to tell a retired row apart.
- **A second channel besides it**: the identity cell drops to `opacity: 0.65`, which takes the `Default` pill and the mono cells down with it and never below the contrast the muted token already clears.
- **No kebab at all on a retired row**, rather than a disabled one. There is no reactivate route, so every item would be an affordance that cannot work — the shown-and-inert pattern US-203 rejected for the Admin entry itself. Restoring a model needs a backend enabler, and is logged in the [build order](../prd/enterprise-ui-rebuild-build-order.md)'s interim-behaviours table.

Five more details are load-bearing:

- **The `DEFAULT` pill renders in `--brand`, not frame `5e`'s `--accent`.** `--accent` (`#21A8D8`) measures **2.74:1** on light `--surface` — below the 4.5:1 AA minimum at this size — and the pill's border is the same colour, so there is no second channel to fall back on. Other badges in the app carry the same ratio; this one is the only marker of which model the whole deployment defaults to, which makes it the worst place to keep it. Flagged for the design owner beside the unresolved `--warn` question in the PRD.
- **The tools column says what it means in text.** The glyph is `aria-hidden` and a visually-hidden "Tools enabled" / "Tools not enabled" carries the meaning, because colour and shape are never the only channel (WCAG 1.4.1) and a bare tick under "Tools" reads as nothing at all.
- **Numbers are formatted in one fixed locale**, `en-US`, rather than the reader's. The app ships no `LOCALE_ID` and no i18n, so a German reader would otherwise see `32.768` beside a deployment name and a context size that are not localised at all. `formatContext` abbreviates `400000` to `400k` because the column is 90px and every value in it is six digits; a fractional value keeps one decimal, since the column is `decimal(18,2)` server-side and a model seeded outside this screen can hold one.
- **A provider this build does not know degrades to a muted dot and its raw id**, rather than to a blank cell. There is no `GET api/providers` (§10.7), so a provider seeded server-side after this build has no name here — and the row still says which provider it belongs to.
- **The count is stated over the rows on screen**: `Showing 3 of 11 models` while a term is active, `11 models in the catalog` otherwise. A client-side filter changes what the table is showing without changing what the server holds, and a count that ignored the filter would contradict the rows underneath it.

Two empty states, because they are two situations: nothing matched a term (**Clear search**, which returns focus to the field, since the button removes itself the moment rows come back), and a genuinely empty catalog.

### 10.4 The dialog binds eleven fields where frame `5f` draws eight

`CreateModelActionDto` and `UpdateModelActionDto` are the same eleven fields server-side, so one `ModelWriteBody` shape serves both verbs. The three the frame does not draw are `isReasoningEnabled` and the two `…PricePerMillionTokens`, and they are bound for one reason: **`ModelMapper` assigns every property unconditionally on a PUT**, so an edit that omitted a price would silently _clear_ a figure US-1302's cost report is built on, and an omitted `isReasoningEnabled` would read as `false`. This is the same rule `UpdateUserActionDto` imposes on the user profile (§7.3), one screen over.

What frame `5f`'s caption rules out is still ruled out: **no API key, no endpoint URL, no "available to" selector**. None of the three exists in this system.

| Field                                                       | Notes                                                                                       |
| ----------------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| `providerId`                                                | A select over `PROVIDER_OPTIONS`, the client-side mirror (§10.7). Takes initial focus       |
| `name`, `deploymentName`, `description`                     | 256 / 512 / 1024, mirroring `CreateModelActionDtoValidator` so a rejection is the exception |
| `contextWindowSize`, `maxOutputTokens`                      | Whole tokens, greater than zero                                                             |
| `inputPricePerMillionTokens`, `outputPricePerMillionTokens` | Optional. **Blank is unpriced; zero is free**                                               |
| `isToolEnabled`, `isReasoningEnabled`, `isDefault`          | Switches (`role="switch"`), each with a hint saying what it changes                         |

Four decisions inside it outlive the story.

**"Must be a whole number of tokens." is the client's own rule.** `ContextWindowSize` and `MaxOutputTokens` are `decimal(18,2)` columns whose validator carries only `GreaterThan(0)` ([Model Management §5](../models/model-management.md#5-validation)) — the server would accept `400000.5` and store it. Frame `5f` draws the refusal, so it has to live somewhere, and the client is the only place it can.

**The four numeric fields bind as `type="text"` with `inputmode`, never `type="number"`.** The native numeric control hands back `null` for anything the browser cannot parse, which throws away the text the reader typed — and frame `5f` draws `400000.5` still sitting in its field beneath the refusal. It also collapses two states the wire distinguishes: left blank (a price the deployment has not set) and typed something that is not a number. `ModelFormValue` therefore holds all four as **strings**, and `toModelBody` parses them.

**Blank is `null` and zero is `0`, and normalising one to the other would be a bug in a report.** A missing price means unpriced; summing it as zero silently understates spend, which is why the server models it as a nullable decimal rather than defaulting it. The dialog says so under the price fields rather than leaving it to be discovered.

**`toModelBody` is the only builder**, and it returns `null` rather than a body it cannot make. Both verbs go through it, so neither can drop a field on a full-representation PUT, and an unusable numeric field cannot reach a request as `NaN`.

The rest is `ProjectFormDialog`'s shape, the app's fourth Signal Form: `disabled` as a schema rule rather than a `[disabled]` binding (the `[formField]` directive owns that attribute, NG8022, as it owns `maxlength`); the server's verdict delivered through a declarative `validate` rule that reports it only while the field still holds the exact value that was sent, which is what clears it on the next keystroke; and `unmatchedServerMessages` rendering anything keyed to a property no control here displays, because a server message the reader cannot see is a save that silently fails. One difference from its predecessors: the rejected **whole form value** is held rather than one field's, because eight fields are rejectable and the server may fault more than one — and it is the raw form value rather than the parsed body, so the comparison is exact (`'0400000'` must not match the `400000` the wire carried).

### 10.5 `formBusy`, the flow, and the dialog no failure closes

Three flags with three different jobs, and conflating any two of them produces a defect that looks like something else:

| Flag                | Set by                                  | Drives                                                                     |
| ------------------- | --------------------------------------- | -------------------------------------------------------------------------- |
| `formBusy`          | **Only** the dialog's own submit        | The fields, both footer buttons, and `Modal`'s Escape/backdrop suppression |
| `pendingIds`        | Every per-row request, including a save | One row's kebab, and nothing else's                                        |
| `formRejectedValue` | A rejected submit                       | Which fields still show the server's messages                              |

**`formBusy` is deliberately not set by `setAsDefault`.** That row action has its own `mergeMap` flow. Because the flag also suppresses Escape and backdrop dismissal, a row action that set it would trap the reader inside a dialog _they_ opened, waiting on a request that is not theirs — a keyboard trap (WCAG 2.1.2) produced by an unrelated click. `beginCreate` and `beginEdit` refuse while it is true for the same reason from the other side: a dialog opened then would contain no focusable field and no way out until an unrelated request settled. Neither `cancelForm()` nor the screen's constructor clears it, because it tracks a request rather than a surface, and a flight still occupying `exhaustMap` must stay visible.

Concurrency is chosen per flow, and each choice means something:

| Flow            | Operator     | Why                                                                                                                                                                                            |
| --------------- | ------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| create / update | `exhaustMap` | A second submit while one is in flight is a double-click, not a second model                                                                                                                   |
| set as default  | `mergeMap`   | Promotions of different rows are independent; sharing `_update`'s `exhaustMap` would have silently dropped the reader's own save. Same-row overlap cannot happen — the guard is the pending id |
| retire          | `mergeMap`   | Retirements of different models are independent and may overlap; `exhaustMap` would drop the second                                                                                            |

**No failure closes the dialog.** Eleven fields, one of them a 1024-character description, are behind it — and `retryInterceptor` retries neither a POST nor a PUT, so discarding them because a 502 came back would cost the reader every one of them with no automatic second attempt. The failure is said, the values stay, and the reader decides whether to try again.

Where it is said depends on the flow, which **identifies itself** rather than being read back off the store (§6.5's defect, in a second store):

- A **validation problem** on the surface that produced it renders inline, per field, and raises no toast.
- A **create's 404** lands inline at the Provider field. `CreateModelAsync` throws exactly one `NotFoundException` — `EnsureProviderExistsAsync`'s — so on that flow a 404 can only be about the provider, which is a real case: a deployment can deactivate a provider row this build still offers. It carries no `errors` dictionary, so it travels beside `formError` rather than through `serverMessagesFor`.
- An **edit's 404 is ambiguous** — the model may have been retired by somebody else — so it is toasted rather than guessed at.
- Anything reaching a surface that is **no longer the one on screen** is toasted, including a validation problem, which has to be raised by hand because `ToastStore.fromError` silences those on the assumption they render inline.
- A **row action's failure** is always a toast: no form produced it, so there is nowhere inline to put it.

The screen's constructor calls `cancelForm()` and `cancelDeactivate()`, because `ModelActionsStore` outlives the route and a dialog left open would otherwise re-mount `showModal()` on a surface the reader never asked for when they came back to the tab.

### 10.6 Retiring a model, and the event every mutation raises

The confirmation names the model, styles the destructive action red, and lands initial focus on **Cancel**. It says what a soft delete does and what it does not:

> **Claude Sonnet 4.5** is removed from every user's model picker. It stays on the conversations that already used it, and it cannot be brought back from here.

When the target is the default it adds a second line, and that one is a statement of fact rather than a caution: `DeactivateModelAsync` clears `IsDefault` in the same statement that sets `DateDeactivated` and promotes nothing in its place, so **the deployment is left with no default at all** until an administrator sets another.

Unlike `DeactivateUserDialog` this confirms **on the store** rather than emitting to the screen. Nothing is removed optimistically and there is no rollback to thread back: the row stays in the table and turns muted when the refetch lands. That also means no focus fallback is needed — the kebab it was invoked from is still in the document for `Modal` to return focus to.

**`adminEvents.modelCatalogChanged` is `type<void>()`, and every observer refetches.** The three user events carry a DTO and patch a row in place; this one deliberately cannot, and three facts are why:

- `POST` and `PUT` return only the model they saved, while saving one with `isDefault` **demotes another row the response does not describe** ([Model Management §6](../models/model-management.md#6-single-default-invariant));
- `DELETE` additionally clears `IsDefault` on the row it retires, so the catalog can be left with no default at all;
- the catalog is unpaginated and small, so a refetch costs one request and is the only thing that can show either.

It has **two** observers, and the second is the whole point of the event:

| Observer                   | Why it must hear this                                                                                                                                                |
| -------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `AdminModelsStore` (route) | The table would otherwise show two default rows, or a stale one                                                                                                      |
| `ModelCatalogStore` (root) | The chat composer's model menu reads it. Without this the picker goes on offering a retired model for the rest of the session, and a turn started on one takes a 404 |

The root store's handler calls `reload()` rather than `ensureLoaded()`: the memo is already resolved by the time an administrator can change anything, so the memoized call would hand back the stale list — and `reload()` also replaces the memo, so a later `ensureLoaded()` sees the fresh one. That handler is the whole **+0.46 kB** this story adds to the initial bundle (§11.1).

### 10.7 Providers are a client-side mirror, and there are four

`ModelDto` carries only `providerId`, and **no endpoint exposes provider names to any caller, administrator included** — there is no `GET api/providers`. So `domain/api/provider.ts` mirrors the seeded `Core.Ref.Provider` rows, whose ids [are documented as matching `Providers.cs` verbatim](../models/model-management.md#8-key-files), which is what makes a client-side mirror safe. It is also why the labels live there: the seeded rows spell their names `AzureOpenAI`, `AzureAIFoundry`, `AmazonBedrock` and `Anthropic` — `nameof(...)` values, not display copy.

US-1207 added **Azure AI Foundry**, which the mirror had been missing since the provider was seeded: `Providers.cs` seeds four and the client knew three. Until this story only the chat picker read the map, and a model on the missing provider rendered without a name there too.

It shares Azure OpenAI's dot tone. The boards draw three provider hues and the palette has no fourth; inventing a brand colour is a decision for `docs/design/` rather than for this file, the provider's **name** renders in the same cell, and grouping the two Azure providers under one dot is the honest reading of it. **Flagged for the design owner.**

`PROVIDER_OPTIONS` — the select's entries — is built in `core/catalog/model-form.ts` rather than beside `providerMeta`, so it stays off the initial graph: `domain/api/provider.ts` is reached by the chat composer's model menu, and this list has exactly one consumer, on a lazy admin route. An id it offers that a deployment has since deactivated fails the save with the 404 §10.5 renders at the Provider field, which is the correct failure and the only one a client with no provider endpoint can know about.

## 11. Route, bundle and the shared kit

### 11.1 The bundle

The initial bundle measures **669.63 kB raw / 168.80 kB transfer** against the 670 kB warning and 720 kB error ceiling, with `styles` unchanged at 63.96 kB. No re-baseline — and **0.37 kB of headroom left**, so the next root-scoped store trips the warning.

| Story           | Initial raw   | Δ            | What landed on the graph                                                                                               |
| --------------- | ------------- | ------------ | ---------------------------------------------------------------------------------------------------------------------- |
| US-1201–US-1204 | 669.17 kB     | +0.27 kB     | The shared kit's — the paginator's select and its `liveSummary` opt-out, and `initialsOf` moving into `shared/avatar/` |
| **US-1207**     | **669.63 kB** | **+0.46 kB** | `ModelCatalogStore`'s event handler, and nothing else (§10.6)                                                          |

Everything else rides the lazy admin chunk (~70.2 kB), including both root-scoped action stores: `providedIn: 'root'` is a DI scope, not a chunk assignment, and nothing eagerly reachable imports either of them. `check-initial-chunk.mjs` still reports the admin area absent from the initial graph.

The 0.46 kB is worth understanding, because it is the shape of every future cross-boundary event. `ModelCatalogStore` is root-scoped and genuinely initial — the chat composer reads it — so composing `withEventHandlers` onto it puts that handler, and the `adminEvents` symbol it names, on the initial graph. The admin screens it coordinates with do not follow: an event group is a set of type tokens, not an import edge back into `features/admin/`.

### 11.2 The kit had been waiting for this epic

Three components built by US-106 **against these frames** had no consumer but `/ui-kit` until now, and between them needed two inputs:

| Component         | Built as                                                                     | What US-1201 added                                                                                                                                           |
| ----------------- | ---------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `Paginator`       | "the numbered pager of frame `5a`"                                           | A `pageSizes` select (empty by default, so frame `4a` opts in to nothing), a `liveSummary` opt-out, a past-the-end summary and a genuinely disabled Previous |
| `Offcanvas`       | "the admin detail drawer of frame `5c`" — 420px, sticky footer, focus return | Nothing                                                                                                                                                      |
| `PermissionBadge` | "in the admin user list and detail drawer"                                   | Nothing                                                                                                                                                      |

The select's selection is bound **on the options**, never as `[value]` on the `<select>`: that binding runs before `@for` inserts the options, so it sets `selectedIndex = -1` on an empty list, and the browser's reset then selects the first option — a deep link to `?size=100` would request 100 and display "25 / page". The chosen size is also validated against the offered list before it is emitted, so nothing can reach a request as `take=NaN`.

Two things did have to be built: `AvatarInitials` (§4.4) and the `AdminLayout` rail (§3).

## 12. Testing

**184 specs**: 82 across seven files as EP-12's first four stories closed, and 102 more with US-1207 — seven new files, plus four cases added to `model-catalog-store` and `admin-layout`. The suite total is **1569**.

| Area                    | Spec                      | Notable cases                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                |
| ----------------------- | ------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| The area's chrome       | `admin-layout` (7)        | `/admin` redirecting to the users tab; the open tab marked **from the router**; **only the tabs that exist** listed; the pill strip below 768px; **`/admin/models` opened by URL instantiating only its own store**; back restoring the previous tab                                                                                                                                                                                                                                                         |
| The URL contract        | `admin-users` (2)         | A page number that would send a negative offset refused; `?size=` read only as one of the three offered, so the control cannot contradict the URL                                                                                                                                                                                                                                                                                                                                                            |
| The rows                | `admin-users` (4)         | Initials, name, email and one badge per grant; **an MCP server named without a second request**; the email fallback for an account with no directory name; the caller's own row marked                                                                                                                                                                                                                                                                                                                       |
| Deep links and paging   | `admin-users` (5)         | **Term, page and size on one request**; a search dropping the page with it; back restoring the page before it; the size a deep link asked for shown; a size change returning to page 1                                                                                                                                                                                                                                                                                                                       |
| Honesty                 | `admin-users` (3)         | **No sort control, no selection column and no permission filter**; the order stated with its explanation reachable; **the count announced once**, by the table and not the pager                                                                                                                                                                                                                                                                                                                             |
| Empty and failed states | `admin-users` (4)         | A way out of a page past the end; **the page blamed rather than the term** on a filtered link past the end; the term named and cleared; a 403 showing the missing permission rather than an empty table                                                                                                                                                                                                                                                                                                      |
| The store               | `admin-users-store` (14)  | A page number turned into the offset the endpoint takes; **a page replacing rather than appending**; an in-flight query superseded rather than raced; an empty page past the end told apart from an empty directory; **a creation re-reading the page rather than inserting the row**; an update patched in place and an unheld one ignored; **a restore refused into a result set the reader replaced**; a row put back at the index it came from; sign-out cancelling and emptying                         |
| The requests            | `user-actions-store` (16) | The permissions loaded once and from **the active route, not `/all`**; MCP rows sorted last; a second submit refused; a 400 kept inline with no toast; **the directory 404 rendered at the field**; a toast when the surface has already closed; **one flow's failure never rendered on the other flow's surface**; overlapping deactivations of different rows; a standing refusal forgotten when the screen starts fresh; everything cancelled on sign-out; the form still usable when the catalogue fails |
| The create modal        | `user-form-dialog` (8)    | A work email and permissions **and nothing else**; an MCP row marked without being disabled; the trimmed address sent; the server's 400 cleared on the next keystroke; the 404 rendered at the field too; **a message the form does not model shown rather than dropped**; a second open starting empty                                                                                                                                                                                                      |
| The edit panel          | `user-edit-panel` (10)    | The held grants checked; **the whole-set warning present**; a lock-marked row still operable; **the profile echoed, because the PUT requires all three fields**; unchecking revoking; the self-revocation refusal at the Administrator checkbox and **never as a toast**; another administrator's row not mistaken for the caller's; a row that has gone since it was listed; a second row reseeding the checkboxes                                                                                          |
| Deactivation            | `deactivate-user` (7)     | The person named and the action red; the row removed on the 204; **the row put back where it was on failure**; the refusal at the control, never as a toast; a standing refusal cleared on retry; **focus to the main landmark when the invoking row is destroyed**; an unrelated failure toasted                                                                                                                                                                                                            |
| The shared kit          | `data` (4 new)            | No select unless a caller asks for one; **the size in force shown before the reader touches the control**; a size that is not on offer refused; a caller silencing the summary the table already announces                                                                                                                                                                                                                                                                                                   |

And US-1207's, on the models tab:

| Area                   | Spec                       | Notable cases                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| ---------------------- | -------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The catalog rows       | `admin-models` (16)        | The columns frame `5e` draws; exactly one row marked default; **tools stated in text, not only in colour**; **a retired row muted, marked and with no actions at all**; Set as default hidden on the row that already is; **filtering on provider without a second request**; the term named and cleared; no sort control; a 403 replacing the table; **both dialogs mounted once, outside every branch**; a dialog left standing not reopened when the tab is revisited; a provider this build does not know rendered without breaking the row                      |
| The list store         | `admin-models-store` (11)  | **`models/all`, the only route returning retired models**; a match on provider name as well as model name; **filtering without touching the server**; an empty catalog told apart from a search that found nothing; the filtered figure stated against the total; **sort offered only over a set held in full**; **the set still authoritative while a mutation's refetch is in flight**; a catalog change refetching rather than patching; sign-out cancelling and emptying                                                                                         |
| The requests           | `model-actions-store` (20) | **A second submit refused**; a body it cannot build never sent as `NaN`; a 400 kept inline with no toast; **one flow's rejection never rendered on the other's surface**; Set as default echoing the ten fields it does not change; **a row action never setting `formBusy`**, and never swallowing the reader's save; **the dialog and its eleven fields kept when a save fails**; a create's provider 404 at the field and an edit's 404 toasted; overlapping retirements; **a dialog it could not dismiss refused**; `formBusy` not cleared by a mid-flight close |
| The form dialog        | `model-form-dialog` (18)   | **The eight fields the frame draws plus the three the wire needs**; **no API key, endpoint URL or "available to"**; three switches; the whole-number refusal in the board's words; **a blank price sent as `null` and a zero one as `0`**; reasoning and both prices echoed by an edit that touches neither; the server's message at its field, cleared on the next edit; **a message the form does not model shown rather than dropped**; Azure AI Foundry offered                                                                                                  |
| The form's rules       | `model-form` (20)          | `tokenCountError` and `priceError` at their boundaries; **blank and zero as separate states in both directions**; a round trip through every field the PUT requires; **the three fields frame `5f` does not draw carried**; all four providers offered, under display names rather than the seed's `nameof` values                                                                                                                                                                                                                                                   |
| Retiring               | `deactivate-model` (7)     | The model named and the action red; initial focus on Cancel; **what a soft delete does, and does not**; **the warning that retiring the default leaves none**, and its absence otherwise; confirmed on the store, because there is no row to remove                                                                                                                                                                                                                                                                                                                  |
| The chat picker's copy | `model-catalog-store` (2)  | **A reload when an administrator changes the catalog**, and **the memo replaced**, so a later `ensureLoaded()` sees the fresh list                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| Formatting             | `model-format` (7)         | `400000` as `400k`, `32768` as `32,768`; a fraction keeping one decimal; **one fixed grouping rather than the reader's locale**; a dash rather than `NaN`                                                                                                                                                                                                                                                                                                                                                                                                            |

```bash
# from enterprise-gpt-ui/
npm test        # Vitest, single run — 1569 specs
npm run lint    # ESLint + icon, forbidden-API and token checks
npm run build   # budgets, then check-initial-chunk.mjs
npm run format  # Prettier
```

> **Verification status.** As with every epic before it, none of this has been exercised against a live API. The gates above are green and every behaviour here is asserted against `HttpTestingController` and `RouterTestingHarness`.

## 13. Deliberately not here

Each is pinned by a spec, so removing the absence fails a test rather than passing silently.

On the directory:

| Missing from frame `5a` / `5c` | Owner             | Notes                                                                                                                                                                                                     |
| ------------------------------ | ----------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Last active**                | a backend enabler | `UserDto` carries no timestamp of any kind, and `DateModified` moves only on an administrator's edit (§4.5). Recorded in the build order's interim-behaviours table                                       |
| **Any sort control**           | US-706            | `GET api/users` accepts no sort parameter — the PRD's regime **D**. A control that reordered one page would sort 25 rows out of 312 and read as if it had sorted all of them; the order is stated instead |
| **A row-selection column**     | —                 | No endpoint acts on more than one user. A checkbox that can only ever select is not a control                                                                                                             |
| **A "Permission: any" filter** | US-1206           | Waiting on the US-1205 enabler that puts `permissionId` on the endpoint. Until then it could filter only the loaded page, and a directory filter that quietly means "this page" is worse than none        |

On the catalog:

| Missing from frame `5e` / `5f`     | Owner             | Notes                                                                                                                                                                                                                                    |
| ---------------------------------- | ----------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Any sort control**               | US-902's sibling  | Frame `5e` draws none. Unlike the directory this set _is_ sortable — the comparators are wired in the store and `canSort()` is true — so closing it is a template change (§10.2)                                                         |
| **Restore for a retired model**    | a backend enabler | There is no reactivate route; `PUT` cannot clear `DateDeactivated` ([Model Management §9](../models/model-management.md#9-known-gaps-and-extension-points)). The kebab is therefore absent on a retired row rather than disabled (§10.3) |
| **A server-side whole-token rule** | —                 | The columns are `decimal(18,2)` and the validator carries only `GreaterThan(0)`, so a model seeded outside this screen can hold `400000.5` and this table will render it (§10.4)                                                         |
| **A cost column**                  | US-1302           | The two prices are set here and read there. Adding a figure to this table would duplicate the report's arithmetic in a place nobody would look for it                                                                                    |

And across both:

| Missing                  | Owner            | Notes                                  |
| ------------------------ | ---------------- | -------------------------------------- |
| The other two admin tabs | US-1208, US-1302 | The rail lists only what exists (§3.2) |

The last three catalog absences are recorded in the [build order](../prd/enterprise-ui-rebuild-build-order.md)'s interim-behaviours table, alongside the directory's Last active column.

The single-permission grant and revoke routes ([User Management §2.1](../users/user-management.md#21-related-grant-routes)) are deliberately unused: frame `5c` edits a set, and mixing a per-row request into a panel that also PUTs the whole set would make two paths to the same outcome with different failure modes.

## 14. Troubleshooting

| Symptom                                                                                     | Cause                                                                                                                                                                 |
| ------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Two requests fire when a filtered link is opened cold                                       | `bindQuery` was handed values instead of a computation, so it ran with defaults before the router's parameters arrived (§2.2)                                         |
| The field fights the user, or the term resets as they type                                  | `[(value)]` was used on `SearchInput`, or something navigates from an effect over a bound input. The loop is one-directional by construction (§4.1)                   |
| Back walks through the search one letter at a time                                          | `shouldReplaceHistory` is returning `false` while a term is being refined (§4.1)                                                                                      |
| **A deep link to `?size=100` requests 100 rows and displays "25 / page"**                   | The select's selection was bound as `[value]` on the `<select>` instead of `[selected]` on the options; the empty option list resets it to the first entry (§11.2)    |
| The summary reads `Showing 2451–312 of 312 users`                                           | The past-the-end clamp came off one of the two summaries. Both the store's and `Paginator`'s have it (§4.3)                                                           |
| A filtered link past the end tells the reader to clear a search that was working            | `hasNoMatches` was tested before `isPastEnd` in the template (§4.3)                                                                                                   |
| A screen reader reads the count twice, with two different numbers                           | `Paginator` lost `[liveSummary]="false"`, or a second count was derived instead of handing `countSummary()` to `DataTable`'s `summary` (§4.4)                         |
| **A save's failure appears under a fresh create form's email field**                        | `rejectForm` was routed by `formMode()` instead of by the flow it was given (§6.5)                                                                                    |
| An unrelated 500 closes a panel whose checkbox edits are unsaved                            | Same defect, other direction: only the failing request's **own** surface may be closed (§6.5)                                                                         |
| A permission checkbox can be ticked and the save always 404s                                | The checklist was pointed at `GET api/permissions/all`, which also returns deactivated rows (§6.3)                                                                    |
| **A save wipes the user's name and email**, or answers 400 on three fields it did not touch | The PUT body dropped the echoed `firstName`, `lastName` and `email` (§7.3)                                                                                            |
| An administrator cannot grant MCP tool access to anybody                                    | The MCP-derived checkboxes were disabled. They are lock-**marked**, not frozen (§7.4)                                                                                 |
| The self-revocation refusal stopped appearing after a backend reword                        | It was matched on the server's message. Match the `errors` key and the target's identity (§9)                                                                         |
| A refusal is re-announced when the tab is reopened                                          | `clearSelfDeactivateRefusal()` came off the screen's constructor. The store outlives the route and `role="alert"` fires on insertion (§9)                             |
| A row that failed to deactivate never comes back                                            | The rollback callback was dropped, or a query ran in between and `restoreRow` correctly refused a stale restore (§5.4)                                                |
| A checkbox looks dead — clicking changes nothing                                            | A `Set` was mutated and the same reference written back. `patchState` and `model` compare by reference, so there is no signal write at all (§5.1)                     |
| A newly created user appears at the top of every page                                       | The row was inserted locally instead of re-reading the page. Ordering is the server's (§5.3)                                                                          |
| Deleting the row an administrator was standing on drops focus to `<body>`                   | The confirmation's `#main-content` fallback was removed, or its fell-through check was inverted (§8.1)                                                                |
| The whole admin area lands in the initial bundle                                            | Something eagerly reachable imported `UserActionsStore`, `ModelActionsStore` or a file beside one. `providedIn: 'root'` is a DI scope, not a chunk assignment (§11.1) |
| The rail marks the wrong tab, or none                                                       | The tabs were made local state instead of child routes (§3.1)                                                                                                         |

And on the models tab:

| Symptom                                                                  | Cause                                                                                                                                                                         |
| ------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **A saved edit clears a model's prices**, or turns reasoning off         | The body was assembled by hand instead of through `toModelBody`. The PUT is a full representation and `ModelMapper` assigns every property (§10.4)                            |
| Two rows show `DEFAULT` after a save                                     | The response was patched into the row instead of dispatching `modelCatalogChanged`. Nothing in that response describes the row it demoted (§10.6)                             |
| The chat picker still offers a model that was just retired               | `ModelCatalogStore` lost its event handler, or it calls `ensureLoaded()` rather than `reload()` — the memo is already resolved by then (§10.6)                                |
| **Deleting a model appears to do nothing**                               | Retired rows were filtered out of the table. The delete is soft, and this route is the only one that returns what it produces (§10.3)                                         |
| The reader is stuck in a dialog they cannot dismiss                      | Something other than the dialog's own submit set `formBusy` — it is also `Modal`'s Escape and backdrop suppression (§10.5)                                                    |
| A typed value vanishes from a field when the browser rejects it          | A numeric field was bound as `type="number"`, which returns `null` for what it cannot parse (§10.4)                                                                           |
| **A blank price is stored as zero**, and a cost report reads as complete | Blank was normalised to `0` somewhere between the field and `toPrice`. Blank is `null` — unpriced — and zero is a real price (§10.4)                                          |
| The sort control disappears for a moment after every save                | `isAuthoritative` was reduced to `isFulfilled` alone. Every mutation refetches, and the rows on screen are still the whole catalog while the next copy is on the wire (§10.2) |
| A model's provider renders as a bare GUID                                | The provider was seeded server-side after this build. `provider.ts` is a mirror and there is no endpoint to fetch (§10.7)                                                     |
| A dialog reopens on its own when the models tab is revisited             | `cancelForm()`/`cancelDeactivate()` came off the screen's constructor. `ModelActionsStore` outlives the route (§10.5)                                                         |

## 15. Key files

| Concern                        | File                                                                                                                                                                                                                                                                                                                                                                                                |
| ------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The layout, the rail, the tabs | [`features/admin/admin-layout.ts`](../../enterprise-gpt-ui/src/app/features/admin/admin-layout.ts), [`admin-tabs.ts`](../../enterprise-gpt-ui/src/app/features/admin/admin-tabs.ts), [`admin.routes.ts`](../../enterprise-gpt-ui/src/app/features/admin/admin.routes.ts)                                                                                                                            |
| The directory                  | [`features/admin/users/admin-users.ts`](../../enterprise-gpt-ui/src/app/features/admin/users/admin-users.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/admin/users/admin-users.html), [`admin-users-store.ts`](../../enterprise-gpt-ui/src/app/features/admin/users/admin-users-store.ts)                                                                                                 |
| The URL contract               | [`features/admin/users/admin-users-route.ts`](../../enterprise-gpt-ui/src/app/features/admin/users/admin-users-route.ts)                                                                                                                                                                                                                                                                            |
| The three surfaces             | [`user-form-dialog.ts`](../../enterprise-gpt-ui/src/app/features/admin/users/user-form-dialog.ts), [`user-edit-panel.ts`](../../enterprise-gpt-ui/src/app/features/admin/users/user-edit-panel.ts), [`deactivate-user-dialog.ts`](../../enterprise-gpt-ui/src/app/features/admin/users/deactivate-user-dialog.ts)                                                                                   |
| The shared checkboxes          | [`features/admin/users/permission-checklist.ts`](../../enterprise-gpt-ui/src/app/features/admin/users/permission-checklist.ts)                                                                                                                                                                                                                                                                      |
| The model catalog              | [`features/admin/models/admin-models.ts`](../../enterprise-gpt-ui/src/app/features/admin/models/admin-models.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/admin/models/admin-models.html), [`admin-models-store.ts`](../../enterprise-gpt-ui/src/app/features/admin/models/admin-models-store.ts)                                                                                        |
| Its two surfaces               | [`model-form-dialog.ts`](../../enterprise-gpt-ui/src/app/features/admin/models/model-form-dialog.ts), [`deactivate-model-dialog.ts`](../../enterprise-gpt-ui/src/app/features/admin/models/deactivate-model-dialog.ts)                                                                                                                                                                              |
| The catalog's number formats   | [`features/admin/models/model-format.ts`](../../enterprise-gpt-ui/src/app/features/admin/models/model-format.ts)                                                                                                                                                                                                                                                                                    |
| Every request                  | [`core/users/user-actions-store.ts`](../../enterprise-gpt-ui/src/app/core/users/user-actions-store.ts), [`core/catalog/model-actions-store.ts`](../../enterprise-gpt-ui/src/app/core/catalog/model-actions-store.ts)                                                                                                                                                                                |
| The model form's rules         | [`core/catalog/model-form.ts`](../../enterprise-gpt-ui/src/app/core/catalog/model-form.ts)                                                                                                                                                                                                                                                                                                          |
| The chat picker's catalogue    | [`core/catalog/model-catalog-store.ts`](../../enterprise-gpt-ui/src/app/core/catalog/model-catalog-store.ts) — the second observer of `modelCatalogChanged`                                                                                                                                                                                                                                         |
| The refusal messages           | [`core/users/self-action-messages.ts`](../../enterprise-gpt-ui/src/app/core/users/self-action-messages.ts)                                                                                                                                                                                                                                                                                          |
| The events                     | [`core/events/admin-events.ts`](../../enterprise-gpt-ui/src/app/core/events/admin-events.ts)                                                                                                                                                                                                                                                                                                        |
| The wire shapes                | [`domain/api/user.ts`](../../enterprise-gpt-ui/src/app/domain/api/user.ts), [`domain/api/model.ts`](../../enterprise-gpt-ui/src/app/domain/api/model.ts), [`domain/api/provider.ts`](../../enterprise-gpt-ui/src/app/domain/api/provider.ts)                                                                                                                                                        |
| The avatar                     | [`shared/avatar/initials.ts`](../../enterprise-gpt-ui/src/app/shared/avatar/initials.ts), [`shared/avatar/avatar-initials/`](../../enterprise-gpt-ui/src/app/shared/avatar/avatar-initials/)                                                                                                                                                                                                        |
| The pager                      | [`shared/data/paginator/paginator.ts`](../../enterprise-gpt-ui/src/app/shared/data/paginator/paginator.ts)                                                                                                                                                                                                                                                                                          |
| The admin route guard          | [`core/auth/guards/admin.guard.ts`](../../enterprise-gpt-ui/src/app/core/auth/guards/admin.guard.ts), [`core/auth/auth-routes.ts`](../../enterprise-gpt-ui/src/app/core/auth/auth-routes.ts)                                                                                                                                                                                                        |
| The API side                   | [`Enterprise.Gpt.Api/Endpoints/UserEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/UserEndpoints.cs), [`UserService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/UserService.cs), [`ModelEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ModelEndpoints.cs), [`ModelService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ModelService.cs) |
| Related reference              | [User Management](../users/user-management.md), [Model Management](../models/model-management.md), [Conversation Library](conversation-library.md), [Frontend Foundation](frontend-foundation.md), [Design System](design-system.md), [Authentication and Session](authentication-and-session.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md)                                     |
