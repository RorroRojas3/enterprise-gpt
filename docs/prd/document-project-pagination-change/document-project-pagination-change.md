# PRD: Document & Project List Pagination

## 1. Overview

**Problem.** Three list surfaces in `enterprise-gpt-ui/` load data three different ways, and only one behaves the way a reader expects. Conversations (`ConversationLibraryStore`) is server-paged: 25 rows, a Load more button that appends, the button gone — not disabled — once everything is in. Documents and Projects instead *drain*: on load they fire up to five back-to-back requests at `take=100`, stop dead at a 500-row ceiling, and render a sentence where a control should be — "Showing the first 500 of 1,284 projects, most recently updated first." Past that line the two diverge again. Projects sends `name` to the server, so a search there does cross the ceiling. Documents' search is client-side (`withClientQuery` over rows already held), so it cannot — document #900 is unreachable by any control this screen offers. Neither ceiling notice tells the reader what to do about it. The result is three mental models for one app, a hard reachability wall on Documents, and up to 500 DOM nodes rendered before anyone asked to see them.

The drain was not an oversight — a fully materialised set is what let `withClientQuery` filter and sort honestly over the whole listing rather than whatever page happened to be in hand. This PRD is that tradeoff being deliberately reversed: paging bounds the DOM to what the reader asked for, and moving Documents' name filter server-side is what makes the reversal safe rather than a regression.

