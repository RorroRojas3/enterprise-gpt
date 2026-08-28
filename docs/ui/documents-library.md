# Documents Library

The `/documents` screen and the API underneath it: every document the signed-in user has uploaded, across every conversation, on one screen — a drained set filtered client-side, grouped by conversation by default, with the term in the URL and downloads on the click-only signed-link rules the attachment chips already follow. EP-10's first three stories landed together on 2026-08-19: the backend enabler (US-1001) and the screen it releases (US-1002, US-1003).

Audience: a developer adding US-1004/US-1005 (the source badge and the assistant-created filter, which this screen deliberately stubs), building the next drained-and-filtered listing, or debugging a count that disagrees with the rows, a group that will not collapse, or a filter that seems to drop documents. Read [File Attachments](file-attachments.md) first for the upload pipeline and the download rules this screen inherits, [Projects §4.1](projects.md#41-it-drains-rather-than-pages) for `ProjectListStore` — the drain this store copies — and [Frontend Foundation §5.4](frontend-foundation.md#54-withclientquerysource-config) for `withClientQuery`. Bare `§` references below are to sections of _this_ page.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference. The server side of upload and download stays where it was: [Document Upload and Ingestion](../documents/upload-workflow.md) and [Document Download](../documents/download-workflow.md).

## 1. Overview

| Story | What it delivers |
| --- | --- |
| **US-1001** `[enabler]` | Two listing routes: `GET api/conversations/{id}/documents` (the B2 contract) and the cross-conversation `GET api/documents` the library actually reads — plus the widened `ConversationDocumentDto`, the `(UserId, DateDeactivated)` index, and the ingestion re-check that closed a pre-existing orphan race the new listing would have made visible |
| **US-1002** | Frame `4j`'s screen: the lazy `/documents` route, the sidebar entry, `DocumentLibraryStore`'s drain, the custom rows with glyph/size/date/badge/conversation-link, US-804's click-only downloads, the error panel and the never-uploaded empty state |
| **US-1003** | The client-side name filter with the term in the URL, and the **grouped-by-conversation default** with collapsible headers, recorded in the URL only as its deviation (`?view=flat`) |

Seven decisions shape everything here, and each looks removable until you know what it prevents:

1. **One cross-conversation route, not a fan-out.** The per-conversation route alone would force one request per conversation over an unbounded library — the PRD's §9 open question, answered by shipping both routes (§3.3).
2. **The store drains, then filters client-side** — regime A's loading with no sort offered, a combination none of the other listings has (§4.3).
3. **A mid-drain failure clears the dead pages** rather than keeping them, the opposite of the reference drain's call — because here a partial set is never a complete previous result (§4.2).
4. **Grouped is the default, and the URL records only the deviation.** A bare `/documents` is the grouped view frame `4j` draws checked; only `?view=flat` is ever written (§5).
5. **The rows are custom, not `DataTable`.** The frame draws no header row, and the table has no grouping for US-1003 to grow into (§6.2).
6. **The source badge is hard-coded `Uploaded`.** A discriminator now exists in the data model (`ConversationDocumentTypes`, added by the File Agent — [`generated-files.md`](../file-agent/generated-files.md) §1), but both routes behind this screen filter to `Uploaded` explicitly (§3.1, §3.2), so every row this screen ever receives already is one. The badge is stubbed at one call site with a comment naming US-1004, the story that would widen the query and read the flag rather than the one that added the column (§6.2, §9).
7. **Ingestion re-checks its parent after persisting.** A conversation deleted mid-ingest has already run its document cascade, which never re-runs — so the just-inserted rows deactivate themselves to match (§3.4).

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
| The index | [`Repository/Migrations/20260819042820_AddConversationDocumentUserIndex.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260819042820_AddConversationDocumentUserIndex.cs) |
| Fixtures for specs | [`src/testing/documents.ts`](../../enterprise-gpt-ui/src/testing/documents.ts) |

## 2. Quick start

### 2.1 Reading the library

The store is provided by the `Documents` component, so it exists only while the route does:

```ts
protected readonly library = inject(DocumentLibraryStore);

//   library.results()             → the filtered rows, in the server's newest-first order
//   library.groups()              → DocumentGroup[] over those same rows, first-seen order
//   library.query() / .hasQuery() → the committed filter term
//   library.isFirstLoad()         → pending with nothing on screen — a skeleton is honest
//   library.isEmpty()             → loaded, and this user has uploaded nothing at all
//   library.hasNoMatches()        → loaded, documents exist, nothing matched, a term is why
//   library.countSummary()        → "12 documents" / "Showing 3 of 12 documents"
//   library.truncated()           → the drain stopped at the 500-item ceiling
//   library.ceilingNotice()       → "Showing the 500 most recent of 812 documents."
//   library.resultsPartialReason()→ the filtered-count qualifier over a truncated drain
//   library.error()               → AppError | null
//
//   library.load();  library.bindQuery(nameSignal);  library.retry();
```

There is no `search(term)` and no `setView(mode)` — **the only way to change the query is to navigate** (§5). `load()` and `bindQuery()` are each called once, from the component's constructor; `bindQuery` takes the **signal**, not its value, which is what makes a deep link filter on its very first render.

### 2.2 The two URL parameters

| Parameter | Written as | History | Effect |
| --- | --- | --- | --- |
| `?name=` | The trimmed term, or the key **removed** when empty | Replaces while refined; pushes when added or removed (`shouldReplaceHistory`, shared with the conversations library) | Client-side: `setQuery` narrows rows the store already holds. **No request is issued** |
| `?view=` | Exactly `flat`, or the key **removed** | Always pushes — on/off is the add-or-remove transition | Flat listing with the conversation link column; anything else, including absence, reads as **grouped** |

Both inputs are defensively transformed: the router writes `undefined` when a key leaves the URL, `name` trims and falls back to `''`, and `view` reads `value === 'flat'` — so a hand-edited `?view=list` cannot put the switch and the listing out of step, the same rule the conversations library applies to `?favorites=yes`.

## 3. The two endpoints

### 3.1 `GET api/conversations/{id}/documents`

The B2 contract: one conversation's active documents, as a **bare `List<ConversationDocumentDto>`** — no envelope, no paging — ordered `DateCreated` descending, the exact mirror of the project-documents route.

| Case | Answer |
| --- | --- |
| Owner, documents exist | `200` with the list, newest first |
| Owner, no documents | `200` with `[]` — **never** 404 |
| Unknown, deactivated, or another user's conversation | `404`, deliberately indistinguishable, so the route cannot probe for conversation ids |

`ConversationDocumentDto` was widened **in place** — it had never left the server — with `extension` (lower-cased, leading dot included), `mimeType`, `size` (bytes) and `dateCreated` (when ingestion finished), matching the field set `ProjectDocumentDto` already provides so both listings key the same way. It also emits `documentId` as a read-only alias of `id`, the shape the upload-status response uses. The mapping moved to `Service/Mappers/ConversationDocumentMapper` beside its project sibling — materialized `MapToConversationDocumentDto` plus `Expression` projections, retiring the entity-file `ChatDocumentExtensions` — and every projection deliberately excludes chunk data: a listing must never drag the embedding column across the wire.

**Uploads only, since the File Agent shipped its `Uploaded`/`Generated` discriminator** ([`generated-files.md`](../file-agent/generated-files.md) §1). The query filters `Type == Uploaded` explicitly: every surface this route feeds — the composer's chips, the files panel — is about what the user brought to the conversation, and a file the assistant made reaches them through the chip on the message that made it instead (§9 below).

The rebuilt client does not call this route yet; the library reads §3.2's. It exists because US-1001's contract requires it, mirroring what a conversation-scoped surface will consume.

### 3.2 `GET api/documents?skip&take`

The library's route: every active conversation document **the caller owns**, across all conversations, in the standard `PaginatedResponseDto<UserDocumentDto>` envelope.

- **`UserDocumentDto : ConversationDocumentDto` adds `conversationName`**, projected through the `Conversation` navigation — the whole reason the screen needs no per-conversation name lookup.
- **Ordered `DateCreated` descending with `Id` descending as the tie-break.** `DateCreated` alone is not a total order, so two documents ingested in the same tick could straddle a page boundary and be duplicated or skipped.
- **`skip` is clamped to ≥ 0 and `take` to 1..100** — clamped rather than rejected, because the arguments come straight off the query string. The endpoint defaults `take` to 20 when omitted; the client always sends 100. A malformed value is a binding failure, so the route declares `.ProducesProblem(400)` and not `.ProducesValidationProblem()` — there is no errors dictionary to carry.
- **Owner-scoped, deliberately not permission-gated.** `Upload File` gates *adding* documents; reading back what you own follows the download routes' rule, where owning the parent is the authorization.
- **No conversation-active predicate in the query.** `DeactivateConversationAsync` and its bulk sibling deactivate a conversation's documents in the same operation, and ingestion re-checks the conversation after persisting (§3.4) — so an active document implies an active conversation, and the filter is `UserId` + `DateDeactivated` alone. That pair is exactly what migration `AddConversationDocumentUserIndex` indexes, whose `Up` also drops the conventional single-column `UserId` index the composite supersedes.
- **Uploads only, since the File Agent shipped.** `ConversationDocument.Type` now distinguishes `Uploaded` from `Generated` ([`generated-files.md`](../file-agent/generated-files.md) §1), and this query filters to `Uploaded` explicitly, for the same reason the per-conversation route (§3.1) does: every row here is captioned as an upload, and a generated file listed among them would be labelled as something it is not.

### 3.3 Why one cross-conversation route, and not a fan-out

US-1001's fourth acceptance criterion demanded the decision be made and recorded rather than defaulted into: the per-conversation route alone forces an N+1 over the conversation list — acceptable for a grouped view of one conversation, not for an unbounded library. The answer (product owner + backend engineer, 2026-08-19, recorded in [PRD §9](../prd/enterprise-ui-rebuild.md#9-assumptions--open-questions)): **both routes ship, and the screen never fans out.** Each item carries its conversation's id *and name*, the client drains pages at `take=100` to the store's 500-item ceiling, and the envelope's `totalCount` is what keeps the on-screen count honest when the drain stops short.

### 3.4 The ingestion re-check

Review of US-1001 found a real defect the new listing would have surfaced: ingestion spans seconds to minutes, and a conversation deleted meanwhile has already run its document-deactivation cascade — **before the document row existed**. The cascade never re-runs, so the row landed active under a deactivated parent: an orphan no query expected, which the library would have listed and whose download would then 404.

Both `Create*DocumentAsync` ingestion paths (conversation *and* project — the project side had the same window) now re-check the parent's active state **after** `SaveChangesAsync`, and when it has gone:

1. Deactivate the just-inserted chunks, then the document, **set-based** via `ExecuteUpdateAsync` — no rowversion involved, so if the cascade did catch the rows between the insert and the re-check, this is an idempotent no-op rather than a `DbUpdateConcurrencyException` from a tracked save.
2. Throw `NotFoundException`, failing the job with the same not-found message an upload into an already-deleted conversation gets.

This shrinks the window to the cascade's own statement gap. It is also load-bearing for §3.2's query shape: it is half of what makes "an active document implies an active conversation" true.

## 4. The store: a drain with a client-side filter

[`DocumentLibraryStore`](../../enterprise-gpt-ui/src/app/features/documents/document-library-store.ts) is provided on the component, created by the route that shows it and destroyed with it. It holds **zero seeded records by construction** — a US-1002 criterion: nothing is fetched until the screen calls `load()`, and no placeholder row ever ships in state.

### 4.1 The drain

`ProjectListStore`'s drain, minus the term — this route takes no filter, so a drain is only ever superseded by a retry or a sign-out — with the same load-bearing details:

- **`take=100` to a 500-item ceiling** (`DOCUMENT_PAGE_SIZE`, `DOCUMENT_LOAD_CEILING` — the PRD's regime-A figures, five requests at most). 100 is the server's own clamp ceiling, so the drain costs the fewest possible round trips.
- **`expand`, not a self-referential `concat`** — each iteration completes and is dropped, so a five-page drain does not retain five nested subscribers.
- **`_querySuperseded$` cancels the whole chain**, not just the request in flight: `switchMap` cancels one request, but the drain is a *sequence*, and without the subject a page fetched by the old drain lands after the retry's `setAllEntities` and appends rows twice.
- **`truncated` is written where the drain stops, never in a `finalize`** — a `finalize` also runs on the cancellation a retry or sign-out causes, and would stamp a partial load as a truncated one.
- **`takeUntil(signedOut$)` on the request**, because `withResetOnSignOut` clears state and does not cancel work; and `withResetOnSignOut` composed **last**, as the feature requires.

### 4.2 A mid-drain failure clears the dead pages

The one place this store deliberately does **not** copy its reference: on a failure partway through the drain, the error arm runs `setAllEntities([])` + `resetPagination()` + `setError(...)`, so the panel replaces the listing and **Retry shows the skeleton** rather than a half-drained set. A review finding — the reference drain keeps its partial pages, but there they can be a *complete previous result*; here they never are, and `setPending` alone would have re-shown the condemned rows for Retry's first round trip while the event handlers went on patching them. A half-drained set counted or grouped as if it were whole is exactly the silently-partial listing regime A exists to avoid — the same reasoning that makes `ProjectListStore` throw its pages away and `ProjectLookupStore` keep them ([Projects §9.1](projects.md#91-the-lookup-behind-it-and-why-a-failed-drain-keeps-its-pages)).

### 4.3 `withClientQuery`, and where this sits in the regime taxonomy

`withClientQuery` is composed over `entities` through `withFeature` (US-1003), and the configuration is where the decisions live:

```ts
withClientQuery(entities, {
  searchableText: (document) => document.name,      // the name and nothing else
  comparators: {},                                  // no sort story for documents
  isAuthoritative: computed(() => isFulfilled() && isFullyLoaded()),
  incompleteSetReason: `Only the ${DOCUMENT_LOAD_CEILING} most recent documents have loaded.`,
})
```

- **`searchableText` is the file name only** — never the conversation name the rows also carry. That is what the empty state's "Filters cover file names only." promises, and it is why a conversation rename never evicts a row (§4.4).
- **`comparators` is empty.** No sort key is offered and `results` stays in server order — the first `withClientQuery` host with no sort at all.
- **`isAuthoritative` gates on both** `isFulfilled()` and `isFullyLoaded()`, because `isFullyLoaded` reads `true` before the first fetch ([Frontend Foundation §5.2](frontend-foundation.md#52-withoffsetpaginationtake--100)).

Against [the four regimes](frontend-foundation.md#54-withclientquerysource-config) this screen is a hybrid the table does not list: **regime A's loading** (drain at `take=100` to the 500 ceiling) with **regime B's ordering** (server order, no sort control) and a client-side filter qualified through `incompleteSetReason` when the drain truncates. The filter is not suppressed over a truncated set — a filter over part of the data is still useful where a sort over it is simply wrong — it is *qualified* (§6.4).

The grouped view derives from the same `results`, so one term narrows both shapes (§6.3).

### 4.4 Staying in step: `conversationEvents`, server-confirmed facts only

- **`updated`** patches `conversationName` on every row of the affected conversation — a rename has to keep the link column and the group headers honest. It never evicts: the filter is on document names only, so a rename cannot change what matches.
- **`deleted`** removes the conversation's rows and shifts `skip` *and* `totalCount` down together (`shiftCounters`) — deleting a conversation takes its documents with it server-side, so their rows go here too rather than 404-ing on the next download, and both counters move so the count and `hasMore` stay honest.

### 4.5 `bindQuery` is a `signalMethod`, not an `rxMethod`

A deliberate departure from every list store before it: the filter narrows rows the store already holds, so there is **no request to race** and nothing for `switchMap` to cancel. `rxMethod` earns its place where responses can arrive out of order; here it would be ceremony.

## 5. The URL contract

The conversations library's loop, verbatim ([Conversation Library §3](conversation-library.md#3-the-url-is-the-source-of-truth)): `withComponentInputBinding()` binds `?name=` and `?view=` to `input()`s, `_applyQuery` is the **only** writer and writes to the router with `queryParamsHandling: 'merge'` (so the term and the view survive each other), a cleared parameter removes its key (`null`), and the loop cannot feed itself because a navigation re-seeds `SearchInput`'s `linkedSignal`, whose debounce finds the text unchanged and emits nothing. `shouldReplaceHistory` is re-exported from `core/navigation/search-history` — refining a term replaces, adding or removing one pushes, and the view toggle always pushes because on/off *is* the add-or-remove transition.

The one structural difference from the conversations library: typing navigates but **never issues a request** — the URL drives `setQuery`, not a fetch. The consequences are the same ones: a filtered, grouped-or-flat view is shareable, the back button undoes the filter *and* the view toggle, and a deep link filters on the first render because `bindQuery` was handed the signal.

**Grouped is the default, so the URL records only the deviation.** Frame `4j` draws the switch checked; a bare `/documents` is the grouped view and only `?view=flat` is ever written. Encoding the default (`?view=grouped`) would make two URLs mean one state, and would break every bookmark if the default ever changed.

## 6. The screen

Frame `4j`: a title, a toolbar, and a card of rows — grouped under conversation headers by default, flat with a conversation link column behind the toggle. Frame `4k` covers the error and empty states.

### 6.1 The toolbar

Three things, left to right: the filter field, the **Group by conversation** switch, and the count.

- **The field is labelled "Filter documents by name" — "Filter", not "Search".** The criterion's own wording, and honest about the mechanics: it narrows what is already here rather than asking the server.
- **The count is `aria-hidden`, mirrored by one visually-hidden `role="status"` element** that exists from first render — a live region inserted together with its content is not reliably read, and two independently derived counts would eventually disagree. It is withheld before the first response, where "No documents" would be a claim rather than a count.
- **`countSummary` reads the envelope's `totalCount`, not the rows held.** Unfiltered it states the total ("12 documents") — which is what keeps it honest under the ceiling, whose notice already qualifies the shortfall. Filtered it pairs the visible figure with that same total ("Showing 3 of 12 documents"), because a client-side filter changes what the listing shows without changing what the server holds.
- **The whole toolbar is withheld under the error panel** — a divergence from the conversations library, whose field stays up beside its panel. That field drives a *server* query a retry can re-run; this one narrows rows the store holds client-side, and over a failed load there is nothing to narrow. Frame `4k` draws the error card alone.

### 6.2 The rows: custom, not `DataTable`

`app-document-row` is the `ProjectFiles` anatomy — glyph, name, size, date, then the row's controls — and **deliberately not `DataTable`**: the table always renders the header row frame `4j` does not draw, and it has no grouping for US-1003 to grow into.

- **The glyph, the size and the date reuse the existing formatters** — `fileGlyph` (the attachment chip's table, tone included), `formatBytes` (the same digits the chip and the too-large toast show) and `formatShortDate`.
- **The source badge is hard-coded `<app-source-badge source="uploaded" />`.** The data model can now tell the two apart, but neither route behind this screen sends a generated row (§3.1, §3.2); US-1004 is the story that would widen the query, put assistant-generated files on the wire, and make this data-driven, and the call site says so in a comment (§9).
- **The conversation cell is an input, because the two views disagree about it.** The flat view keeps it — the link column is US-1002's criterion — and grouped rows pass `showConversation` `false`, because the group header already names and links the conversation. A host class drops the cell's grid track with it, so the badge sits flush against the download control rather than leaving a phantom column.
- **Download is US-804's flow, unchanged**: the root `DocumentDownloadStore`, `{ kind: 'conversation', id }`, the signed link fetched **on click only** — it is a five-minute bearer credential, and a list that prefetched one per row would mint a credential for every file merely looked at ([File Attachments §6](file-attachments.md#6-download-us-804)). `isRowPending` gives the row its own busy state without disabling the other thirty-nine.
- **The download control is a real `<button>` with an accessible name** ("Download {file}"), not the prototype's clickable `<i>` — the same correction the project files panel makes.

### 6.3 The grouped view

`groups` is computed over the **filtered** results, so the same term narrows both view modes: a group whose documents all fail the filter disappears, and its count labels what is actually visible under it rather than what the conversation holds.

- **First-seen order.** The server lists documents newest first, so groups order by their newest visible document — exactly where a refetch would put them, with no second ordering rule to maintain.
- **Each `--surface-2` header carries** a chevron button, a `bi-chat-left` glyph, the conversation's name as a link into the chat, and a `'1 file'` / `'N files'` count.
- **The chevron carries `aria-expanded`, and `aria-controls` only while its target is rendered.** A collapsed group's list leaves the DOM, and an id reference to nothing is an axe violation, not a hint — so the attribute is nulled while the body is out.
- **Collapse state is a screen-local signal, not store state.** Which groups are folded is view state that should survive a filter change but die with the route, exactly like a scroll position. It is held as *collapsed* ids rather than expanded ones, so every group starts expanded — including groups a filter reveals later — and a filter can never hide its own matches behind closed groups. Each write replaces the `Set`, because reference equality is how signals decide anything changed.

### 6.4 The states, in precedence order

| State | Renders | Action |
| --- | --- | --- |
| `error()` | Frame `4k`'s `ErrorPanel` — "Documents couldn't load", the `traceId`, Retry — **instead of everything**, toolbar included | **Retry** re-runs the drain from the first page |
| `isFirstLoad()` | Six skeleton rows, `aria-busy` | — |
| `isEmpty()` | "Nothing uploaded yet" / "Attach files in any conversation and they'll appear here.", the ridgeline motif | **None** — uploading happens in a conversation's composer, and a button here could only restate the sentence below it |
| `hasNoMatches()` | Frame `4b`'s shape: a heading naming the committed term, the scope line "Filters cover file names only." | **Clear filter**, which also returns focus to the field |
| Otherwise | The card of rows, grouped or flat | — |

Three orderings in that table are decisions:

- **Never-uploaded wins over no-matches.** `isEmpty` is checked first, so a zero-document deep link that carries `?name=` reads "Nothing uploaded yet" — the truth, since there is nothing a cleared filter would reveal. With the filter client-side, the two states are genuinely tellable apart, which the conversations library (whose filter is a server parameter) cannot always claim.
- **Over a truncated drain, the scope line yields to the partial-set qualifier.** The no-matches message renders `resultsPartialReason() ?? 'Filters cover file names only.'` — an unqualified "no matches" over 500 of 812 documents would claim rows the store never loaded.
- **One qualifier below the card, never both.** Unfiltered, `ceilingNotice` states what the card is showing ("Showing the 500 most recent of 812 documents."); filtered, `resultsPartialReason` qualifies the *result* instead, because a count computed over part of the data is the claim that needs the caveat. Rendering both would say the same thing twice in different words.

## 7. Accessibility

`/documents` joined `AXE_ROUTES` **in the same change as the route** — the rule [the audit's coverage guard](accessibility-audit.md#52-the-coverage-guard) enforces — and `a11y-coverage.spec.ts`'s absence assertion was deleted exactly as that assertion's own comment demanded. `documents.a11y.spec.ts` adds **4 browser audits** (both themes × both view modes, because the two render different anatomies: group headers with chevrons and links on one side, the conversation link column on the other), taking the suite to **36**. The screen is audited with rows from two conversations rather than empty, because an empty listing has no glyphs, badges, links or download controls in it — which is most of the surface worth auditing.

The per-control semantics are stated where they live: the collapse chevrons in §6.3 (`aria-expanded`, `aria-controls` nulled while collapsed, an `aria-label` naming the group), the count's single `role="status"` mirror in §6.1, the named download buttons in §6.2. Both listings are `role="list"` with an `aria-label`, restated explicitly because `list-style: none` strips list semantics in Safari.

## 8. Route, nav entry and bundle

`/documents` is a lazy `loadComponent` under the shell, like every other shell child: the screen and its store are code a reader who only ever opens `/chat` never runs. `DOCUMENTS_ROUTE` lives beside the other route constants, and the sidebar's **Documents** entry (`bi-file-earmark-text`, between Projects and Admin) appears now that there is a route to point it at — the sidebar renders only entries whose route exists ([Shell and Navigation §2.3](shell-and-navigation.md#23-adding-a-nav-entry)). Every glyph the screen needs was already in the icon manifest.

|                 | Initial raw   | Initial transfer | Budget (warn / error) |
| --------------- | ------------- | ---------------- | --------------------- |
| Before EP-10    | 669.93 kB     | 168.90 kB        | 675 kB / 720 kB       |
| **After US-1003** | **670.68 kB** | **169.07 kB**  | 675 kB / 720 kB       |

+0.75 kB across the two frontend stories, and all of it is the route record and `DOCUMENTS_ROUTE` — the screen, the store and the row ride the feature's own lazy chunk. No re-baseline and no threshold change.

## 9. Deliberately not here

Recorded in the [build order](../prd/enterprise-ui-rebuild-build-order.md)'s interim-behaviours table where a story owns the gap:

- **Frame `4i`'s pre-enabler unavailable panel was never built.** US-1002's first criterion covered the world where US-1001 had not landed; US-1001 landed first, so no client ever shipped without the endpoint behind this screen, and the skipped criterion is recorded in the store's class doc (product-owner decision, 2026-08-19).
- **Every row wears the `Uploaded` badge, and the assistant-created filter is hidden** — not shown-and-disabled. The data model can tell a generated file from an uploaded one (§3.1, §3.2), but neither route behind this screen sends one yet; US-1004 (B7) is the story that would widen the query and make the badge data-driven, and US-1005 then ships the filter.
- **The filter covers file names only** — the searchable text is one field, and the empty state says so. Widening it is a product decision, not an oversight to fix in passing.
- **No sort control.** No sort story exists for documents; the server's newest-first order is the only one, `comparators` is empty, and `results` stays in server order (§4.3).
- **No per-row detach.** There is **no `DELETE` route for a conversation document** — only project documents have one — so once ingested, a document belongs to its conversation and this screen offers download only. The same gap shapes the attachment chip's Remove/Dismiss split ([File Attachments §4.1](file-attachments.md#41-remove-means-two-different-things)).

## 10. Testing

**35 specs across the screen's two files** — 20 in `documents.spec.ts` (the URL contract and history rule, the view toggle, the grouped view and its collapse semantics, the three non-error states and the count) and 15 in `document-library-store.spec.ts` (the drain, the ceiling, the failure arm clearing its pages, the two event handlers, the filter and grouping) — inside the suite's **1785** jsdom tests, plus the 4 browser audits (§7) in the a11y suite's 36. The shell spec asserts the new nav entry, and `a11y-coverage.spec.ts` now simply covers `/documents` like any other route.

On the API side, US-1001 added **16 unit and 12 integration tests** — the two listings' ordering, clamping, ownership 404s, empty-list 200s and the ingestion re-check on both paths — taking those suites to **1193 unit / 278 integration**.

```bash
# from enterprise-gpt-ui/
npm test              # Vitest, single run — 1785 specs
npm run test:a11y     # 36 audits in headless Chromium

# from enterprise-gpt-api/
dotnet test --filter "Category!=Integration"   # 1193 unit tests
dotnet test                                    # + 278 integration tests; Docker required
```

> **Verification status.** As with every epic before it, the UI has not been exercised against a live API: the gates are green and every behaviour is asserted against `HttpTestingController` and `RouterTestingHarness`. The two endpoints *are* exercised end-to-end by the integration suite against real SQL Server 2025.

## 11. Troubleshooting

| Symptom | Cause |
| --- | --- |
| The listing shows fewer documents than the count claims | By design at the ceiling: the drain stops at 500 and `countSummary` reads the envelope's total. The ceiling notice below the card states the shortfall (§6.4) |
| Typing in the filter issues requests | Something replaced `bindQuery`'s `signalMethod` with a fetch. The filter is client-side; typing navigates and `setQuery` narrows rows already held (§4.5, §5) |
| A deep link with `?name=` flashes the unfiltered list first | `bindQuery` was handed `this.name()` instead of `this.name` — it needs the signal to filter on the first render (§2.1) |
| `/documents?view=grouped` renders flat, or the switch disagrees with the listing | It cannot, unless the transform changed: anything other than `flat` reads as grouped, and only `flat` is ever written (§2.2) |
| Retry briefly shows stale rows before the skeleton | The failure arm stopped clearing its pages. `setAllEntities([])` + `resetPagination()` belong with `setError` — a half-drained set is never a previous complete result here (§4.2) |
| Rows appended twice after a retry | The drain lost `_querySuperseded$`. `switchMap` cancels only the request in flight, and the drain is a chain (§4.1) |
| A load that was merely cancelled shows the ceiling notice | `truncated` moved into a `finalize`, which also runs on cancellation. It is written only where the drain genuinely stops short (§4.1) |
| A renamed conversation still shows its old name in headers or the link column | The `conversationEvents.updated` handler came off, or the screen cached the name instead of reading the row (§4.4) |
| Rows of a deleted conversation linger and their downloads 404 | The `deleted` handler came off — or it removed rows without `shiftCounters`, leaving the count claiming rows that are gone (§4.4) |
| axe reports an `aria-controls` violation on a group header | The attribute is being rendered while the group is collapsed. It must be nulled when the body leaves the DOM (§6.3) |
| A filter appears to hide matching documents inside closed groups | Collapse state was inverted to track *expanded* ids, so new groups start collapsed. It tracks collapsed ids precisely so every group — including ones a filter reveals — starts open (§6.3) |
| "No documents match" over a truncated drain, with no qualifier | The empty state stopped preferring `resultsPartialReason` over the scope line (§6.4) |
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
| The migration | [`Repository/Migrations/20260819042820_AddConversationDocumentUserIndex.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260819042820_AddConversationDocumentUserIndex.cs) |
| Related reference | [File Attachments](file-attachments.md), [Projects](projects.md), [Conversation Library](conversation-library.md), [Frontend Foundation](frontend-foundation.md), [Accessibility Audit](accessibility-audit.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md) |
