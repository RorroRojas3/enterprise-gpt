# Documents Library

The `/documents` screen and the API underneath it: every document the signed-in user holds across every conversation — uploaded by them or produced by the assistant — on one screen. Paged 25 rows at a time behind a Load more control, narrowed at the server by origin and by name alike, grouped by conversation by default, with both filters in the URL and downloads on the click-only signed-link rules the attachment chips already follow. EP-10's first three stories landed together on 2026-08-19: the backend enabler (US-1001) and the screen it releases (US-1002, US-1003). US-1004 and US-1005 closed the epic on 2026-08-29, crossing the `Uploaded`/`Generated` discriminator the File Agent had already put on the schema ([`generated-files.md`](../file-agent/generated-files.md) §1) onto this screen. Later the same day the drain came out: the screen pages, and the name filter moved to the server so that paging did not put half the library out of reach (§4.1, §4.3).

Audience: a developer building the next paged-and-filtered listing, adding a third origin or a second server-side filter to this one, or debugging a count that disagrees with the rows, a group that will not collapse, or a filter — either one — that seems to drop documents. Read [File Attachments](file-attachments.md) first for the upload pipeline and the download rules this screen inherits, [Conversation Library §4](conversation-library.md) for the paging shape this store copies, and [Projects §4.1](projects.md#41-it-pages-and-it-used-to-drain) for the other screen that made the same move. Bare `§` references below are to sections of _this_ page.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference. The server side of upload and download stays where it was: [Document Upload and Ingestion](../documents/upload-workflow.md) and [Document Download](../documents/download-workflow.md).

## 1. Overview

| Story | What it delivers |
| --- | --- |
| **US-1001** `[enabler]` | Two listing routes: `GET api/conversations/{id}/documents` (the B2 contract) and the cross-conversation `GET api/documents` the library actually reads — plus the widened `ConversationDocumentDto`, the `(UserId, DateDeactivated)` index, and the ingestion re-check that closed a pre-existing orphan race the new listing would have made visible |
| **US-1002** | Frame `4j`'s screen: the lazy `/documents` route, the sidebar entry, `DocumentLibraryStore` (a drain then, paged now — §4.1), the custom rows with glyph/size/date/badge/conversation-link, US-804's click-only downloads, the error panel and the never-uploaded empty state |
| **US-1003** | The name filter with the term in the URL — client-side then, served now (§4.3) — and the **grouped-by-conversation default** with collapsible headers, recorded in the URL only as its deviation (`?view=flat`) |
| **US-1004** `[enabler]` | `type` on `ConversationDocumentDto` (`"Uploaded"`/`"Generated"`, member-name serialized), `?type=uploaded\|generated` on `GET api/documents`, and the removal of that route's hard-coded `Uploaded`-only predicate — the crossing that puts the File Agent's discriminator ([`generated-files.md`](../file-agent/generated-files.md) §1) on the wire |
| **US-1005** | The data-driven source badge and the **Generated only** toolbar switch, narrowing the listing at the server with the deviation recorded as `?source=generated` |

Nine decisions shape everything here, and each looks removable until you know what it prevents:

1. **One cross-conversation route, not a fan-out.** The per-conversation route alone would force one request per conversation over an unbounded library — the PRD's §9 open question, answered by shipping both routes (§3.3).
2. **The store pages, and both filters are served.** Plain regime B, with no sort offered because the endpoint accepts no order (§4.1, §4.3).
3. **A failed appended page keeps its rows and toasts; a failed first page replaces the listing.** The rows behind an appended page belong to a query that succeeded (§4.2).
4. **Grouped is the default, and the URL records only the deviation.** A bare `/documents` is the grouped view frame `4j` draws checked; only `?view=flat` is ever written (§5).
5. **The rows are custom, not `DataTable`.** The frame draws no header row, and the table has no grouping for US-1003 to grow into (§6.2).
6. **The source badge is data-driven, since US-1004/US-1005.** `ConversationDocumentTypes` ([`generated-files.md`](../file-agent/generated-files.md) §1) now reaches the client as `UserDocumentDto.type`, and `app-document-row` reads it through a small lookup table rather than a constant (§6.2).
7. **Both filters narrow at the server.** The origin always did; the name term joined it when the drain came out, because a filter over the loaded page would have searched 25 of 812 documents and reported the answer as the whole one (§4.3, §4.6).
8. **Ingestion re-checks its parent after persisting.** A conversation deleted mid-ingest has already run its document cascade, which never re-runs — so the just-inserted rows deactivate themselves to match (§3.4).
9. **The per-conversation route stays uploads-only and gains no parameter.** A generated file reaches its reader through the chip on the message that produced it, not through this listing (§3.1).

### 1.1 Where each piece lives

| Concern | Where |
| --- | --- |
| The screen | [`features/documents/documents.ts`](../../enterprise-gpt-ui/src/app/features/documents/documents.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/documents/documents.html), [`.scss`](../../enterprise-gpt-ui/src/app/features/documents/documents.scss) |
| The route-provided store | [`features/documents/document-library-store.ts`](../../enterprise-gpt-ui/src/app/features/documents/document-library-store.ts) |
| One row | [`features/documents/document-row.ts`](../../enterprise-gpt-ui/src/app/features/documents/document-row.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/documents/document-row.html) |
| The parameter names and the history rule | [`features/documents/documents-route.ts`](../../enterprise-gpt-ui/src/app/features/documents/documents-route.ts) |
| The wire shape | [`domain/api/document.ts`](../../enterprise-gpt-ui/src/app/domain/api/document.ts) (`UserDocumentDto`) |
| The download store the rows call | [`core/documents/document-download-store.ts`](../../enterprise-gpt-ui/src/app/core/documents/document-download-store.ts) — [File Attachments §6](file-attachments.md#6-download-us-804) |
| The lazy route, `DOCUMENTS_ROUTE`, the nav entry | [`src/app/app.routes.ts`](../../enterprise-gpt-ui/src/app/app.routes.ts), [`core/auth/auth-routes.ts`](../../enterprise-gpt-ui/src/app/core/auth/auth-routes.ts), [`features/shell/sidebar/sidebar.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.ts) |
| The API routes | [`Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs), [`DocumentEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/DocumentEndpoints.cs) |
| The queries and the ingestion re-check | [`Enterprise.Gpt.Service/ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs), [`DocumentService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/DocumentService.cs) |
| The DTOs and the mapper | [`Enterprise.Gpt.Dto/ConversationDocumentDto.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/ConversationDocumentDto.cs), [`UserDocumentDto.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/UserDocumentDto.cs), [`Enterprise.Gpt.Service/Mappers/ConversationDocumentMapper.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Mappers/ConversationDocumentMapper.cs) |
| The `?type=` token table | [`Enterprise.Gpt.Service/Filtering/DocumentTypeNames.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Filtering/DocumentTypeNames.cs) |
| The index | [`Repository/Migrations/20260819042820_AddConversationDocumentUserIndex.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260819042820_AddConversationDocumentUserIndex.cs) |
| Fixtures for specs | [`src/testing/documents.ts`](../../enterprise-gpt-ui/src/testing/documents.ts) |

## 2. Quick start

### 2.1 Reading the library

The store is provided by the `Documents` component, so it exists only while the route does:

```ts
protected readonly library = inject(DocumentLibraryStore);

//   library.entities()            → the loaded rows, in the server's newest-first order
//   library.groups()              → DocumentGroup[] over those same rows, first-seen order
//   library.name() / .hasQuery()  → the committed name term
//   library.generatedOnly()       → the committed origin filter — true once ?source=generated resolves
//   library.isFirstLoad()         → pending with nothing on screen — a skeleton is honest
//   library.isEmpty()             → loaded, and the listing holds nothing under the current query
//   library.hasNoMatches()        → loaded, nothing matched, and a term is why
//   library.countSummary()        → "Showing 25 of 312 documents" / "No generated documents"
//   library.hasMore()             → more rows exist than are loaded; the control renders
//   library.loadingMore()         → an appended page is on the wire
//   library.loadMore()            → append the next page, guarded three ways (§4.1)
//   library.error()               → AppError | null
//
//   library.bindQuery(() => ({ name, generatedOnly }));  library.retry();
```

There is no `search(term)` and no `setView(mode)` — **the only way to change the query is to navigate** (§5). `bindQuery()` is called once, from the component's constructor, over a **computation** carrying both filters, which is what makes a deep link issue one filtered request on its very first render. Its first value *is* the initial load — there is no separate `load()` call (§4.6).

### 2.2 The three URL parameters

| Parameter | Written as | History | Effect |
| --- | --- | --- | --- |
| `?name=` | The trimmed term, or the key **removed** when empty | Replaces while refined; pushes when added or removed (`shouldReplaceHistory`, shared with the conversations library) | Server-side: re-queries with `name=`, from the first page. It rides every appended page too (§4.3) |
| `?view=` | Exactly `flat`, or the key **removed** | Always pushes — on/off is the add-or-remove transition | Flat listing with the conversation link column; anything else, including absence, reads as **grouped** |
| `?source=` | Exactly `generated`, or the key **removed** | Always pushes, on the same on/off rule as `?view=` | Server-side: re-queries with `?type=generated`; anything else, including absence, reads as **every origin** and queries with no `type` at all |

All three inputs are defensively transformed: the router writes `undefined` when a key leaves the URL, `name` trims and falls back to `''`, and both `view` and `source` read as an exact-string equality (`value === 'flat'`, `value === 'generated'`) — so a hand-edited `?view=list` or `?source=uploaded` cannot put a switch and the listing out of step, the same rule the conversations library applies to `?favorites=yes`. `?source=uploaded` in particular reads as **unfiltered**, not as "uploads only": the screen never writes that value, and reading it as a no-op is what keeps the client from ever sending the server a `?type=` value the switch has no way to represent.

## 3. The two endpoints

### 3.1 `GET api/conversations/{id}/documents`

The B2 contract: one conversation's active documents, as a **bare `List<ConversationDocumentDto>`** — no envelope, no paging — ordered `DateCreated` descending, the exact mirror of the project-documents route.

| Case | Answer |
| --- | --- |
| Owner, documents exist | `200` with the list, newest first |
| Owner, no documents | `200` with `[]` — **never** 404 |
| Unknown, deactivated, or another user's conversation | `404`, deliberately indistinguishable, so the route cannot probe for conversation ids |

`ConversationDocumentDto` was widened **in place** — it had never left the server — with `extension` (lower-cased, leading dot included), `mimeType`, `size` (bytes) and `dateCreated` (when ingestion finished), matching the field set `ProjectDocumentDto` already provides so both listings key the same way. It also emits `documentId` as a read-only alias of `id`, the shape the upload-status response uses. The mapping moved to `Service/Mappers/ConversationDocumentMapper` beside its project sibling — materialized `MapToConversationDocumentDto` plus `Expression` projections, retiring the entity-file `ChatDocumentExtensions` — and every projection deliberately excludes chunk data: a listing must never drag the embedding column across the wire. Since US-1004 it also carries `type` (§3.2.1).

**Uploads only, since the File Agent shipped its `Uploaded`/`Generated` discriminator** ([`generated-files.md`](../file-agent/generated-files.md) §1), and **still uploads only after US-1004/US-1005** — the query filters `Type == Uploaded` explicitly and gains no `?type=` parameter: every surface this route feeds — the composer's chips, the files panel — is about what the user brought to the conversation, and a file the assistant made reaches them through the chip on the message that made it instead (§9 below). This is the one place the two stories deliberately left the pre-existing behaviour alone.

The rebuilt client does not call this route yet; the library reads §3.2's. It exists because US-1001's contract requires it, mirroring what a conversation-scoped surface will consume.

### 3.2 `GET api/documents?skip&take&type&name`

The library's route: every active conversation document **the caller owns**, across all conversations, in the standard `PaginatedResponseDto<UserDocumentDto>` envelope.

- **`UserDocumentDto : ConversationDocumentDto` adds `conversationName`**, projected through the `Conversation` navigation — the whole reason the screen needs no per-conversation name lookup — **and `conversationDocumentCount`** (§3.2.3).
- **Ordered `DateCreated` descending with `Id` descending as the tie-break.** `DateCreated` alone is not a total order, so two documents ingested in the same tick could straddle a page boundary and be duplicated or skipped.
- **`skip` is clamped to ≥ 0 and `take` to 1..100** — clamped rather than rejected, because the arguments come straight off the query string. The endpoint defaults `take` to 20 when omitted; the client sends 25, its own page size.
- **Owner-scoped, deliberately not permission-gated.** `Upload File` gates *adding* documents; reading back what you own follows the download routes' rule, where owning the parent is the authorization.
- **No conversation-active predicate in the query.** `DeactivateConversationAsync` and its bulk sibling deactivate a conversation's documents in the same operation, and ingestion re-checks the conversation after persisting (§3.4) — so an active document implies an active conversation, and the filter is `UserId` + `DateDeactivated` alone. That pair is exactly what migration `AddConversationDocumentUserIndex` indexes, whose `Up` also drops the conventional single-column `UserId` index the composite supersedes.
- **Both origins by default, since US-1004/US-1005.** The route's hard-coded `Type == Uploaded` predicate is gone; `GET api/documents` now lists uploaded and generated documents alike unless narrowed by `?type=` (§3.2.1). No migration and no index change went with the widening — the filter is a residual predicate on the same `(UserId, DateDeactivated)` seek, applied only when a caller asks for one origin.

#### 3.2.1 The `?type=` filter, and why it moved the route onto `.ProducesValidationProblem()`

[`DocumentTypeNames`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Filtering/DocumentTypeNames.cs) is the `?type=uploaded|generated` token table, shaped after `Service/Export/ConversationExportFormatNames.cs` (the export route's `?format=` table, [Conversation Export](../conversations/conversation-export.md)) for the identical reason: the value a caller sends, the value a rejection message advertises, and the value the client's `GENERATED_DOCUMENT_TYPE` constant writes all have to be the same token, or one of them stops matching.

- **Case-insensitive and trimmed.** A listing row carries `Generated`, the enum member name; the advertised token is lower-case `generated`. Resolving case-insensitively means a client that round-trips a row's own value back into the filter still resolves.
- **Blank means no filter — `null`, an omitted parameter, or whitespace all resolve to "every origin".** That is not a misspelling and is not rejected.
- **An unrecognised value is a validation-error 400 naming `type`,** thrown by `DocumentService.GetUserDocumentsAsync` *before any database work* — `DocumentTypeNames.Resolve` runs first, so a misspelled origin costs nothing more than the round trip to the failure. Ignoring it instead would return a correct-looking page holding documents the caller asked not to see.
- **This is what moved `DocumentEndpoints`' bare-GET route from `.ProducesProblem(400)` to `.ProducesValidationProblem()`.** A binding failure on `?skip=` or `?take=` still answers 400 with no `errors` dictionary; a rejected `?type=` now carries one keyed on `type`. OpenAPI allows one schema per status code, so the route declares the richer of the two.

#### 3.2.2 The `?name=` filter, which is what made paging safe

An optional case-insensitive substring on the document's own name, applied **before `CountAsync`** so `TotalCount` describes the filtered set rather than the whole listing:

```csharp
if (!string.IsNullOrWhiteSpace(name))
{
    query = query.Where(x => EF.Functions.Like(x.Name, $"%{name}%"));
}
```

Copied verbatim from `ProjectService.SearchProjectsAsync`, wildcards and all — `%` and `_` in a caller's term are LIKE metacharacters here exactly as they already are on `GET api/projects` and the conversation search, and the three staying identical is worth more than one of them escaping on its own.

- **Blank narrows nothing.** `null`, empty and whitespace all resolve to "every document", the same `IsNullOrWhiteSpace` guard `?type=` uses — and, unlike `?type=`, an unrecognised value is impossible, so this parameter can never be rejected.
- **It composes with `?type=`.** Both predicates land on the same query before the count, so a request carrying both is narrowed by both and `TotalCount` reflects the intersection.
- **No index, deliberately.** A leading-wildcard `LIKE` cannot seek, so it is a residual predicate on the existing `(UserId, DateDeactivated)` scan — the same arrangement `GET api/projects` has had since it shipped.
- **It is additive**, which is what let it ship a wave ahead of the client that needs it: a caller that omits it sees exactly the behaviour it had before.

#### 3.2.3 `conversationDocumentCount`, so a group can say "3 of 8"

A grouped client holds only what it has paged to, so counting the rows in hand and calling that the group's size overstates a partially loaded conversation. The row therefore carries the server's own count of its conversation's active documents **matching the same filters the listing was narrowed by**:

```csharp
ConversationDocumentCount = document.Conversation.Documents.Count(sibling =>
    !sibling.DateDeactivated.HasValue
    && (!hasOrigin || sibling.Type == originType)
    && (pattern == null || EF.Functions.Like(sibling.Name, pattern)))
```

- **`MapToUserDocumentDtoExpression` became a method over `(origin, name)`.** A static `Expression` property cannot close over per-request filter values, and the count has to re-apply them or a filtered group would be labelled against an unfiltered total.
- **The flags are hoisted out of the tree** so EF parameterizes plain values rather than translating a nullable enum comparison on every row.
- **No owner predicate is needed** — siblings under one conversation share its owner.
- **It reads through the existing `(ConversationId, DateDeactivated)` index** the per-conversation listing already added: no migration, no new index, nothing persisted. At most 25 correlated counts per page, where the drain this replaced could have run 500.
- **Its production cost is unmeasured.** [The PRD](../prd/document-project-pagination-change/document-project-pagination-change.md) §6 flags the marginal latency as its one open question and names the retreat if it disagrees: drop the field, and let groups label by loaded count alone — a template-only change, which is why the client compares rather than presence-checks (§6.3).

### 3.3 Why one cross-conversation route, and not a fan-out

US-1001's fourth acceptance criterion demanded the decision be made and recorded rather than defaulted into: the per-conversation route alone forces an N+1 over the conversation list — acceptable for a grouped view of one conversation, not for an unbounded library. The answer (product owner + backend engineer, 2026-08-19, recorded in [PRD §9](../prd/enterprise-ui-rebuild.md#9-assumptions--open-questions)): **both routes ship, and the screen never fans out.** Each item carries its conversation's id *and name* — and, since the count landed, how many of that conversation's documents match the same filters (§6.3). The client asks for one page at a time, and the envelope's `totalCount` is what keeps the on-screen count honest while the rest is unloaded.

### 3.4 The ingestion re-check

Review of US-1001 found a real defect the new listing would have surfaced: ingestion spans seconds to minutes, and a conversation deleted meanwhile has already run its document-deactivation cascade — **before the document row existed**. The cascade never re-runs, so the row landed active under a deactivated parent: an orphan no query expected, which the library would have listed and whose download would then 404.

Both `Create*DocumentAsync` ingestion paths (conversation *and* project — the project side had the same window) now re-check the parent's active state **after** `SaveChangesAsync`, and when it has gone:

1. Deactivate the just-inserted chunks, then the document, **set-based** via `ExecuteUpdateAsync` — no rowversion involved, so if the cascade did catch the rows between the insert and the re-check, this is an idempotent no-op rather than a `DbUpdateConcurrencyException` from a tracked save.
2. Throw `NotFoundException`, failing the job with the same not-found message an upload into an already-deleted conversation gets.

This shrinks the window to the cascade's own statement gap. It is also load-bearing for §3.2's query shape: it is half of what makes "an active document implies an active conversation" true.

## 4. The store: one page at a time, both filters served

[`DocumentLibraryStore`](../../enterprise-gpt-ui/src/app/features/documents/document-library-store.ts) is provided on the component, created by the route that shows it and destroyed with it. It holds **zero seeded records by construction** — a US-1002 criterion: nothing is fetched until the screen binds its query (§4.6), and no placeholder row ever ships in state.

### 4.1 Paging, and the drain it replaced

`ConversationLibraryStore`'s shape, with the same load-bearing details:

- **`DOCUMENT_PAGE_SIZE = 25`**, matching `LIBRARY_PAGE_SIZE`. The constant kept its name when its value changed, because nothing outside this file imports it.
- **`switchMap` on the query, `exhaustMap` on the page.** The first cancels a superseded search — typing drives it, and a slow `"quart"` must not paint over a fast `"quarterly"`. The second drops a double press, which would otherwise fetch the same window twice because `skip` has not moved yet.
- **`_querySuperseded$` cancels a page a later query overtook.** `switchMap` cancels one *query* for the next, but a page is a separate request on a separate `rxMethod`: without the subject, a page fetched against the old term lands after the new query's `setAllEntities` and appends a window from a list that is gone. The `conversationEvents.deleted` handler fires it too (§4.4).
- **`loadMore()` guards on all three of `!isPending() && !loadingMore() && hasMore()`.** `isPending()` is the load-bearing half: `_querySuperseded$` cannot help a page started *after* a query was already in flight.
- **`takeUntil(signedOut$)` on both requests**, because `withResetOnSignOut` clears state and does not cancel work; and `withResetOnSignOut` composed **last**, as the feature requires.

**This store drained to a 500-item ceiling until 2026-08-29.** It fired up to five requests at `take=100` on mount, rendered up to 500 rows nobody had asked for, and stated the shortfall in a sentence below the card. The product owner reversed that alongside the projects grid; what made the reversal safe rather than a regression is §4.3.

### 4.2 A failed page keeps its rows; a failed first page does not

A **first**-page failure replaces the listing with the error panel: the rows on screen belong to a query that failed, so leaving them up would show a stale result set as if it were the answer, and there is nothing behind them worth keeping. A failed **appended** page is the opposite case — the rows already loaded belong to a query that succeeded, so they stay, the failure is reported as a toast, and `store.error()` is never written. That is the conversations library's own split, and it replaces the mid-drain clear this store used to perform.

### 4.3 The name filter is served, which is why the paging is safe

`withClientQuery` is **no longer composed here**. It narrowed rows the store already held, which was honest only because the drain materialised the whole set — and even then only up to the ceiling: a document ranked past 500 was unreachable by any control this screen offered. Under paging the same feature would have been strictly worse, searching whatever page happened to be in hand.

So `GET api/documents` took a `name` parameter (§3), and the term now rides **every** request — the first page and each appended page alike:

```ts
if (trimmed !== '') {
  params = params.set('name', trimmed);   // omitted, never sent empty
}
```

Omitted rather than blank because an empty `name=` would still take the server down its `LIKE` branch. Two consequences worth holding on to:

- **A match comes back on the first request that names it**, whatever its position in the ordering. That is the reachability guarantee the whole change exists for.
- **A no-match state is now a fact about the library, not about the loaded window**, so the empty state's scope line is unqualified and `resultsPartialReason` is gone (§6.4).

The feature itself is untouched and still serves the admin `/all` routes; this removed its one caller whose set is no longer materialised in full.

Against [the four regimes](frontend-foundation.md#54-withclientquerysource-config) this screen is now plainly **regime B**: server-side paging, server-side filtering, server order, no sort control — the same regime the conversations library and the projects grid sit in.

The grouped view derives from `entities()`, so one term narrows both shapes (§6.3).

### 4.4 Staying in step: `conversationEvents`, server-confirmed facts only

- **`updated`** patches `conversationName` on every row of the affected conversation — a rename has to keep the link column and the group headers honest. It never evicts: the filter is on document names only, so a rename cannot change what matches.
- **`deleted`** removes the conversation's rows, shifts `skip` *and* `totalCount` down together (`shiftCounters`), and **cancels a page in flight**. Deleting a conversation takes its documents with it server-side, so their rows go here too rather than 404-ing on the next download; both counters move so the count and `hasMore` stay honest; and the page goes because its URL was built against the pre-removal offset — letting it land would append the *next* window and skip the rows that slid into the gap, leaving a hole with the counter reading as if nothing were missing.

### 4.5 `bindQuery` is an `rxMethod` over one object

Both filters go out on one request, so they are bound as one computation rather than as two reactive methods — binding them separately would fire a second query for every change to either. It is an `rxMethod` now, not the `signalMethod` the client-side filter used, because the term reaches the server and responses can arrive out of order: `switchMap` is what stops a slow `"quart"` painting over a fast `"quarterly"`.

### 4.6 Both filters are served, and one binding starts the listing

`generatedOnly` narrowed at the server from US-1005 onward, for a reason that now applies to the name term too: a client-side narrowing over a partial set would report "no generated files" to any reader whose generated files sit past the loaded window, and would leave `totalCount` counting rows the filter removed rather than the server's own count. Since the name term joined it, there is one binding for both:

```ts
this.library.bindQuery(() => ({ name: this.name(), generatedOnly: this.source() }));
```

- **Binding the query *is* the initial load.** There is no separate `load()` — the first value the computation produces runs the first request, on whatever filters the URL named on first render.
- **A computation, not the values.** `rxMethod` subscribes through an effect, so it reads the parameters the router has already bound: a deep link carrying `?name=` and `?source=` issues **one** request carrying both, never an unfiltered request followed by a filtered one.
- **Either filter changing supersedes a page in flight** through `_querySuperseded$` (§4.1), and replaces the rows rather than narrowing them. That is what keeps the previous query's rows from being counted, grouped or captioned for a whole round trip as though they belonged to the new one.
- **`retry()` re-runs the committed query**, both filters included — a retry after a failure must not silently drop back to the unfiltered listing.

## 5. The URL contract

The conversations library's loop, extended by one parameter ([Conversation Library §3](conversation-library.md#3-the-url-is-the-source-of-truth)): `withComponentInputBinding()` binds `?name=`, `?view=` and `?source=` to `input()`s, `_applyQuery` is the **only** writer and writes to the router with `queryParamsHandling: 'merge'` (so all three survive each other), a cleared parameter removes its key (`null`), and the loop cannot feed itself because a navigation re-seeds `SearchInput`'s `linkedSignal`, whose debounce finds the text unchanged and emits nothing. `shouldReplaceHistory` is re-exported from `core/navigation/search-history` — refining a term replaces, adding or removing one pushes, and both toggles always push because on/off *is* the add-or-remove transition.

Two of the three parameters issue a request and one does not: `?name=` and `?source=` are both query state, so either changing re-runs the listing, while `?view=` re-shapes rows already held and asks the server for nothing. The consequences of the URL-as-source-of-truth rule are the same regardless: a filtered, grouped-or-flat, origin-narrowed view is shareable, the back button undoes any of the three, and a deep link filters on the first render because `bindQuery` was handed a computation, not values.

**Grouped is the default, so the URL records only the deviation** — and the origin filter follows the identical rule. Frame `4j` draws the group switch checked; a bare `/documents` is the grouped, every-origin view, and only `?view=flat` or `?source=generated` is ever written. Encoding either default (`?view=grouped`, `?source=uploaded`) would make two URLs mean one state, and would break every bookmark if a default ever changed — and, for `?source=`, it is also what stops the client from ever sending the server a `?type=` value the switch cannot represent (only `generated` is a real request; anything else the client would have to invent).

## 6. The screen

Frame `4j`: a title, a toolbar, and a card of rows — grouped under conversation headers by default, flat with a conversation link column behind the toggle. Frame `4k` covers the error and empty states; US-1005 adds a fourth toolbar control and a second empty-state shape.

### 6.1 The toolbar

Four things, left to right: the filter field, the **Group by conversation** switch, the **Generated only** switch, and the count.

- **The field is labelled "Filter documents by name" — "Filter", not "Search".** The board's own wording, kept when the term moved to the server: it is what the reader is doing to the listing, whichever side answers.
- **The origin control is a second `.form-check.form-switch`, not a pressed button.** Frame `4j` draws no chrome for this filter at all, so the decision was the client's; a switch was chosen over a toggle button because two filters that look alike beat one that invents a second idiom for the same toolbar, and it reuses the group switch's own markup pattern.
- **The count is `aria-hidden`, mirrored by one visually-hidden `role="status"` element** that exists from first render — a live region inserted together with its content is not reliably read, and two independently derived counts would eventually disagree. It is withheld before the first response, where "No documents" would be a claim rather than a count. **The Load more busy state rides that same region**, appended to the count as one string built in a component computed rather than interpolated beside an `@if` in the template, where the formatter's line breaks become whitespace inside the announced text.
- **`countSummary` pairs the rows on screen with the envelope's `totalCount`** — "Showing 25 of 312 documents" — because paging changes what the listing shows without changing what the server holds. The loaded half counts the rows actually rendered rather than reading `skip`: the two agree after a page, but a conversation delete decrements both counters, and counting rows keeps this honest whichever path moved them. Narrowed to one origin, it says "generated documents" instead of "documents" (`documentNoun`) — a count over the assistant's own files must not read as a count over the library.
- **The whole toolbar is withheld under the error panel** — a divergence from the conversations library, whose field stays up beside its panel. Frame `4k` draws the error card alone, and over a failed first load there is nothing on screen for either control to act on.

### 6.2 The rows: custom, not `DataTable`

`app-document-row` is the `ProjectFiles` anatomy — glyph, name, size, date, then the row's controls — and **deliberately not `DataTable`**: the table always renders the header row frame `4j` does not draw, and it has no grouping for US-1003 to grow into.

- **The glyph, the size and the date reuse the existing formatters** — `fileGlyph` (the attachment chip's table, tone included), `formatBytes` (the same digits the chip and the too-large toast show) and `formatShortDate`.
- **The source badge is data-driven, since US-1004/US-1005.** `<app-source-badge [source]="source()" />` reads a small `BADGE_SOURCES` lookup (`Record<DocumentType, DocumentSource>`) keyed off the row's own `type` — a translation table because `domain/`'s wire vocabulary (`'Uploaded' | 'Generated'`) and `shared/`'s badge vocabulary (`'uploaded' | 'generated'`) are two different unions on two different layers, and a row component is the one place allowed to know both. The lookup defaults to `'uploaded'` on a miss, checked exhaustively against the union rather than against a wire that could add a member before the union does — so an origin this build does not know degrades to the existing badge rather than rendering nothing.
- **The conversation cell is an input, because the two views disagree about it.** The flat view keeps it — the link column is US-1002's criterion — and grouped rows pass `showConversation` `false`, because the group header already names and links the conversation. A host class drops the cell's grid track with it, so the badge sits flush against the download control rather than leaving a phantom column.
- **Download is US-804's flow, unchanged**: the root `DocumentDownloadStore`, `{ kind: 'conversation', id }`, the signed link fetched **on click only** — it is a five-minute bearer credential, and a list that prefetched one per row would mint a credential for every file merely looked at ([File Attachments §6](file-attachments.md#6-download-us-804)). `isRowPending` gives the row its own busy state without disabling the other thirty-nine.
- **The download control is a real `<button>` with an accessible name** ("Download {file}"), not the prototype's clickable `<i>` — the same correction the project files panel makes.

### 6.3 The grouped view

`groups` is computed over `entities()` — the rows actually loaded — so the same term narrows both view modes at the server: a group whose documents all fail the filter never comes back at all.

- **First-seen order.** The server lists documents newest first, so groups order by their newest loaded document — exactly where a refetch would put them, with no second ordering rule to maintain.
- **Each `--surface-2` header carries** a chevron button, a `bi-chat-left` glyph, the conversation's name as a link into the chat, and a count.
- **The count says how much of the group is loaded.** A group holding three of a conversation's eight matching documents reads **"3 of 8 files"**; once it holds everything the server counted, the qualifier drops and it reads "8 files" rather than "8 of 8 files". The eight comes from `conversationDocumentCount` on the row itself (§3), narrowed by the same filters the listing was, so a search moves both halves of the figure. The label is a plain comparison rather than a presence check — a response that omits the field compares false and falls through to the loaded count alone, which is the fallback [PRD §9](../prd/document-project-pagination-change/document-project-pagination-change.md) named, rather than an "of N" nobody sent.
- **The chevron carries `aria-expanded`, and `aria-controls` only while its target is rendered.** A collapsed group's list leaves the DOM, and an id reference to nothing is an axe violation, not a hint — so the attribute is nulled while the body is out.
- **Collapse state is a screen-local signal, not store state.** Which groups are folded is view state that should survive a filter change but die with the route, exactly like a scroll position. It is held as *collapsed* ids rather than expanded ones, so every group starts expanded — including groups a filter reveals later — and a filter can never hide its own matches behind closed groups. Each write replaces the `Set`, because reference equality is how signals decide anything changed.

### 6.4 The states, in precedence order

| State | Renders | Action |
| --- | --- | --- |
| `error()` | Frame `4k`'s `ErrorPanel` — "Documents couldn't load", the `traceId`, Retry — **instead of everything**, toolbar included | **Retry** re-runs the first page, on the committed query |
| `isFirstLoad()` | Six skeleton rows, `aria-busy` | — |
| `hasNoMatches()` | Frame `4b`'s shape: a heading naming the committed term, the scope line "Filters cover file names only." | **Clear filter**, which also returns focus to the field |
| `isEmpty()` **and not** `generatedOnly()` | "Nothing uploaded yet" / "Attach files in any conversation and they'll appear here.", the ridgeline motif | **None** — uploading happens in a conversation's composer, and a button here could only restate the sentence below it |
| `isEmpty()` **and** `generatedOnly()` | "No generated documents yet" / "Files the assistant creates in a conversation appear here.", the same ridgeline motif | **Show every document** — clears `?source=` *and* `?name=` together (§9), then returns focus to the origin switch |
| Otherwise | The card of rows, grouped or flat | — |

Three things in that table are decisions:

- **No-matches now wins over the empty set, and that ordering inverted when the filter moved to the server.** While the term narrowed rows the client held, zero rows with a term meant the library itself was empty — so `isEmpty` was checked first and a `?name=ghost` deep link over an empty library correctly read "Nothing uploaded yet". Served, the same response means only that the term matched nothing, and claiming the library is empty from it would be a guess the answer does not support. So the term is checked first, the heading names it, and Clear filter is the way out.
- **Clearing the term keeps the origin switch.** `clearFilter` drops `?name=` alone: clearing a search is not a request to widen every other filter with it, and the reader who turned the switch on gets to keep it.
- **`isEmpty` picks its own heading and message from `generatedOnly()`, not from a hidden third state.** With no term in play, the origin filter is the one thing that can be why nothing came back, and it is the one thing worth an undo action. **Show every document** is deliberately not called Clear filter — it clears two parameters at once, and naming what it does prevents the reader from wondering why a term they typed also vanished.

The scope line is unqualified because there is nothing left to qualify: the server searched the whole library, not the loaded window, so `resultsPartialReason` and the ceiling notice both went out with the drain.

## 7. Accessibility

`/documents` joined `AXE_ROUTES` **in the same change as the route** — the rule [the audit's coverage guard](accessibility-audit.md#52-the-coverage-guard) enforces — and `a11y-coverage.spec.ts`'s absence assertion was deleted exactly as that assertion's own comment demanded. `documents.a11y.spec.ts` runs **10 browser audits**: the original 4 (both themes × both view modes, because the two render different anatomies: group headers with chevrons and links on one side, the conversation link column on the other), **2 added by US-1005** — both themes over the filtered empty state (`/documents?source=generated` with nothing to show), the one screen state with an action button neither of the other four exercises — and **4 added with the Load more control**: both themes with the control present, and both themes with it mid-load, since `aria-disabled` on a busy control is a state axe has to see rather than one a jsdom spec can vouch for. The grouped/flat fixture also gained a `Generated` row so both source-badge variants are measured, not only the muted `Uploaded` one; an empty listing has no glyphs, badges, links or download controls in it, which is most of the surface worth auditing.

The per-control semantics are stated where they live: the collapse chevrons in §6.3 (`aria-expanded`, `aria-controls` nulled while collapsed, an `aria-label` naming the group), the count's single `role="status"` mirror in §6.1, the named download buttons in §6.2. Both listings are `role="list"` with an `aria-label`, restated explicitly because `list-style: none` strips list semantics in Safari.

## 8. Route, nav entry and bundle

`/documents` is a lazy `loadComponent` under the shell, like every other shell child: the screen and its store are code a reader who only ever opens `/chat` never runs. `DOCUMENTS_ROUTE` lives beside the other route constants, and the sidebar's **Documents** entry (`bi-file-earmark-text`, between Projects and Admin) appears now that there is a route to point it at — the sidebar renders only entries whose route exists ([Shell and Navigation §2.3](shell-and-navigation.md#23-adding-a-nav-entry)). Every glyph the screen needs was already in the icon manifest.

|                     | Initial raw   | Initial transfer | Budget (warn / error) |
| ------------------- | ------------- | ---------------- | --------------------- |
| Before EP-10        | 669.93 kB     | 168.90 kB        | 675 kB / 720 kB       |
| After US-1003       | 670.68 kB     | 169.07 kB         | 675 kB / 720 kB       |
| **After US-1004/US-1005** | **672.90 kB** | **169.64 kB** | 675 kB / 720 kB |

+0.75 kB across US-1002/US-1003, and all of it was the route record and `DOCUMENTS_ROUTE` — the screen, the store and the row ride the feature's own lazy chunk. **Unchanged by US-1004/US-1005**: the 672.90 kB figure is the same one measured after the Application Insights work several stories later, and every file this batch touched — the store, the row, the badge lookup, the toolbar switch, the empty-state action — rides that same lazy `documents` chunk. Nothing about the origin filter is reachable from `main.ts`.

## 9. Deliberately not here

Recorded in the [build order](../prd/enterprise-ui-rebuild-build-order.md)'s interim-behaviours table where a story owns the gap:

- **Frame `4i`'s pre-enabler unavailable panel was never built.** US-1002's first criterion covered the world where US-1001 had not landed; US-1001 landed first, so no client ever shipped without the endpoint behind this screen, and the skipped criterion is recorded in the store's class doc (product-owner decision, 2026-08-19).
- **The per-conversation listing (§3.1) stays uploads-only and gains no `?type=`.** A generated file surfaces there through the chip on the message that made it ([`generated-files.md`](../file-agent/generated-files.md) §8), not through a second listing route — this screen's own `GET api/documents` is the only place the origin filter exists.
- **`ProjectDocumentDto` gains no discriminator.** The `Uploaded`/`Generated` split sits on `ConversationDocument`, never `BaseDocument`; a project-scoped generated file is out of scope by the same design the storage contract states ([`generated-files.md`](../file-agent/generated-files.md) §12).
- **The name filter covers file names only** — `LIKE '%term%'` over `ConversationDocument.Name`, and the empty state says so. Widening it to the conversation name is a product decision and a server change, not an oversight to fix in passing.
- **No sort control.** No sort story exists for documents; the server's newest-first order is the only one, `comparators` is empty, and `results` stays in server order (§4.3).
- **No per-row detach.** There is **no `DELETE` route for a conversation document** — only project documents have one — so once ingested, a document belongs to its conversation and this screen offers download only. The same gap shapes the attachment chip's Remove/Dismiss split ([File Attachments §4.1](file-attachments.md#41-remove-means-two-different-things)).

## 10. Testing

**Specs across the screen's two files** — `documents.spec.ts` covers the URL contract and history rule, the view toggle, the grouped view and its collapse semantics, the non-error states and the count, the assistant-created filter, and a `describe('paging')` block: 25 rows from one request, a page appended in server order, the control **removed not disabled** when everything is loaded, `aria-disabled` while busy and during a narrowing search, the busy state announced through the one status region, a keyboard press firing the same request, and focus landing on the last row, staying on the control while more remains, and staying where the reader put it mid-flight. `document-library-store.spec.ts` covers one request rather than five, an appended page moving the offset by what came back, a double press dropped, a press refused once everything is loaded, a failed page keeping its rows and toasting, a page superseded by a newer query and by a conversation delete, the served name and origin filters riding every page, grouping and its "3 of 8" label, the two event handlers, and sign-out cancelling a page in flight. The shell spec asserts the nav entry, and `a11y-coverage.spec.ts` simply covers `/documents` like any other route.

On the API side, US-1001 added 16 unit and 12 integration tests when this screen first shipped. US-1004/US-1005 added `Filtering/DocumentTypeNamesTests.cs` (6 tests: the supported-token invariants, case-insensitivity, blank-means-nothing, the rejection message), widened `DocumentServiceTests`/`DocumentEndpointsTests` (7 more: both origins by default, narrowing to each one, casing and padding, an unsupported value naming `type`, the parameter reaching the endpoint), and `DocumentEndpointsIntegrationTests` (6 more: both origins returned together, the member-name serialization, another user's generated document excluded, the narrowed request, the validation-problem shape, and the per-conversation route still excluding a generated row) — taking the suites to **2193 unit / 463 integration**, of which the documents endpoints' own integration suite is **58**.

```bash
# from enterprise-gpt-ui/
npm test              # Vitest, single run — 2122 specs across 159 files
npm run test:a11y     # 61 browser checks in headless Chromium

# from enterprise-gpt-api/
dotnet test --filter "Category!=Integration"   # 2193 unit tests
dotnet test                                    # + 463 integration tests; Docker required
```

> **Verification status.** As with every epic before it, the UI has not been exercised against a live API: the gates are green and every behaviour is asserted against `HttpTestingController` and `RouterTestingHarness`. The two endpoints *are* exercised end-to-end by the integration suite against real SQL Server 2025.

## 11. Troubleshooting

| Symptom | Cause |
| --- | --- |
| The listing shows fewer documents than the count claims | By design while pages remain: `countSummary` reads the envelope's total, and the Load more control below the card is how the rest arrives (§6.4) |
| Typing in the filter narrows rows without a request | Something reintroduced a client-side filter. The term is a server parameter and rides every page — narrowing locally would search the loaded window and report it as the whole library (§4.3) |
| A deep link with `?name=` flashes the unfiltered list first | `bindQuery` was handed `this.name()` instead of `this.name` — it needs the signal to filter on the first render (§2.1) |
| `/documents?view=grouped` renders flat, or the switch disagrees with the listing | It cannot, unless the transform changed: anything other than `flat` reads as grouped, and only `flat` is ever written (§2.2) |
| `/documents?source=uploaded` narrows the listing, or the switch reads on | It cannot, unless the transform changed: anything other than `generated` reads as unfiltered, and only `generated` is ever written (§2.2) |
| The server rejects a request with `type=uploaded` or `type=generated` | It should not — both are in `DocumentTypeNames.Supported`. Rejected instead means the client sent something else, most likely a stray value from a hand-built query string (§3.2.1) |
| The listing shows both origins after the switch was turned on | A stale page from before the toggle landed and appended its rows — check `_querySuperseded$` fires on every query change, not only on retry (§4.1, §4.6) |
| Rows from an old term appear beneath the new one | `_querySuperseded$` was dropped, or `loadMore()` lost its `isPending()` guard: `switchMap` cancels the query, not a page already on the wire (§4.1) |
| Pressing Load more twice fetches the same window twice | `exhaustMap` became `mergeMap` or `switchMap`. `skip` has not moved when the second press lands, so only `exhaustMap` is correct here (§4.1) |
| A failed page blanks the listing | The next-page arm called `setError` instead of toasting. The rows behind an appended page belong to a query that succeeded (§4.2) |
| The Load more control is missing while rows clearly are | `hasMore()` is `skip < totalCount`; a row removed without `shiftCounters` moving **both** leaves the two agreeing when they should not (§4.4) |
| A renamed conversation still shows its old name in headers or the link column | The `conversationEvents.updated` handler came off, or the screen cached the name instead of reading the row (§4.4) |
| Rows of a deleted conversation linger and their downloads 404 | The `deleted` handler came off — or it removed rows without `shiftCounters`, leaving the count claiming rows that are gone (§4.4) |
| axe reports an `aria-controls` violation on a group header | The attribute is being rendered while the group is collapsed. It must be nulled when the body leaves the DOM (§6.3) |
| A filter appears to hide matching documents inside closed groups | Collapse state was inverted to track *expanded* ids, so new groups start collapsed. It tracks collapsed ids precisely so every group — including ones a filter reveals — starts open (§6.3) |
| "Nothing uploaded yet" while a search term is in the URL | The empty-state branches were reordered. `hasNoMatches()` is checked **first** now that the term is served — zero rows under a term says nothing about the library (§6.4) |
| Every document 404s on download for one user | Suspect the orphan race pre-dating US-1001 if the server predates it too: a document whose ingestion outlived its conversation's deletion stayed active. Current servers deactivate it at ingest time (§3.4) |
| The screen shows another user's documents | It cannot: the route reads the caller's id from the token, not a parameter. If a listing seems foreign, the wrong account is signed in |

## 12. Key files

| Concern | File |
| --- | --- |
| The screen | [`features/documents/documents.ts`](../../enterprise-gpt-ui/src/app/features/documents/documents.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/documents/documents.html), [`.scss`](../../enterprise-gpt-ui/src/app/features/documents/documents.scss) |
| The store | [`features/documents/document-library-store.ts`](../../enterprise-gpt-ui/src/app/features/documents/document-library-store.ts) |
| One row | [`features/documents/document-row.ts`](../../enterprise-gpt-ui/src/app/features/documents/document-row.ts) |
| The URL contract | [`features/documents/documents-route.ts`](../../enterprise-gpt-ui/src/app/features/documents/documents-route.ts) |
| The a11y audit | [`features/documents/documents.a11y.spec.ts`](../../enterprise-gpt-ui/src/app/features/documents/documents.a11y.spec.ts), [`src/testing/a11y-routes.ts`](../../enterprise-gpt-ui/src/testing/a11y-routes.ts) |
| The API routes | [`Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs), [`DocumentEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/DocumentEndpoints.cs) |
| The listing queries and the re-check | [`Enterprise.Gpt.Service/ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs), [`DocumentService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/DocumentService.cs) |
| The DTOs and mapper | [`Enterprise.Gpt.Dto/ConversationDocumentDto.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/ConversationDocumentDto.cs), [`UserDocumentDto.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/UserDocumentDto.cs), [`Enterprise.Gpt.Service/Mappers/ConversationDocumentMapper.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Mappers/ConversationDocumentMapper.cs) |
| The `?type=` token table | [`Enterprise.Gpt.Service/Filtering/DocumentTypeNames.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Filtering/DocumentTypeNames.cs) |
| The badge lookup | [`features/documents/document-row.ts`](../../enterprise-gpt-ui/src/app/features/documents/document-row.ts) (`BADGE_SOURCES`), [`shared/badge/source-badge/source-badge.ts`](../../enterprise-gpt-ui/src/app/shared/badge/source-badge/source-badge.ts) |
| The migration | [`Repository/Migrations/20260819042820_AddConversationDocumentUserIndex.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260819042820_AddConversationDocumentUserIndex.cs) |
| Related reference | [File Attachments](file-attachments.md), [Projects](projects.md), [Conversation Library](conversation-library.md), [Frontend Foundation](frontend-foundation.md), [Accessibility Audit](accessibility-audit.md), [Generated Files](../file-agent/generated-files.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md) |
