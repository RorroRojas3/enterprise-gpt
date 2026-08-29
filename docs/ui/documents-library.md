# Documents Library

The `/documents` screen and the API underneath it: every document the signed-in user holds across every conversation — uploaded by them or produced by the assistant — on one screen. A drained set, narrowed at the server to one origin or the other and at the client to a name term, grouped by conversation by default, with both filters in the URL and downloads on the click-only signed-link rules the attachment chips already follow. EP-10's first three stories landed together on 2026-08-19: the backend enabler (US-1001) and the screen it releases (US-1002, US-1003). US-1004 and US-1005 closed the epic on 2026-08-29, crossing the `Uploaded`/`Generated` discriminator the File Agent had already put on the schema ([`generated-files.md`](../file-agent/generated-files.md) §1) onto this screen.

Audience: a developer building the next drained-and-filtered listing, adding a third origin or a second server-side filter to this one, or debugging a count that disagrees with the rows, a group that will not collapse, or a filter — either one — that seems to drop documents. Read [File Attachments](file-attachments.md) first for the upload pipeline and the download rules this screen inherits, [Projects §4.1](projects.md#41-it-drains-rather-than-pages) for `ProjectListStore` — the drain this store copies — and [Frontend Foundation §5.4](frontend-foundation.md#54-withclientquerysource-config) for `withClientQuery`. Bare `§` references below are to sections of _this_ page.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference. The server side of upload and download stays where it was: [Document Upload and Ingestion](../documents/upload-workflow.md) and [Document Download](../documents/download-workflow.md).

## 1. Overview

| Story | What it delivers |
| --- | --- |
| **US-1001** `[enabler]` | Two listing routes: `GET api/conversations/{id}/documents` (the B2 contract) and the cross-conversation `GET api/documents` the library actually reads — plus the widened `ConversationDocumentDto`, the `(UserId, DateDeactivated)` index, and the ingestion re-check that closed a pre-existing orphan race the new listing would have made visible |
| **US-1002** | Frame `4j`'s screen: the lazy `/documents` route, the sidebar entry, `DocumentLibraryStore`'s drain, the custom rows with glyph/size/date/badge/conversation-link, US-804's click-only downloads, the error panel and the never-uploaded empty state |
| **US-1003** | The client-side name filter with the term in the URL, and the **grouped-by-conversation default** with collapsible headers, recorded in the URL only as its deviation (`?view=flat`) |
| **US-1004** `[enabler]` | `type` on `ConversationDocumentDto` (`"Uploaded"`/`"Generated"`, member-name serialized), `?type=uploaded\|generated` on `GET api/documents`, and the removal of that route's hard-coded `Uploaded`-only predicate — the crossing that puts the File Agent's discriminator ([`generated-files.md`](../file-agent/generated-files.md) §1) on the wire |
| **US-1005** | The data-driven source badge and the **Generated only** toolbar switch, narrowing the listing at the server with the deviation recorded as `?source=generated` |

Nine decisions shape everything here, and each looks removable until you know what it prevents:

1. **One cross-conversation route, not a fan-out.** The per-conversation route alone would force one request per conversation over an unbounded library — the PRD's §9 open question, answered by shipping both routes (§3.3).
2. **The store drains, then filters client-side on name — but narrows to an origin at the server.** Regime A's loading with no sort offered, plus one served filter the client-side one deliberately is not (§4.3, §4.6).
3. **A mid-drain failure clears the dead pages** rather than keeping them, the opposite of the reference drain's call — because here a partial set is never a complete previous result (§4.2).
4. **Grouped is the default, and the URL records only the deviation.** A bare `/documents` is the grouped view frame `4j` draws checked; only `?view=flat` is ever written (§5).
5. **The rows are custom, not `DataTable`.** The frame draws no header row, and the table has no grouping for US-1003 to grow into (§6.2).
6. **The source badge is data-driven, since US-1004/US-1005.** `ConversationDocumentTypes` ([`generated-files.md`](../file-agent/generated-files.md) §1) now reaches the client as `UserDocumentDto.type`, and `app-document-row` reads it through a small lookup table rather than a constant (§6.2).
7. **The assistant-created filter narrows at the server, unlike the name term beside it.** The drain would otherwise decide the answer at the 500-item ceiling instead of the query doing it (§4.6).
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

//   library.results()             → the filtered rows, in the server's newest-first order
//   library.groups()              → DocumentGroup[] over those same rows, first-seen order
//   library.query() / .hasQuery() → the committed name term
//   library.generatedOnly()       → the committed origin filter — true once ?source=generated resolves
//   library.isFirstLoad()         → pending with nothing on screen — a skeleton is honest
//   library.isEmpty()             → loaded, and the listing holds nothing under the current filter
//   library.hasNoMatches()        → loaded, documents exist, nothing matched, a term is why
//   library.countSummary()        → "12 documents" / "Showing 3 of 12 documents" / "5 generated documents"
//   library.truncated()           → the drain stopped at the 500-item ceiling
//   library.ceilingNotice()       → "Showing the 500 most recent of 812 documents."
//   library.resultsPartialReason()→ the filtered-count qualifier over a truncated drain
//   library.error()               → AppError | null
//
//   library.bindSource(sourceSignal);  library.bindQuery(nameSignal);  library.retry();
```

There is no `search(term)` and no `setView(mode)` — **the only way to change the query is to navigate** (§5). `bindSource()` and `bindQuery()` are each called once, from the component's constructor, and each takes the **signal**, not its value, which is what makes a deep link filter on its very first render. `bindSource`'s first value *is* the initial load — there is no separate `load()` call any more; binding the origin starts the drain (§4.6).

### 2.2 The three URL parameters

| Parameter | Written as | History | Effect |
| --- | --- | --- | --- |
| `?name=` | The trimmed term, or the key **removed** when empty | Replaces while refined; pushes when added or removed (`shouldReplaceHistory`, shared with the conversations library) | Client-side: `setQuery` narrows rows the store already holds. **No request is issued** |
| `?view=` | Exactly `flat`, or the key **removed** | Always pushes — on/off is the add-or-remove transition | Flat listing with the conversation link column; anything else, including absence, reads as **grouped** |
| `?source=` | Exactly `generated`, or the key **removed** | Always pushes, on the same on/off rule as `?view=` | Server-side: re-drains with `?type=generated`; anything else, including absence, reads as **every origin** and drains with no `type` at all |

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

### 3.2 `GET api/documents?skip&take&type`

The library's route: every active conversation document **the caller owns**, across all conversations, in the standard `PaginatedResponseDto<UserDocumentDto>` envelope.

- **`UserDocumentDto : ConversationDocumentDto` adds `conversationName`**, projected through the `Conversation` navigation — the whole reason the screen needs no per-conversation name lookup.
- **Ordered `DateCreated` descending with `Id` descending as the tie-break.** `DateCreated` alone is not a total order, so two documents ingested in the same tick could straddle a page boundary and be duplicated or skipped.
- **`skip` is clamped to ≥ 0 and `take` to 1..100** — clamped rather than rejected, because the arguments come straight off the query string. The endpoint defaults `take` to 20 when omitted; the client always sends 100.
- **Owner-scoped, deliberately not permission-gated.** `Upload File` gates *adding* documents; reading back what you own follows the download routes' rule, where owning the parent is the authorization.
- **No conversation-active predicate in the query.** `DeactivateConversationAsync` and its bulk sibling deactivate a conversation's documents in the same operation, and ingestion re-checks the conversation after persisting (§3.4) — so an active document implies an active conversation, and the filter is `UserId` + `DateDeactivated` alone. That pair is exactly what migration `AddConversationDocumentUserIndex` indexes, whose `Up` also drops the conventional single-column `UserId` index the composite supersedes.
- **Both origins by default, since US-1004/US-1005.** The route's hard-coded `Type == Uploaded` predicate is gone; `GET api/documents` now lists uploaded and generated documents alike unless narrowed by `?type=` (§3.2.1). No migration and no index change went with the widening — the filter is a residual predicate on the same `(UserId, DateDeactivated)` seek, applied only when a caller asks for one origin.

#### 3.2.1 The `?type=` filter, and why it moved the route onto `.ProducesValidationProblem()`

[`DocumentTypeNames`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Filtering/DocumentTypeNames.cs) is the `?type=uploaded|generated` token table, shaped after `Service/Export/ConversationExportFormatNames.cs` (the export route's `?format=` table, [Conversation Export](../conversations/conversation-export.md)) for the identical reason: the value a caller sends, the value a rejection message advertises, and the value the client's `GENERATED_DOCUMENT_TYPE` constant writes all have to be the same token, or one of them stops matching.

- **Case-insensitive and trimmed.** A listing row carries `Generated`, the enum member name; the advertised token is lower-case `generated`. Resolving case-insensitively means a client that round-trips a row's own value back into the filter still resolves.
- **Blank means no filter — `null`, an omitted parameter, or whitespace all resolve to "every origin".** That is not a misspelling and is not rejected.
- **An unrecognised value is a validation-error 400 naming `type`,** thrown by `DocumentService.GetUserDocumentsAsync` *before any database work* — `DocumentTypeNames.Resolve` runs first, so a misspelled origin costs nothing more than the round trip to the failure. Ignoring it instead would return a correct-looking page holding documents the caller asked not to see.
- **This is what moved `DocumentEndpoints`' bare-GET route from `.ProducesProblem(400)` to `.ProducesValidationProblem()`.** A binding failure on `?skip=` or `?take=` still answers 400 with no `errors` dictionary; a rejected `?type=` now carries one keyed on `type`. OpenAPI allows one schema per status code, so the route declares the richer of the two.

### 3.3 Why one cross-conversation route, and not a fan-out

US-1001's fourth acceptance criterion demanded the decision be made and recorded rather than defaulted into: the per-conversation route alone forces an N+1 over the conversation list — acceptable for a grouped view of one conversation, not for an unbounded library. The answer (product owner + backend engineer, 2026-08-19, recorded in [PRD §9](../prd/enterprise-ui-rebuild.md#9-assumptions--open-questions)): **both routes ship, and the screen never fans out.** Each item carries its conversation's id *and name*, the client drains pages at `take=100` to the store's 500-item ceiling, and the envelope's `totalCount` is what keeps the on-screen count honest when the drain stops short.

### 3.4 The ingestion re-check

Review of US-1001 found a real defect the new listing would have surfaced: ingestion spans seconds to minutes, and a conversation deleted meanwhile has already run its document-deactivation cascade — **before the document row existed**. The cascade never re-runs, so the row landed active under a deactivated parent: an orphan no query expected, which the library would have listed and whose download would then 404.

Both `Create*DocumentAsync` ingestion paths (conversation *and* project — the project side had the same window) now re-check the parent's active state **after** `SaveChangesAsync`, and when it has gone:

1. Deactivate the just-inserted chunks, then the document, **set-based** via `ExecuteUpdateAsync` — no rowversion involved, so if the cascade did catch the rows between the insert and the re-check, this is an idempotent no-op rather than a `DbUpdateConcurrencyException` from a tracked save.
2. Throw `NotFoundException`, failing the job with the same not-found message an upload into an already-deleted conversation gets.

This shrinks the window to the cascade's own statement gap. It is also load-bearing for §3.2's query shape: it is half of what makes "an active document implies an active conversation" true.

## 4. The store: a drain with two filters, one client-side and one served

[`DocumentLibraryStore`](../../enterprise-gpt-ui/src/app/features/documents/document-library-store.ts) is provided on the component, created by the route that shows it and destroyed with it. It holds **zero seeded records by construction** — a US-1002 criterion: nothing is fetched until the screen binds its origin filter (§4.6), and no placeholder row ever ships in state.

### 4.1 The drain

`ProjectListStore`'s drain, with the same load-bearing details:

- **`take=100` to a 500-item ceiling** (`DOCUMENT_PAGE_SIZE`, `DOCUMENT_LOAD_CEILING` — the PRD's regime-A figures, five requests at most). 100 is the server's own clamp ceiling, so the drain costs the fewest possible round trips.
- **`expand`, not a self-referential `concat`** — each iteration completes and is dropped, so a five-page drain does not retain five nested subscribers.
- **`_querySuperseded$` cancels the whole chain**, not just the request in flight: `switchMap` cancels one request, but the drain is a *sequence*, and without the subject a page fetched by the old drain lands after the retry's `setAllEntities` and appends rows twice. Since US-1005, a change to the origin filter supersedes the same way a retry does (§4.6) — it is the one filter this route serves, so it is the one filter that can supersede a drain in flight.
- **`truncated` is written where the drain stops, never in a `finalize`** — a `finalize` also runs on the cancellation a retry or sign-out causes, and would stamp a partial load as a truncated one.
- **`takeUntil(signedOut$)` on the request**, because `withResetOnSignOut` clears state and does not cancel work; and `withResetOnSignOut` composed **last**, as the feature requires.

Each page's request now carries the origin the *drain that started it* was asked for, not whatever `generatedOnly` reads at the moment a later page fires — `fetchPage`/`nextPage` take it as a parameter rather than reading it off state, so a filter change mid-drain cannot make one drain's later pages ask for a different set than its first page did.

### 4.2 A mid-drain failure clears the dead pages

The one place this store deliberately does **not** copy its reference: on a failure partway through the drain, the error arm runs `setAllEntities([])` + `resetPagination()` + `setError(...)`, so the panel replaces the listing and **Retry shows the skeleton** rather than a half-drained set. A review finding — the reference drain keeps its partial pages, but there they can be a *complete previous result*; here they never are, and `setPending` alone would have re-shown the condemned rows for Retry's first round trip while the event handlers went on patching them. A half-drained set counted or grouped as if it were whole is exactly the silently-partial listing regime A exists to avoid — the same reasoning that makes `ProjectListStore` throw its pages away and `ProjectLookupStore` keep them ([Projects §9.1](projects.md#91-the-lookup-behind-it-and-why-a-failed-drain-keeps-its-pages)).

### 4.3 `withClientQuery`, and where this sits in the regime taxonomy

`withClientQuery` is composed over `entities` through `withFeature` (US-1003), and the configuration is where the decisions live:

```ts
withClientQuery(entities, {
  searchableText: (document) => document.name,      // the name and nothing else
  comparators: {},                                  // no sort story for documents
  isAuthoritative: computed(() => isFulfilled() && isFullyLoaded()),
  // A signal, since US-1005: the noun the shortfall names depends on generatedOnly().
  incompleteSetReason: computed(
    () => `Only the ${DOCUMENT_LOAD_CEILING} most recent ${documentNoun(DOCUMENT_LOAD_CEILING, generatedOnly())} have loaded.`,
  ),
})
```

- **`searchableText` is the file name only** — never the conversation name the rows also carry. That is what the empty state's "Filters cover file names only." promises, and it is why a conversation rename never evicts a row (§4.4).
- **`comparators` is empty.** No sort key is offered and `results` stays in server order — the first `withClientQuery` host with no sort at all.
- **`isAuthoritative` gates on both** `isFulfilled()` and `isFullyLoaded()`, because `isFullyLoaded` reads `true` before the first fetch ([Frontend Foundation §5.2](frontend-foundation.md#52-withoffsetpaginationtake--100)).
- **`incompleteSetReason` is a `Signal<string>` as of US-1005**, not the plain string every other host still passes — [Frontend Foundation §5.4](frontend-foundation.md#54-withclientquerysource-config) widened the config type to `string | Signal<string>` for this one caller, resolved the same way `isAuthoritative` already was. A shortfall over the generated-only listing has to say "generated documents", not "documents" — the two nouns come from the same `documentNoun(count, generatedOnly)` helper `countSummary` and `ceilingNotice` also call (§6.1, §6.4).

Against [the four regimes](frontend-foundation.md#54-withclientquerysource-config) this screen is a hybrid the table does not list: **regime A's loading** (drain at `take=100` to the 500 ceiling) with **regime B's ordering** (server order, no sort control) and a client-side name filter qualified through `incompleteSetReason` when the drain truncates. The filter is not suppressed over a truncated set — a filter over part of the data is still useful where a sort over it is simply wrong — it is *qualified* (§6.4). The origin filter sits **outside** this feature entirely (§4.6): it changes what the drain asks the server for, not what `withClientQuery` narrows client-side.

The grouped view derives from the same `results`, so one term narrows both shapes (§6.3).

### 4.4 Staying in step: `conversationEvents`, server-confirmed facts only

- **`updated`** patches `conversationName` on every row of the affected conversation — a rename has to keep the link column and the group headers honest. It never evicts: the filter is on document names only, so a rename cannot change what matches.
- **`deleted`** removes the conversation's rows and shifts `skip` *and* `totalCount` down together (`shiftCounters`) — deleting a conversation takes its documents with it server-side, so their rows go here too rather than 404-ing on the next download, and both counters move so the count and `hasMore` stay honest.

### 4.5 `bindQuery` is a `signalMethod`, not an `rxMethod`

A deliberate departure from every list store before it: the filter narrows rows the store already holds, so there is **no request to race** and nothing for `switchMap` to cancel. `rxMethod` earns its place where responses can arrive out of order; here it would be ceremony.

### 4.6 The origin filter is served, not client-side — and `bindSource` starts the drain

The one decision US-1005's criteria do not spell out, and the one most worth remembering: **`generatedOnly` narrows at the server**, unlike `query`. `bindSource` is a `signalMethod<boolean>` — not an `rxMethod`, because writing state synchronously and then calling the `_drain` method it triggers is exactly the shape `signalMethod` fits — that does two things on every value, including the first:

```ts
bindSource: signalMethod<boolean>((generatedOnly) => {
  patchState(store, { generatedOnly });
  _drain(generatedOnly);
}),
```

- **The drain, not the filter, decides what "no generated documents" means.** The store drains to the 500-item ceiling; a client-side narrowing over that partial set would make the empty state's "the assistant hasn't created any documents yet" unfalsifiable for a user whose generated files all sit past the 500 most recent documents, and would leave `totalCount` counting rows the filter removed rather than the server's own count of the origin asked for. Serving it instead makes both honest at any library size.
- **Binding the origin *is* the initial load.** There is no separate `load()` any more — `_load` was renamed `_drain` and takes the origin as its trigger value, so the component's constructor needs only `this.library.bindSource(this.source)`; the first value the signal produces runs the first drain, on whatever origin the URL named on first render.
- **A filter change supersedes a drain in flight through the same `_querySuperseded$` the retry path already used** (§4.1) — `switchMap` inside `_drain` cancels the in-flight request chain, and the trigger tap clears the held rows (`setAllEntities([])`, `resetPagination()`, `truncated: false`) before the new request goes out. That clear is what keeps the previous origin's rows from being counted, grouped, or captioned for a whole round trip as though they belonged to the new filter — the same failure mode §4.2's mid-drain clear exists to avoid, one filter change earlier.
- **`retry()` re-drains on `store.generatedOnly()`**, the committed filter — a retry after a failure must not silently drop back to the unfiltered listing.

## 5. The URL contract

The conversations library's loop, extended by one parameter ([Conversation Library §3](conversation-library.md#3-the-url-is-the-source-of-truth)): `withComponentInputBinding()` binds `?name=`, `?view=` and `?source=` to `input()`s, `_applyQuery` is the **only** writer and writes to the router with `queryParamsHandling: 'merge'` (so all three survive each other), a cleared parameter removes its key (`null`), and the loop cannot feed itself because a navigation re-seeds `SearchInput`'s `linkedSignal`, whose debounce finds the text unchanged and emits nothing. `shouldReplaceHistory` is re-exported from `core/navigation/search-history` — refining a term replaces, adding or removing one pushes, and both toggles always push because on/off *is* the add-or-remove transition.

The one structural difference from the conversations library: the **name** term navigates but never issues a request — the URL drives `setQuery`, not a fetch. The **origin** filter is the exception (§4.6): its navigation is what triggers the re-drain, exactly the way `?view=` triggers nothing server-side at all. The consequences of the URL-as-source-of-truth rule are the same regardless: a filtered, grouped-or-flat, origin-narrowed view is shareable, the back button undoes any of the three, and a deep link filters on the first render because both `bindQuery` and `bindSource` were handed the signal, not its value.

**Grouped is the default, so the URL records only the deviation** — and the origin filter follows the identical rule. Frame `4j` draws the group switch checked; a bare `/documents` is the grouped, every-origin view, and only `?view=flat` or `?source=generated` is ever written. Encoding either default (`?view=grouped`, `?source=uploaded`) would make two URLs mean one state, and would break every bookmark if a default ever changed — and, for `?source=`, it is also what stops the client from ever sending the server a `?type=` value the switch cannot represent (only `generated` is a real request; anything else the client would have to invent).

## 6. The screen

Frame `4j`: a title, a toolbar, and a card of rows — grouped under conversation headers by default, flat with a conversation link column behind the toggle. Frame `4k` covers the error and empty states; US-1005 adds a fourth toolbar control and a second empty-state shape.

### 6.1 The toolbar

Four things, left to right: the filter field, the **Group by conversation** switch, the **Generated only** switch, and the count.

- **The field is labelled "Filter documents by name" — "Filter", not "Search".** The criterion's own wording, and honest about the mechanics: it narrows what is already here rather than asking the server.
- **The origin control is a second `.form-check.form-switch`, not a pressed button.** Frame `4j` draws no chrome for this filter at all, so the decision was the client's; a switch was chosen over a toggle button because two filters that look alike beat one that invents a second idiom for the same toolbar, and it reuses the group switch's own markup pattern.
- **The count is `aria-hidden`, mirrored by one visually-hidden `role="status"` element** that exists from first render — a live region inserted together with its content is not reliably read, and two independently derived counts would eventually disagree. It is withheld before the first response, where "No documents" would be a claim rather than a count.
- **`countSummary` reads the envelope's `totalCount`, not the rows held.** Unfiltered it states the total ("12 documents") — which is what keeps it honest under the ceiling, whose notice already qualifies the shortfall. Filtered on name it pairs the visible figure with that same total ("Showing 3 of 12 documents"), because a client-side filter changes what the listing shows without changing what the server holds. Narrowed to one origin, both forms say "generated documents" instead of "documents" (`documentNoun`, §4.3) — a count over the assistant's own files must not read as a count over the library.
- **The whole toolbar is withheld under the error panel** — a divergence from the conversations library, whose field stays up beside its panel. The name field drives a client-side narrowing a retry cannot help; the origin switch drives a request, but the same withholding applies to it too — over a failed load there is nothing on screen for either control to act on. Frame `4k` draws the error card alone.

### 6.2 The rows: custom, not `DataTable`

`app-document-row` is the `ProjectFiles` anatomy — glyph, name, size, date, then the row's controls — and **deliberately not `DataTable`**: the table always renders the header row frame `4j` does not draw, and it has no grouping for US-1003 to grow into.

- **The glyph, the size and the date reuse the existing formatters** — `fileGlyph` (the attachment chip's table, tone included), `formatBytes` (the same digits the chip and the too-large toast show) and `formatShortDate`.
- **The source badge is data-driven, since US-1004/US-1005.** `<app-source-badge [source]="source()" />` reads a small `BADGE_SOURCES` lookup (`Record<DocumentType, DocumentSource>`) keyed off the row's own `type` — a translation table because `domain/`'s wire vocabulary (`'Uploaded' | 'Generated'`) and `shared/`'s badge vocabulary (`'uploaded' | 'generated'`) are two different unions on two different layers, and a row component is the one place allowed to know both. The lookup defaults to `'uploaded'` on a miss, checked exhaustively against the union rather than against a wire that could add a member before the union does — so an origin this build does not know degrades to the existing badge rather than rendering nothing.
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
| `error()` | Frame `4k`'s `ErrorPanel` — "Documents couldn't load", the `traceId`, Retry — **instead of everything**, toolbar included | **Retry** re-runs the drain from the first page, on the committed origin |
| `isFirstLoad()` | Six skeleton rows, `aria-busy` | — |
| `isEmpty()` **and not** `generatedOnly()` | "Nothing uploaded yet" / "Attach files in any conversation and they'll appear here.", the ridgeline motif | **None** — uploading happens in a conversation's composer, and a button here could only restate the sentence below it |
| `isEmpty()` **and** `generatedOnly()` | "No generated documents yet" / "Files the assistant creates in a conversation appear here.", the same ridgeline motif | **Show every document** — clears `?source=` *and* `?name=` together (§9), then returns focus to the origin switch |
| `hasNoMatches()` | Frame `4b`'s shape: a heading naming the committed term, the scope line "Filters cover file names only." | **Clear filter**, which also returns focus to the field |
| Otherwise | The card of rows, grouped or flat | — |

Four orderings in that table are decisions:

- **The empty set wins over no-matches.** `isEmpty` is checked first, so a zero-document deep link that carries `?name=` reads the empty state's own heading rather than "no matches" — the truth, since there is nothing a cleared name term would reveal. With the name filter client-side, the two states are genuinely tellable apart, which the conversations library (whose filter is a server parameter) cannot always claim.
- **`isEmpty` picks its own heading and message from `generatedOnly()`, not from a hidden third state.** The origin filter is the one exception to "nothing here would reveal more": it is served, so it really is why nothing came back, and it is the one thing on this screen worth an undo action. **Show every document** is deliberately not called Clear filter — it clears two parameters at once, and naming what it does prevents the reader from wondering why a term they typed also vanished.
- **Over a truncated drain, the scope line yields to the partial-set qualifier.** The no-matches message renders `resultsPartialReason() ?? 'Filters cover file names only.'` — an unqualified "no matches" over 500 of 812 documents would claim rows the store never loaded.
- **One qualifier below the card, never both.** Unfiltered, `ceilingNotice` states what the card is showing ("Showing the 500 most recent of 812 documents."); filtered, `resultsPartialReason` qualifies the *result* instead, because a count computed over part of the data is the claim that needs the caveat. Rendering both would say the same thing twice in different words. Narrowed by origin, `ceilingNotice` itself says "generated documents" (§4.3, §6.1).

## 7. Accessibility

`/documents` joined `AXE_ROUTES` **in the same change as the route** — the rule [the audit's coverage guard](accessibility-audit.md#52-the-coverage-guard) enforces — and `a11y-coverage.spec.ts`'s absence assertion was deleted exactly as that assertion's own comment demanded. `documents.a11y.spec.ts` runs **6 browser audits**: the original 4 (both themes × both view modes, because the two render different anatomies: group headers with chevrons and links on one side, the conversation link column on the other) plus **2 added by US-1005** — both themes over the filtered empty state (`/documents?source=generated` with nothing to show), the one screen state with an action button neither of the other four exercises. The suite is at **58**. The grouped/flat fixture also gained a `Generated` row so both source-badge variants are measured, not only the muted `Uploaded` one; an empty listing has no glyphs, badges, links or download controls in it, which is most of the surface worth auditing.

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
- **The name filter covers file names only** — the searchable text is one field, and the empty state says so. Widening it is a product decision, not an oversight to fix in passing. The origin filter is the one other axis this screen offers, and it is the only one served rather than client-side (§4.6).
- **No sort control.** No sort story exists for documents; the server's newest-first order is the only one, `comparators` is empty, and `results` stays in server order (§4.3).
- **No per-row detach.** There is **no `DELETE` route for a conversation document** — only project documents have one — so once ingested, a document belongs to its conversation and this screen offers download only. The same gap shapes the attachment chip's Remove/Dismiss split ([File Attachments §4.1](file-attachments.md#41-remove-means-two-different-things)).

## 10. Testing

**51 specs across the screen's two files** — 29 in `documents.spec.ts` (the URL contract and history rule, the view toggle, the grouped view and its collapse semantics, the four non-error states and the count, plus US-1005's `describe('the assistant-created filter')` block: the switch's default, the served narrowing, the deep link, the filtered empty state's action and focus return, dropping the name term alongside the origin, and the two filters surviving each other) and 22 in `document-library-store.spec.ts` (the drain, the ceiling, the failure arm clearing its pages, the two event handlers, the name filter and grouping, plus US-1005's `describe('served origin filter')` block: no `type` sent until one is bound, the narrowed request, superseding a drain in flight, carrying the filter through every page of one drain, dropping the previous rows the moment the origin changes, and naming the narrowed set in every count and notice) — inside the suite's **2122** jsdom tests across **159 files**, plus the 6 browser audits (§7) in the a11y suite's **58**. The shell spec asserts the nav entry, and `a11y-coverage.spec.ts` simply covers `/documents` like any other route.

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
| The listing shows fewer documents than the count claims | By design at the ceiling: the drain stops at 500 and `countSummary` reads the envelope's total. The ceiling notice below the card states the shortfall (§6.4) |
| Typing in the filter issues requests | Something replaced `bindQuery`'s `signalMethod` with a fetch. The name filter is client-side; typing navigates and `setQuery` narrows rows already held (§4.5, §5) |
| A deep link with `?name=` flashes the unfiltered list first | `bindQuery` was handed `this.name()` instead of `this.name` — it needs the signal to filter on the first render (§2.1) |
| `/documents?view=grouped` renders flat, or the switch disagrees with the listing | It cannot, unless the transform changed: anything other than `flat` reads as grouped, and only `flat` is ever written (§2.2) |
| `/documents?source=uploaded` narrows the listing, or the switch reads on | It cannot, unless the transform changed: anything other than `generated` reads as unfiltered, and only `generated` is ever written (§2.2) |
| The server rejects a request with `type=uploaded` or `type=generated` | It should not — both are in `DocumentTypeNames.Supported`. Rejected instead means the client sent something else, most likely a stray value from a hand-built query string (§3.2.1) |
| The listing shows both origins after the switch was turned on | `bindSource` never called `_drain`, or a stale request from before the toggle landed and re-appended rows — check `_querySuperseded$` fired for the filter change too, not only for retry (§4.6) |
| Retry briefly shows stale rows before the skeleton | The failure arm stopped clearing its pages. `setAllEntities([])` + `resetPagination()` belong with `setError` — a half-drained set is never a previous complete result here (§4.2) |
| Rows appended twice after a retry, or after toggling the origin filter | The drain lost `_querySuperseded$`. `switchMap` cancels only the request in flight, and the drain is a chain (§4.1, §4.6) |
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
| The `?type=` token table | [`Enterprise.Gpt.Service/Filtering/DocumentTypeNames.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Filtering/DocumentTypeNames.cs) |
| The badge lookup | [`features/documents/document-row.ts`](../../enterprise-gpt-ui/src/app/features/documents/document-row.ts) (`BADGE_SOURCES`), [`shared/badge/source-badge/source-badge.ts`](../../enterprise-gpt-ui/src/app/shared/badge/source-badge/source-badge.ts) |
| The migration | [`Repository/Migrations/20260819042820_AddConversationDocumentUserIndex.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260819042820_AddConversationDocumentUserIndex.cs) |
| Related reference | [File Attachments](file-attachments.md), [Projects](projects.md), [Conversation Library](conversation-library.md), [Frontend Foundation](frontend-foundation.md), [Accessibility Audit](accessibility-audit.md), [Generated Files](../file-agent/generated-files.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md) |