**Solution.** Both screens adopt the Conversations Load more pattern outright: 25 rows a page (`LIBRARY_PAGE_SIZE`'s own figure), append on request, the control removed rather than disabled once everything has loaded. Five decisions were taken by the product owner on 2026-08-29 to make that possible, stated here as settled rather than reopened anywhere below:

1. **Both screens adopt Load more.** This explicitly overrides the standing comment at `enterprise-gpt-ui/src/app/features/projects/project-list-store.ts:104-107` — *"There is no Load more here and there never will be: the grid is a card grid, and one that grew a page at a time would keep moving the reader's cursor."* EP-3 rewrites that comment into a dated record of the override rather than deleting it.
2. **Documents' name filter moves server-side.** A client-side filter under paging would search only the loaded page — worse than today's drain. `GET api/documents` gains a `name` parameter (EP-1).
3. **The grouped-by-conversation view keeps grouping loaded rows only**, with its count reworded from `8 files` to `3 of 8 files` so a group never claims to hold more than it currently shows (EP-2).
4. **No virtualization.** `@angular/cdk` stays out of the dependency tree — it is not a dependency today. Paging alone caps the DOM at what the reader asked for; this is a non-goal, not an oversight (§2).
5. **Page size 25** on both screens, matching `LIBRARY_PAGE_SIZE`.

**Success criteria.**

- **Reachability**: a document ranked beyond the former 500-row ceiling is returned by `GET api/documents?name=` — 0 documents in a caller's own library are unreachable by search, measured by an integration test seeding 600+ documents and searching for one near the end.
- **DOM bound**: the first render of `/documents` and `/projects` never places more than 25 rows/cards in the DOM, and each Load more press adds at most 25 more — measured by a node-count assertion in `documents.spec.ts`/`projects.spec.ts`, replacing today's up-to-500 assertion.
- **Request cost**: a signed-in caller with more than 25 documents or projects issues exactly one request to see the first page, not up to five — measured by the mocked-`HttpClient` request count in the same specs, and observable in production via the existing per-route request telemetry (`docs/observability/request-logging.md`).
- **Behavioural parity**: every acceptance criterion already proven for Conversations' Load more (button removed not disabled, one request per double-press, a query change cancels a superseded page, a failed page keeps its rows) holds identically for Documents and Projects — measured by the mirrored assertions added to `documents.spec.ts` and `projects.spec.ts`.
- **Accessibility parity**: `documents.a11y.spec.ts` and `projects.a11y.spec.ts` report zero new axe violations with the Load more control present and in its busy state — measured by those suites passing after this PRD, not merely before it.

A user with 1,284 projects opens `/projects` today and waits through five requests to be told the grid stopped at row 500, with the newest 784 of them permanently invisible unless they narrow with a search. After this PRD they see the first 25 immediately, press Load more if they want the 26th, and can reach every one of the 1,284 either by paging or by typing a name — the server was already searching the full set, only the drain was in the way. A colleague on Documents, hunting for a file they uploaded months ago, today gets nothing: their search box filters the 500 rows already in memory, and their file is #900. After this PRD their search reaches the server, and the file returns on the first request that names it — the reachability wall is gone because the filter that used to stop at the DOM now starts there.

## 2. Goals & non-goals

**Goals.**

- Give Documents and Projects the same paging model Conversations already ships: 25 rows, a Load more control that appends, removed — not disabled — once every row is loaded.
- Make every document in a user's library reachable by search, regardless of how many documents they have or where the target one sits in the ordering.
- Keep every existing per-screen behaviour that paging does not touch: Projects' server-side `name`/`sort`/`dir`, Documents' server-side origin filter (`?source=generated`), grouped-by-conversation view, empty/error states, sign-out cancellation.
- Bound the DOM to what the reader has actually asked to see, without introducing virtualization.
- Retire the drain machinery — `DOCUMENT_LOAD_CEILING`, `PROJECT_LOAD_CEILING`'s use on the grid, `truncated`, `ceilingNotice`, `incompleteSetReason`, `expand`-based draining — everywhere it is no longer needed, while leaving it in place everywhere it still is (`ProjectLookupStore`).

**Non-goals.**

- **`ProjectLookupStore` keeps draining.** It is not a list screen — it resolves `projectId -> name` for project chips and feeds sidebar favourites (`enterprise-gpt-ui/src/app/core/projects/project-lookup-store.ts`), and both need the whole set in hand. Converting it to Load more would leave `nameOf()` returning `null` for any project the reader had not yet paged to, which is a worse defect than the ceiling it has today. `PROJECT_PAGE_SIZE` and `PROJECT_LOAD_CEILING` (`core/projects/project-load.ts`) are untouched.
- **Project detail → Files stays unpaginated.** That route returns every active document for the one project in view, with no envelope to page against; this PRD does not add one.
- **Admin users / reports keep the numbered `<app-paginator>`.** A different, already-coherent surface; nothing here touches it.
- **`?source=generated` stays server-side on Documents**, for the reason `document-library-store.ts:69-76` already records: narrowing it client-side would report "no generated files" to any reader whose generated files sit past whatever page happened to be loaded. This PRD adds `name` beside it; it does not change how the origin filter works.
- **No sort control on Documents.** `GET api/documents` accepts no `sort`, and offering one over a paged set would draw a second sorted run beneath the first — the exact failure `US-706` refused to ship on Conversations before that endpoint accepted an order.
- **No virtualization, and no `@angular/cdk` dependency.** Decision 4. Paging alone bounds the DOM; a virtualized list solves a problem this PRD does not have once the drain is gone.
- **`withClientQuery` (`core/state/with-client-query.ts`) is not removed or changed.** It stays exactly as it is for the admin `/all` routes, which are unaffected by this PRD; only its one caller on Documents goes away.
- **No new feature flag.** This is an additive, optional query parameter on the backend and a loading-strategy swap on the frontend, with no billing risk, no new data exposure, and no schema change — unlike the AI-driven features this codebase does flag. See §8 for the rollback this PRD relies on instead.
- **Documentation is not a story in this PRD.** `docs/ui/documents-library.md`, `docs/ui/projects.md`, and `docs/ui/frontend-foundation.md` §5.4 (the regime table) all describe the drain-and-ceiling regime this PRD retires, and go stale the moment it lands — refreshing them is the standing SE Technical Writer flow's job after implementation, per this repository's own process, not a story here. `docs/prd/enterprise-ui-rebuild.md` is untouched for the same reason file-agent's own predecessor PRDs are: it is the pre-implementation record, and its regime A becomes history rather than drift.

## 3. Users & access

**Personas.**

- **Chat user**: browses their own conversations' documents and their own projects; the reader both screens exist for.
- **Chat user with a large library**: anyone whose documents or projects exceed 25 rows, and specifically anyone whose target document or project used to sit past the 500-row ceiling — the persona this PRD exists to serve.
- **Frontend engineer**: owns EP-2 and EP-3, working under the `ngrx-signal-store` skill with `ConversationLibraryStore` as the store to copy and `conversations.html`/`conversations.ts` as the control to copy.
- **Backend engineer**: owns EP-1, working under `.claude/rules/aspnet-rest-apis.md` and `csharp.md`, with `ProjectService.SearchProjectsAsync` as the filter pattern to copy.

**Role-based access.** Unchanged by this PRD. `GET api/documents` and `GET api/projects` already sit inside `.RequireAuthorization()` groups and already scope every query to `x.UserId == callerId`; the `name` parameter EP-1 adds narrows a set ownership already bounds, and introduces no new way to reach another user's rows. No permission is added, removed, or reinterpreted.

## 4. Functional requirements

| ID | Requirement | Priority | Epic(s) |
| --- | --- | --- | --- |
| FR-1 | `GET api/documents` accepts an optional `name` parameter and applies `EF.Functions.Like(x.Name, $"%{name}%")` before `CountAsync`, matching `ProjectService`/`ConversationService`'s existing pattern | P0 | EP-1 |
| FR-2 | A whitespace-only `name` is treated as absent — no `LIKE` predicate is applied — matching `ProjectService.SearchProjectsAsync`'s `IsNullOrWhiteSpace` guard | P0 | EP-1 |
| FR-3 | `UserDocumentDto` gains `ConversationDocumentCount`, counting the owning conversation's documents that match both the active origin filter and the active `name` filter | P1 | EP-1 |
| FR-4 | `ConversationDocumentMapper`'s user-facing projection becomes a factory over `(origin, name)` rather than a fixed static `Expression`, since a static property cannot close over per-request filter values | P1 | EP-1 |
| FR-5 | `DocumentLibraryStore` requests 25 rows at a time and appends on Load more, replacing the `take=100`/500-row drain | P0 | EP-2 |
| FR-6 | The Documents Load more control is present exactly while more rows exist and is removed, not disabled, once every row is loaded | P0 | EP-2 |
| FR-7 | A second Load more press on Documents while a page is already in flight issues no additional request | P0 | EP-2 |
| FR-8 | A `name` or origin (`?source=`) change in flight on Documents cancels any Load more request already in flight for the superseded query | P0 | EP-2 |
| FR-9 | A failed Load more request on Documents keeps the already-loaded rows on screen and reports the failure as a toast, not a full-panel error | P1 | EP-2 |
| FR-10 | `name` rides every Documents request — the first page and every appended page alike — so a search reaches every document in the caller's library, not just the loaded window | P0 | EP-2 |
| FR-11 | A deep link to `/documents?name=…` issues exactly one request, filtered from the first page, never an unfiltered request followed by a filtered one | P0 | EP-2 |
| FR-12 | Signing out mid-load on Documents cancels the in-flight request and clears the store's state | P0 | EP-2 |
| FR-13 | The grouped-by-conversation view groups only the rows currently loaded and labels each group's count against what is actually shown (`3 of 8 files` once `ConversationDocumentCount` is known, `3 files` otherwise) | P1 | EP-2 |
| FR-14 | The Documents Load more control is operable by keyboard, exposes its busy state via `aria-disabled`, and moves focus to a still-connected element once it removes itself | P1 | EP-2 |
| FR-15 | `ProjectListStore` requests 25 rows at a time and appends on Load more, replacing the `take=100`/500-project drain | P0 | EP-3 |
| FR-16 | `sort` and `dir` ride every appended Projects request, so a Load more page continues the exact order the first page established | P0 | EP-3 |
| FR-17 | The Projects Load more control is present exactly while more rows exist and is removed, not disabled, once every project is loaded | P0 | EP-3 |
| FR-18 | A second Load more press on Projects while a page is in flight issues no additional request; a `name` or `sort` change in flight cancels a superseded page | P0 | EP-3 |
| FR-19 | A failed Load more request on Projects keeps the already-loaded cards on screen and reports the failure as a toast | P1 | EP-3 |
| FR-20 | Signing out mid-load on Projects cancels the in-flight request and clears the grid's state | P0 | EP-3 |
| FR-21 | The Projects Load more control is operable by keyboard, exposes its busy state via `aria-disabled`, and moves focus to a still-connected element once it removes itself | P1 | EP-3 |

## 5. User experience

**Entry points & first-time flow.** Neither screen gains a new entry point — `/documents` and `/projects` are reached exactly as they are today, including every deep-link query parameter (`?name=`, `?view=`, `?source=`, `?sort=`). What changes is what happens once there are more rows than fit in the first response.

**Core experience — Documents.**

1. The screen mounts and requests the first 25 rows: `GET api/documents?skip=0&take=25`, carrying `name` and `type` whenever a filter or the origin toggle is set.
2. Skeleton rows render until the first page lands — unchanged.
3. Below the list — grouped or flat, per the existing toggle — a Load more button renders exactly while `hasMore()` is true.
4. Pressing it appends the next 25 rows: in the flat view they extend the list, in the grouped view they extend an existing group or introduce a new one, ordered exactly as the server returned them.
5. Once every row has loaded, the button is removed.
6. Typing a search term now issues a fresh, server-filtered first-page request; a document that used to sit past the 500-row ceiling returns as soon as it matches, on the very first request.

**Core experience — Projects.**

1. The screen mounts and requests the first 25 cards: `GET api/projects?skip=0&take=25`, carrying `name` and `sort`/`dir` whenever set.
2. Skeleton cards render until the first page lands — unchanged.
3. Below the grid, a Load more button renders exactly while `hasMore()` is true.
4. Pressing it appends the next 25 cards in the order the active sort established; the order never resets mid-scroll because every appended request carries the same `sort=`/`dir=` as the first.
5. Once every project has loaded, the button is removed.

**Edge cases & UI states.**

- **Loading**: skeleton rows/cards on first load (unchanged); the Load more control itself shows a spinner and a "Loading…" label while its own request is in flight, `aria-disabled="true"` throughout.
- **Empty / no matches**: unchanged — both screens already have empty and no-matches states; on Documents the no-matches state now reflects a server-filtered zero-result response rather than a client-filtered one over a drained set.
- **First-page error**: unchanged — a full-panel error replaces the list/grid, with Retry re-running the first page.
- **Next-page (Load more) error**: **new behaviour, matching Conversations** — the already-loaded rows/cards stay exactly as they were, a toast reports the failure, and the reader can press Load more again.
- **Ceiling notice**: **removed from both screens.** There is no ceiling any more, so `ceilingNotice`, `truncated`, and (on Documents) `incompleteSetReason`/`resultsPartialReason` disappear from both templates along with the state that produced them.
- **Sign-out mid-load**: the in-flight request is cancelled and the store's state clears — the same `withResetOnSignOut` + `takeUntil(signedOut$)` pair every list store already uses.

**UI/UX highlights.**

- **No new component and no new visual design.** The Load more button, its busy state, and its removed-not-disabled behaviour are exactly what Conversations already ships (`conversations.html:280-305`); Documents and Projects become new call sites of an existing pattern, not a new one.
- **Accessibility parity, not a lesser version of it.** `aria-disabled` (never the native `disabled` attribute, which would blur the button mid-press), full keyboard operability, and focus restored to a still-connected element once the control disappears — all three already proven on Conversations and now asserted identically for Documents and Projects.
- **No `<app-data-table>` `tableFooter` slot to reuse.** Conversations gets its Load more footer "for free" through `DataTable`'s projected `tableFooter` slot; Documents renders a plain `<ul>` and Projects a plain `.projects__grid` card layout, so both need their own bespoke footer markup rather than a projected one — a genuine (small) implementation difference from the reference, not a design deviation.
- **DOM stays bounded without virtualization.** 25 rows/cards on first render, 25 more per press — the reader controls how much of the list exists in the DOM at once.

## 6. Technical considerations

**Integration points.** All verified against the working tree at `e777a07`.

| Concern | Where |
| --- | --- |
| Reference paging implementation to copy | `enterprise-gpt-ui/src/app/features/conversations/conversation-library-store.ts` — `LIBRARY_PAGE_SIZE` (:58), `_runQuery`/`switchMap` (:380-409), `_loadNextPage`/`exhaustMap` (:411-435), `loadMore()`'s `!isPending() && !loadingMore() && hasMore()` guard (:479-483), `_querySuperseded$` (:194), next-page failure → toast not `setError` (:426-431) |
| Reference Load more control and its a11y | `enterprise-gpt-ui/src/app/features/conversations/conversations.html:280-305` — `aria-disabled`, button removed (not disabled) once `hasMore()` is false, `tableFooter` slot |
| Reference focus restoration | `enterprise-gpt-ui/src/app/features/conversations/conversations.ts:143-145,271-294,333-340` — `_focusAfterPage`, the effect that moves focus to `_focusLastRow()` once `loadingMore()` settles and `hasMore()` is false |
| Offset-pagination primitive, reused unchanged by both rewrites | `enterprise-gpt-ui/src/app/core/state/with-offset-pagination.ts` — `setFirstPage`, `setPage` (advances `skip` by `items.length`, not by `take`, so a short page cannot leave a gap), `resetPagination`, `hasMore`/`isFullyLoaded` |
| Documents store to rewrite | `enterprise-gpt-ui/src/app/features/documents/document-library-store.ts` — `DOCUMENT_PAGE_SIZE` (:52, value changes 100 → 25, name unchanged since nothing outside this file imports it), `DOCUMENT_LOAD_CEILING` (:63, deleted), the `withFeature(withClientQuery(...))` composition (:165-181, deleted), `groups` (:234-255, re-based on `entities()`), `_drain` (:304-352, replaced) |
| Documents screen/template | `enterprise-gpt-ui/src/app/features/documents/documents.ts` (`bindSource`/`bindQuery` at :142-143; `library.query()` read at :121 becomes `library.name()`), `documents.html:143-207` (`groups`, `library.truncated()`/`ceilingNotice()` at :203-204, deleted), `documents-route.ts` (`NAME_PARAM`, `shouldReplaceHistory` — unchanged) |
| Projects store to rewrite | `enterprise-gpt-ui/src/app/features/projects/project-list-store.ts` — the comment this PRD overrides (:104-107), `ceilingNotice` (:166-169, deleted), the `expand`-based drain (:210-261, replaced); `pageParams` (:173-191) is reused as-is since `name`/`sort`/`dir` already ride every request |
| Projects' shared page-size/ceiling constants — untouched | `enterprise-gpt-ui/src/app/core/projects/project-load.ts` — `PROJECT_PAGE_SIZE` (:5) and `PROJECT_LOAD_CEILING` (:15) still serve `ProjectLookupStore`; `ProjectListStore` gets its own `PROJECT_LIST_PAGE_SIZE = 25` rather than reusing either |
| Projects screen/template — no `DataTable`, no `tableFooter` slot | `enterprise-gpt-ui/src/app/features/projects/projects.ts`, `projects.html:1-120` — `.projects__grid` is a plain card grid; the Load more control needs its own markup below it |
| The store that deliberately keeps draining | `enterprise-gpt-ui/src/app/core/projects/project-lookup-store.ts` — resolves `projectId -> name` for chips and favourites; a Load more here would leave `nameOf()` returning `null` for anything not yet paged to |
| `withClientQuery`, staying exactly where it is | `enterprise-gpt-ui/src/app/core/state/with-client-query.ts` — still composed by the admin `/all` routes; this PRD removes exactly one caller (Documents), the feature itself is untouched |
| Backend endpoint to extend | `enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/DocumentEndpoints.cs:75` (route map), `:138-142` (`GetUserDocumentsAsync` handler — `int skip = 0, int take = 20, string? type = null` gains `string? name = null`) |
| Backend service method to extend | `enterprise-gpt-api/Enterprise.Gpt.Service/DocumentService.cs:513-561` (`GetUserDocumentsAsync`), interface declaration at `:146` |
| Server-side name-filter pattern to copy verbatim | `enterprise-gpt-api/Enterprise.Gpt.Service/ProjectService.cs:176-179` — `if (!string.IsNullOrWhiteSpace(name)) { query = query.Where(x => EF.Functions.Like(x.Name, $"%{name}%")); }`, applied before `CountAsync` |
| The mapper to change from a static property to a factory | `enterprise-gpt-api/Enterprise.Gpt.Service/Mappers/ConversationDocumentMapper.cs:64-76` — `MapToUserDocumentDtoExpression`; one call site at `DocumentService.cs:551` |
| DTO to extend | `enterprise-gpt-api/Enterprise.Gpt.Dto/UserDocumentDto.cs` — gains `ConversationDocumentCount`, alongside the existing `ConversationName` |
| Response envelope, unchanged | `enterprise-gpt-api/Enterprise.Gpt.Dto/PaginatedResponseDto.cs` — carries no `hasMore`; every store already derives it client-side as `skip < totalCount`, which is exactly what `withOffsetPagination` does |
| Index this PRD does **not** need to add — already exists | `enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/ConversationDocumentConfiguration.cs:43` — `builder.HasIndex(x => new { x.ConversationId, x.DateDeactivated })` is already present. The correlated per-row count (FR-3) reads through this index; the open risk is query cost under load (measured below), not a missing index the invocation assumed |

**Data storage & privacy.**

- No schema change and no migration. `ConversationDocumentCount` is a computed value in the response projection, never persisted anywhere.
- No new PII crosses the wire. `name` is the same document-name substring the listing already returns per row; the only new field is an integer count.
- `name` is parameterized through EF's `Like` translation, matching `ProjectService`/`ConversationService`/`UserService`'s existing pattern — no string-concatenated SQL, no new injection surface.

**Security.** No new authorization surface. `GetUserDocumentsAsync` and the Projects listing already scope every query to the caller's own `UserId`; `name` narrows a set ownership already bounds and cannot be used to reach another user's rows. Neither endpoint's route filters change.

**Scalability & performance.**

- **DOM**: capped at 25 rows/cards on first render and 25 more per Load more press, down from up to 500 rendered unconditionally on load today.
- **Round trips**: a caller with 25 or fewer documents/projects already sees everything in one request, same as today. A caller with more issues one request per screenful the reader actually asks for, rather than up to five fired automatically on mount whether the reader scrolls that far or not. A reader who does walk all the way to row 1,000 issues roughly the same total request count either way — the difference is that those requests now happen on demand instead of eagerly.
- **The correlated per-row count (FR-3) is the one open performance question in this PRD.** It reads through the existing `(ConversationId, DateDeactivated)` index once per returned row — at most 25 per page now, versus up to 500 under today's drain, so the *ceiling* on total rows scanned per listing actually drops. What has not been measured is the marginal latency `GetUserDocumentsAsync` takes on with the count added versus without it. **Mitigation and fallback**: measure `GetUserDocumentsAsync`'s p95 latency with and without the count during EP-1's implementation; if it regresses past the threshold agreed in §9, ship without `ConversationDocumentCount` and let groups label by loaded count alone (`3 files`, never `3 of 8 files`) — a template-only retreat that blocks nothing else in this PRD (US-102, US-202).

## 7. Epics & user stories

| ID | Epic | Goal | Priority | Estimate | Depends on |
| --- | --- | --- | --- | --- | --- |
| EP-1 | Documents search reaches every document | `GET api/documents` gains a server-side `name` filter and an honest per-group count, so search is no longer bounded by whatever the client happened to have loaded | P0 | M | — |
| EP-2 | Documents adopts Load more | The documents library pages 25 rows at a time behind the control Conversations already ships, with the name filter riding every request | P0 | L | EP-1 |
| EP-3 | Projects adopts Load more | The projects grid pages 25 cards at a time behind the same control, replacing its 500-project drain while keeping the server-side order honest across pages | P0 | L | — |

EP-1 and EP-3 have no dependency on each other and can start in parallel. EP-2 depends on EP-1 specifically because the store rewrite removes `withClientQuery`'s client-side name filter in the same change that introduces paging — shipping the paginated store without the server-side `name` parameter already in place would leave Documents' search working on neither the client nor the server for however long the gap lasted, which is worse than not starting the rewrite yet.

### EP-1: Documents search reaches every document

#### US-101: `[enabler]` Add server-side `name` filtering to `GET api/documents`

- **Story**: `[enabler]` Add an optional `name` query parameter to `GET api/documents`, filtering `ConversationDocuments` with `EF.Functions.Like` before `CountAsync`, on the exact pattern `ProjectService.SearchProjectsAsync` already uses. Unblocks US-201 and every story after it in EP-2.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Acceptance criteria**:
  - Given `GET api/documents?name=quarterly`, when the request is handled, then only documents whose `Name` contains "quarterly" (case-insensitive) are counted in `TotalCount` and returned in `Items`.
  - Given `name` is applied, when the query is built, then the `Like` predicate runs before `CountAsync`, so `TotalCount` reflects the filtered set, not the unfiltered one.
  - Given `name` is omitted, empty, or whitespace-only, when the query runs, then no `Like` predicate is applied — matching `SearchProjectsAsync`'s `IsNullOrWhiteSpace` guard — and the existing `type=` filter, if present, is the only narrowing applied.
  - Given both `name` and `type=generated` are supplied, when the request is handled, then both filters apply together and `TotalCount` reflects rows matching both.
  - Given the existing `GetUserDocumentsAsync` unit and integration tests, when the suite runs after this change, then every prior case still passes unmodified — a caller that omits `name` sees no behavioural change.

#### US-102: Grouped counts state how many of a conversation's documents match the active filters

- **Story**: As a chat user viewing the documents library grouped by conversation, I want a group's count to reflect how many of that conversation's documents currently match my filters, so a group never claims to hold more files than it actually does.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-101
- **Acceptance criteria**:
  - Given a conversation with 8 active documents where 3 match the active `name`/origin filters, when its `UserDocumentDto` rows are returned, then each carries `ConversationDocumentCount = 3`.
  - Given no active filters, when the same conversation's rows are returned, then `ConversationDocumentCount` equals its total active document count (8).
  - Given `ConversationDocumentMapper.MapToUserDocumentDtoExpression`, when it is inspected after this story, then it is a method taking `(origin, name)` and returning the projection `Expression`, since the previous static property cannot close over per-request filter values.
  - Given a measured p95 latency for `GetUserDocumentsAsync` with the correlated count added, when it exceeds the threshold agreed in §9, then `ConversationDocumentCount` is dropped from the response and US-203's grouped labels fall back to the loaded count alone — a decision recorded in this story's implementation, not a separate story.

### EP-2: Documents adopts Load more

#### US-201: `[enabler]` Rebuild `DocumentLibraryStore` on `withOffsetPagination` and Load more

- **Story**: `[enabler]` Replace `DocumentLibraryStore`'s drain — `withClientQuery`, `DOCUMENT_LOAD_CEILING`, `truncated`, `ceilingNotice`, `incompleteSetReason`, the `expand`-based `_drain` — with the paging shape `ConversationLibraryStore` already proves: `_runQuery`/`switchMap` for a fresh query, `_loadNextPage`/`exhaustMap` for an appended page, `_querySuperseded$` to cancel a stale page, and a Load more control wired to `loadMore()`. `name` moves onto the server via US-101. Unblocks US-202 and US-203.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-101
- **Acceptance criteria**:
  - Given `document-library-store.ts` after this story, when it is inspected, then `withClientQuery`, `DOCUMENT_LOAD_CEILING`, `truncated`, `ceilingNotice`, `incompleteSetReason`, `isResultPartial`/`resultsPartialReason`, and the `expand`-based `_drain` no longer appear, and `DOCUMENT_PAGE_SIZE` reads `25`.
  - Given a `?name=` deep link, when the screen mounts, then exactly one request fires, carrying `name` on the query string from the first request — never an unfiltered request followed by a filtered one.
  - Given an active `name` filter, when Load more fires, then the appended-page request carries the same `name` as the first page, so a search reaches every matching document regardless of how many pages have already loaded.
  - Given a Load more press while a page is already in flight, when a second press arrives, then `exhaustMap` drops it and at most one request for that page is ever on the wire.
  - Given a `name` or origin (`?source=`) change while a page is in flight, when the change lands, then `_querySuperseded$` cancels the stale page and the new query's rows replace the list with no duplicate row and no gap.
  - Given a Load more request that fails, when the failure is handled, then the already-loaded rows remain on screen, a toast reports the failure, and `store.error()` stays unset.
  - Given a sign-out while a request is in flight, when it occurs, then the request is cancelled via `takeUntil(signedOut$)` and the store's state clears via `withResetOnSignOut`.
  - Given the Documents Load more button once every row has loaded, when `hasMore()` is false, then the button is removed from the DOM (not disabled), and while a page is in flight it carries `aria-disabled="true"`.

#### US-202: Grouped view groups only what is loaded, worded honestly

- **Story**: As a chat user with grouping on, I want a group's file count to describe what is actually shown, so a partially-loaded conversation's group never overstates itself.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-201, US-102
- **Acceptance criteria**:
  - Given `documents.groups` after this story, when it is computed, then it iterates `entities()` (the rows actually loaded) rather than `withClientQuery`'s former `results()`.
  - Given a conversation with 8 documents where `ConversationDocumentCount = 8` and 3 are currently loaded, when its group renders, then the count label reads "3 of 8 files".
  - Given `ConversationDocumentCount` is unavailable — the fallback US-102 names — when a group renders, then the label reads the loaded count alone ("3 files"), never a false "of N".
  - Given every one of a conversation's documents is loaded, when its group renders, then the label reads "8 files" — the plain form, not "8 of 8 files".
  - Given Load more appends further rows belonging to an already-rendered group, when the new rows land, then that group's count label updates without a full re-render of the list.

#### US-203: Load more control matches the Conversations library's keyboard operability and focus restoration

- **Story**: As a chat user browsing my documents, I want the Load more control to behave exactly the way I already know it from Conversations, so paging feels the same everywhere in the app.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-201
- **Acceptance criteria**:
  - Given the reader presses Load more via keyboard (Enter or Space while the button is focused), when the control has focus, then the same request fires as a mouse click would.
  - Given the final page lands and the Load more control removes itself, when focus was on it, then focus moves to the last loaded row, mirroring `Conversations._focusLastRow`; if no row exists, focus falls back to the filter field.
  - Given `documents.a11y.spec.ts`'s existing axe pass, when the suite runs with the control present and mid-load (`aria-disabled="true"`), then it still reports zero violations.
  - Given a screen reader user, when the control's busy state changes, then the visually-hidden status region already used for `countSummary` also reflects the loading state, so the change is announced without duplicating the announcement.

### EP-3: Projects adopts Load more

#### US-301: `[enabler]` Rebuild `ProjectListStore` on `withOffsetPagination` and Load more

- **Story**: `[enabler]` Replace `ProjectListStore`'s drain-to-500 with the same paging shape US-201 gives Documents. `name`/`sort`/`dir` already ride every request server-side, so this is purely a loading-strategy swap: drop `truncated`, `ceilingNotice`, `PROJECT_LOAD_CEILING`'s use here, and the `expand`-based drain; add `loadingMore`, `_loadNextPage`, and `loadMore()` on a new `PROJECT_LIST_PAGE_SIZE = 25`. Rewrite the standing comment at `project-list-store.ts:104-107` into a dated record of the product-owner override rather than deleting it. Unblocks US-302.
- **Priority**: P0 · **Estimate**: L · **Depends on**: —
- **Acceptance criteria**:
  - Given `project-list-store.ts` after this story, when it is inspected, then `truncated`, `ceilingNotice`, and the `expand`-based drain no longer appear, replaced by `loadingMore`, `_loadNextPage`, and `loadMore()` on `withOffsetPagination(PROJECT_LIST_PAGE_SIZE)`.
  - Given `core/projects/project-load.ts`, when this story lands, then `PROJECT_PAGE_SIZE` and `PROJECT_LOAD_CEILING` are unchanged and still exported for `ProjectLookupStore`'s own drain.
  - Given the comment at `project-list-store.ts:104-107`, when this story lands, then it is rewritten to record the 2026-08-29 product-owner override by name — not silently deleted — matching the invocation's explicit instruction.
  - Given an active `sort`/`dir`, when Load more fires, then the request carries the same `sort=`/`dir=` as the first page, reusing the existing `pageParams` helper unchanged.
  - Given a Load more press while a page is in flight, a `name`/`sort` change in flight, a failed page, or a sign-out mid-load, then the grid behaves exactly as US-201 specifies for Documents: `exhaustMap` drops a double press, `_querySuperseded$` cancels a superseded page, a failed page keeps its cards and toasts, and `takeUntil(signedOut$)` cancels on sign-out.
  - Given a deep link carrying `?name=` and/or `?sort=`, when the screen mounts, then exactly one request fires carrying both.
  - Given the Projects Load more control once every project has loaded, when `hasMore()` is false, then it is removed from the DOM (not disabled), and it carries `aria-disabled="true"` while a page is in flight.

#### US-302: Load more control matches the Conversations library's keyboard operability and focus restoration, adapted to a card grid

- **Story**: As a chat user browsing my projects, I want the Load more control to behave exactly the way I already know it from Conversations, so paging feels the same even though this screen is a grid of cards rather than a table.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-301
- **Acceptance criteria**:
  - Given `.projects__grid` carries no `<app-data-table>`/`tableFooter` slot, when the Load more control is built, then it is written as its own markup directly below the grid rather than reusing a projected slot.
  - Given the reader presses Load more via keyboard, when the button has focus, then the same request fires as a click would.
  - Given the final page lands and the control removes itself, when focus was on it, then focus moves to a still-connected element in the grid, falling back to the search field when none exists — mirroring `Conversations._focusLastRow`'s fallback rule.
  - Given `projects.a11y.spec.ts`'s existing axe pass, when the suite runs with the control present and mid-load, then it still reports zero violations.
  - Given the sort `<select>`'s existing "never disabled" rule, when this story lands, then it still holds — pagination introduces no new reason to disable it, and no "sorting unavailable" copy is reintroduced.

## 8. Milestones & rollout

**Phases**, derived from the epic dependency graph.

| Phase | Contents | Relative estimate |
| --- | --- | --- |
| **Wave 1 — the two independent starting points** | US-101 (EP-1) and US-301 (EP-3), which share no dependency and can run fully in parallel | ~3-4 days |
| **Wave 2 — the count and the Documents rewrite** | US-102 (EP-1, needs US-101), US-201 (EP-2, needs US-101), and US-302 (EP-3, needs US-301) — three independent chains, each gated on its own wave-1 story | ~4-5 days |
| **Wave 3 — Documents polish** | US-202 (needs US-201 and US-102) and US-203 (needs US-201) | ~2 days |

**Critical path.** `US-101 → US-201 → US-202` and `US-101 → US-102 → US-202` are tied at three stories deep — US-201 and US-102 can run in parallel once US-101 closes, but US-202 needs both. `US-301 → US-302` is a shorter, fully independent two-story chain that can start on day one alongside `US-101`. Useful concurrency is at least 2 in every wave; wave 2 reaches 3 (`US-102`, `US-201`, `US-302` share no dependency edge with one another).

**Risks & mitigations.**

| Risk | Mitigation |
| --- | --- |
| The correlated per-row `ConversationDocumentCount` regresses `GetUserDocumentsAsync` latency | Measured during US-102's implementation against the p95 threshold in §9; the fallback (loaded-count-only labels) ships instead and blocks nothing downstream |
| A reviewer reads decision 1's override as silently discarding a deliberate prior design choice | US-301 rewrites the comment into a dated record rather than deleting it, so the history and the reason for reversing it both stay in the source |
| Documents and Projects drift from Conversations' exact Load more behaviour over time (a fourth mental model instead of zero) | Every EP-2/EP-3 acceptance criterion is written as "matches Conversations' own X", and the mirrored spec suites assert the same behaviours `conversations.spec.ts` already does, not a bespoke reimplementation |
| A future change reintroduces a client-side filter under paging (the exact defect this PRD retires on Documents) | US-201's acceptance criteria assert `withClientQuery` is gone from `document-library-store.ts`; a future PR reintroducing it would fail that assertion, not merely a code review |
| The three docs describing the old drain regime (`docs/ui/documents-library.md`, `docs/ui/projects.md`, `docs/ui/frontend-foundation.md` §5.4) are read as current after this ships | Named explicitly in §2 as stale-on-landing and owned by the standing SE Technical Writer flow, so nobody mistakes the gap for an oversight this PRD should have covered |

**Rollout & rollback.** No feature flag (§2's non-goal). This is an additive, optional backend parameter plus a frontend loading-strategy swap, with no schema change, no new data exposure, and no billing risk — unlike the AI-driven features this codebase gates behind a flag. Rollback is a plain code revert: EP-1's `name` parameter is additive and ignorable by any caller that omits it, and EP-2/EP-3 each ship as a single PR per store, so reverting either PR restores the prior drain behaviour exactly. No database change means a rollback never leaves stale schema behind. EP-1 ships ahead of EP-2 specifically so a revert of EP-2 alone still leaves a working, additive backend change in place.

## 9. Assumptions & open questions

**Assumptions.** Each is a guess a reviewer can veto.

- **The p95 latency threshold that triggers US-102's count fallback is 150 ms above `GetUserDocumentsAsync`'s current median**, chosen as a placeholder in the absence of a stated target, mirroring how other PRDs in this repository park an unmeasured numeric knob rather than inventing false precision. A backend engineer should confirm or replace it before implementing US-102.
- **No feature flag guards this change.** The risk profile — additive backend parameter, no schema change, no billing exposure — is judged low enough that a code revert is a sufficient rollback path. If the operator disagrees, EP-1's endpoint change is the natural place to add one, since it is the one piece with any production request-path risk at all.
- **`DOCUMENT_PAGE_SIZE` keeps its existing name and only its value changes** (100 → 25), since nothing outside `document-library-store.ts` imports it — unlike `PROJECT_PAGE_SIZE`, which `ProjectLookupStore` also needs and which this PRD therefore leaves untouched in favour of a new, Projects-grid-only `PROJECT_LIST_PAGE_SIZE`.
- **The focus-restoration fallback on both screens is the existing filter/search field**, mirroring `Conversations._focusLastRow`'s own fallback, rather than some other landing target (a heading, the toolbar). A frontend engineer implementing US-203/US-302 should confirm this reads naturally on a card grid, where "the last row" has no obvious equivalent.
- **This PRD does not otherwise touch `DocumentTypeNames`/`type=` resolution.** `name` is added alongside the existing `type` parameter, not merged into it or validated against it.
- **Phase durations in §8 are relative sizing derived from story estimates**, not a commitment; no team size was specified in the invocation and none is invented here.

**Open questions.**

- **Should `ConversationDocumentCount`'s fallback (loaded-count-only labelling) simply ship as the default from day one, with the richer "of N" figure added only once the measured cost is known to be acceptable** — rather than attempting the richer version first and retreating if it is not? *Backend engineer, before starting US-102.*
- **Is request-count via the existing per-route Application Insights logging sufficient evidence that this PRD's success criteria hold in production, or does the product owner want a dedicated Load more telemetry event** (distinguishing a manual page press from the automatic first load) for future analytics? *Product owner, if usage analytics beyond raw request counts is wanted later.*
- **Does the operator want a rollback path faster than a code revert** — for instance if EP-1's `name` filter turns out to regress `GetUserDocumentsAsync` latency for callers who never pass it — despite this PRD's assumption that a revert is sufficient? *Operator, before EP-1 ships.*
