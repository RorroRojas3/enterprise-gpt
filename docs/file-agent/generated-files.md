# Generated Files: Storage and Delivery

Enterprise GPT could only ever read a document into a conversation, never hand one back — an assistant
asked for "that as a spreadsheet" could describe a spreadsheet, never produce one. This document covers
the contract that closes the other direction: how a file the platform itself produces is stored,
downloaded, kept out of retrieval, and delivered to a client, both live and after a reload.

**Scope.** This is the storage and delivery contract alone — the `Uploaded`/`Generated` discriminator,
the second blob container, the write path, the withdrawal path, the download route, retrieval isolation,
and the transcript reference. It says nothing about what *produces* a file; see
[`the-agent.md`](the-agent.md) for the File Agent itself. The contract was built to stand on its own
specifically so it could be tested against a hand-inserted `Generated` row before the agent existed —
every piece below shipped and passed its own tests two stories ahead of the first line of agent code.

## 1. The `Uploaded`/`Generated` discriminator

`ConversationDocumentTypes` (`Enterprise.Gpt.Dto/Enums/ConversationDocumentTypes.cs`) has two members,
numbered from 1 so an unset column is distinguishable from a legitimate value — the same convention
`JobStatus` already follows:

```csharp
public enum ConversationDocumentTypes
{
    Uploaded = 1,
    Generated = 2
}
```

It lives on `ConversationDocument.Type` and nowhere near `BaseDocument` — `ProjectDocument` is
untouched, so a project-scoped generated file stays out of scope entirely. `ConversationDocumentConfiguration`
configures it with `HasConversion<int>()`, a store `HasDefaultValue(ConversationDocumentTypes.Uploaded)`,
and `ValueGeneratedNever()` — the same pairing `Model.IsUserSelectable` uses, and for the same reason:
EF scaffolds a migration's `AddColumn` from the CLR default alone, so without an explicit store default
every existing row would backfill to whatever the enum's zero value is, which this enum deliberately
does not define. Migration `20260828050720_AddConversationDocumentType` adds the column with
`defaultValue: 1`, so every row that predates this feature reads back as `Uploaded`.

`Tool/DocumentRetrievalSql.cs`'s hand-written SQL depends on there being no `HasColumnName` override —
worth knowing if you ever touch that configuration.

## 2. The second blob container

A generated file never lands in the `documents` container an upload uses. `AzureStorage:GeneratedContainer`
names a second one — `generated-documents` in the committed configuration — so an uploaded file and a
generated one never share a retention policy, an access policy, or a lifecycle rule. Both settings are
read on **each use**, not cached at construction, so a reloaded configuration takes effect without a
restart; an unset value throws `StorageNotConfiguredException`, which maps to the existing 503
`/problems/storage-not-configured` — no new problem type for this feature.

`DocumentStorage` (`Enterprise.Gpt.Service/DocumentStorage.cs`) is the internal static helper both write
paths now share, so the two cannot drift apart on either fact:

- `RequireContainer(configuration, logger, key)` — reads and validates a container name, given
  `DocumentStorage.DocumentsContainerKey` (`AzureStorage:DocumentsContainer`) or
  `DocumentStorage.GeneratedContainerKey` (`AzureStorage:GeneratedContainer`).
- `ResolveContentType(extension)` — the same extension-to-content-type map `DocumentService` used to own
  privately, now shared with the generated-document write path so a `.xlsx` served from either container
  gets the identical `Content-Type`.

Blob keys follow the existing convention unchanged: `{userId}/{conversationId}/{documentId}{extension}`.
It cannot collide with an uploaded blob's key even though the shape is identical, because the two live
in different containers.

## 3. Persisting a generated file without ingesting it

`IGeneratedDocumentService.StoreAsync` (`Enterprise.Gpt.Service/GeneratedDocumentService.cs`) is the
whole write path, and it is deliberately not a smaller version of the upload pipeline — it skips that
pipeline entirely. No `IDocumentTextExtractor` is resolved, no chunker runs, no embedding is generated,
and no background job is queued. It:

1. Normalizes the file name (strips any directory the sandbox put on it, bounds it to 256 characters)
   and validates it: non-empty, no larger than `Documents:MaxFileSizeBytes`, and one of the seven
   extensions this platform actually produces (`.csv .docx .md .pdf .pptx .txt .xlsx`) — a runaway
   script writing anything else never becomes a downloadable blob, whatever the agent's own instructions
   said.
2. Uploads the bytes to the `generated-documents` container, tagged with the owning user, conversation,
   and document id as blob metadata.
3. Inserts a `ConversationDocument` row with `Type = Generated` — on its own DI scope, described below.

If the insert fails after the blob write succeeds, the blob is deleted (best effort, logged, never
throws) so a failure does not leave content no row references, billed forever with nothing pointing at
it. The blob is written **first** deliberately: the alternative order would risk a row referencing a
blob that never landed, which is the worse of the two half-committed states, because a download would
404 against real evidence a client was told exists.

### 3.1 Why the insert runs on its own DI scope

`InsertAsync` obtains a fresh `EnterpriseGptDbContext` from `IServiceScopeFactory.CreateAsyncScope()`
rather than reusing the calling turn's shared context — mirroring `DocumentSummaryService`'s own
persistence pattern, and for the identical reason. EF Core only accepts an entity's tracked state on a
*successful* save; if this insert shared the turn's context and failed, the row would stay tracked as
`Added` on that context, and the turn's own end-of-turn finalization save would silently re-attempt an
insert that had already partially committed — after the answer had already streamed to the client.

The write is also **uncancellable** past the blob upload: by that point the sandbox run has already been
paid for and the bytes are already durable, and the only token that could abort the insert is the one a
client disconnect signals — cancelling here would leave a paid-for blob with no row pointing at it, the
same orphan the try/catch above exists to prevent from the other direction.

One more invariant `InsertAsync` enforces that is easy to miss: a turn can legitimately outlive the
conversation it runs against (the conversation gets soft-deleted mid-turn). After the insert, it
re-checks the conversation is still active and, if not, immediately soft-deletes the row it just wrote —
set-based, because the deactivation cascade may have already moved the row's own concurrency token
between the insert and the re-check. Without this, a document could end up active under a deactivated
conversation, which every listing in this codebase otherwise treats as impossible.

## 4. Withdrawing a file the turn did not deliver

A generated document reaches the transcript only on a completed turn (§8). Stopping or failing a turn
after the agent had already stored a file would otherwise leave that row and blob live under a
conversation whose transcript never mentions them — reachable from `GET api/documents`, with no message a
user could point to and no way to remove it themselves.

`IGeneratedDocumentService.DiscardAsync(documentId)` (`GeneratedDocumentService.cs`) is what withdraws
one. It is called once per artifact from `FileAgentToolLease.DiscardGeneratedAsync()`
(`Enterprise.Gpt.Api.Agents`) — which copies and clears the lease's own `Stored` list first, so a caller
that reads `GeneratedDocuments` afterward sees the turn's real answer (nothing) whatever the withdrawals
themselves do — and that, in turn, is called from `ConversationService`'s streaming `finally` whenever the
turn did not complete. `DiscardAsync` itself:

1. Reads the row's blob path, scoped to `Type == Generated`. A document already gone — a race, or an id
   this turn never actually stored — makes it a no-op.
2. Soft-deletes the row **first**, on its own DI scope for the identical reason `InsertAsync` uses one
   (§3.1). Row-first is the opposite order the write path uses, and deliberately so: a row pointing at a
   blob that is gone reads to every listing as a file that will not download, where a blob with no row is
   merely unreferenced — the less visible failure of the two, and the one worth risking here.
3. Deletes the blob, best effort, logged, never thrown — the same posture `FileAgentSandbox.ReleaseAsync`
   ([`the-agent.md`](the-agent.md) §8) takes on a leaked input file.

The whole call is **uncancellable and never throws**: it runs inside a turn that is already unwinding, on
the very token that caused the unwind, and a discard failure must not replace the exception already in
flight. A withdrawal that itself fails leaves an orphaned row and blob for an operator to reclaim by hand —
logged at error, not retried, because nothing in this contract runs a cleanup pass.

## 5. Downloading a generated document

The download route is **unchanged** — `GET api/documents/conversations/{conversationId}/{documentId}`
(`DocumentEndpoints.cs`), the same route and the same ownership rule (read off the parent conversation;
a miss is 404, never 403) an uploaded document already uses. The only change is inside
`DocumentService.BuildDownload`: the container the signed link is issued against is now chosen from the
row's own `Type` —

```csharp
document.IsGenerated ? GeneratedContainer : DocumentsContainer,
```

— rather than assumed. It has to be read off the row rather than inferred from the file extension,
because both containers hold the same seven formats; nothing about a `.xlsx` blob's name says which
container it lives in.

## 6. Retrieval isolation — two independent guarantees

A generated document must never be searched, summarized, or cited as though a user had uploaded it. This
holds two ways, deliberately redundant:

**Structural.** A generated document is never chunked, so it has zero rows in
`Core.ConversationDocumentChunk`. `Tool/DocumentRetrievalSql.cs`'s `UNION ALL` needed **no change** for
this — a document with no chunk rows contributes nothing to the candidate set, because it was never
there to filter out.

**Explicit, in the scope itself.** `DocumentRetrievalService.GetScopeAsync` filters to
`Type == ConversationDocumentTypes.Uploaded` when it builds a conversation's `DocumentRetrievalScope`.
This matters independently of the structural fact above: the scope's `HasDocuments` flag and its
document-name list feed the retrieval prompt and gate whether `document_search`, `document_summarize`
and `sheet_query` attach to the turn at all. Without this filter, a generated file's *name* would still
reach the model even though its content could never be retrieved — and a conversation whose only file is
a generated one would look, incorrectly, like a conversation that has a document. With it,
`HasDocuments` is `false` and none of those tools attach, so the conversation reads exactly like one with
no files at all.

`IFileAgentDocumentReader` (see [`the-agent.md`](the-agent.md) §7) is the one reader in this codebase
that deliberately goes the other way — it must see a conversation's generated documents too, so the
agent can edit or convert a file it made earlier. It applies the same ownership rule as the download
route, but no `Type` filter.

## 7. The transcript reference

A generated document reaches the transcript as an identity, never a credential. `TranscriptMessageDocument`
(`Entity/Transcripts/TranscriptDocuments.cs`) gained `Attachments`, a list of
`TranscriptMessageAttachment { Id, Name, Extension, MimeType, Size }` — the same shape as the wire DTO,
`MessageAttachmentDto`. Nothing in either carries a URL or a SAS token: a client always asks the download
route for a link at the moment it is needed.

`TranscriptHeaderDocument.CurrentSchemaVersion` moves from `1` to `2`. The new field is additive and
backward-compatible by construction — a transcript document written before this change simply has no
`attachments` property, which deserializes as an empty list, so **no transcript migration runs**. The
field is excluded from the Cosmos indexing policy (`CosmosBootstrapper`'s `/attachments/*` entry) the
same way `/content/*` and `/htmlContent/*` already are, since nothing ever queries by attachment content.

## 8. Two ways a client learns about a generated file

The identity of a file a turn produced reaches the client through two different paths, and both read
from the same underlying fact — the persisted `TranscriptMessageDocument.Attachments` — so they can never
disagree about what a turn made.

**Live, on the turn that made it.** The stream's closing `Finished` event carries a
`generatedAttachments` key in its `metadata` bag, a JSON-serialized array of `MessageAttachmentDto`. It
is JSON *inside* a string, not a typed array, because the vendored streaming contract's metadata bag is
`Record<string, string>` — a typed field would be a breaking change to a contract `npm run check:contract`
guards byte-for-byte. The key is present only when the turn actually stored at least one file; a turn
that produced nothing carries no such key. `ConversationService` copies the lease's `GeneratedDocuments`
out before the tool lease's scope closes (the frame that carries them is yielded after that scope would
otherwise have disposed), then serializes them onto the `Finished` frame alongside the existing
`assistantMessageId` key.

**On reload, from the transcript itself.** `GET api/conversations/{id}/messages` projects each message's
`Attachments` straight off the same rows the write path above created — no separate query, no separate
cache. This is what makes a generated file survive a reload without replaying the activity tree that
made it: only the answer text and the file reference are ever transcribed, matching the existing rule
that a reopened conversation replays an answer, never the work that produced it.

### 8.1 The client side

`turn-store.ts` reads both paths into the same shape. `parseAttachments` decodes the live metadata key
defensively — a parse failure, or a value that is not an array, resolves to an empty list rather than
throwing, because a turn whose files could not be described should read as a turn with no chips, not a
broken transcript. `toHistoryEntries` reads `message.attachments` the same way `message.feedback` is
already read for a rating, on the identical principle: what the server holds on reload is what the
transcript draws.

`app-generated-files` (`features/chat/transcript/generated-files.ts`) renders the chips. It sits
**outside** the turn's hover-revealed footer, deliberately — a file the assistant made is part of the
answer, not an affordance about it — and reuses `shared/chip/attachment-chip` through a new `[generated]`
input rather than a second chip component:

- The chip leads with the file's extension glyph instead of an upload-lifecycle icon, since a generated
  file has no uploading/processing/failed states to represent.
- A `SourceBadge` reads "Generated" in text, not colour alone.
- The chip's accessible name is `"Generated file, {name}"`, so the distinction survives without sight of
  either the glyph or the badge.
- No remove button and no retry — there is nothing to cancel and nothing to dismiss; the file is already
  stored.
- Clicking it calls the existing `DocumentDownloadStore`, unmodified: a link is minted at the moment of
  the click and handed straight to a detached anchor, never prefetched, never written to state.

## 9. Configuration

| Key | Default | Effect |
| --- | --- | --- |
| `AzureStorage:GeneratedContainer` | *(none — must be set)* | The Azure Blob Storage container generated files are written to and downloaded from. Unset throws `StorageNotConfiguredException` on first use (503 `/problems/storage-not-configured`). |

`Documents:MaxFileSizeBytes` (already existing) doubles as the ceiling on a generated artifact — the
same 50 MB limit an upload is held to.

## 10. Testing

**Unit** (`Enterprise.Gpt.Unit.Test`): `Services/GeneratedDocumentServiceTests.cs` drives the write path
against SQLite in-memory — container selection, the `Generated` type on the inserted row, the size and
extension refusals, and the blob-cleanup-on-insert-failure path. `Tool/DocumentRetrievalServiceTests.cs`
gained `GetScopeAsync_GeneratedDocument_IsExcluded` and
`GetScopeAsync_OnlyGeneratedDocuments_ReportsNoDocumentsAtAll`, pinning both retrieval-isolation
guarantees from §6.

**Integration** (`Enterprise.Gpt.Integration.Test`): `Documents/GeneratedDocumentIntegrationTests.cs`
runs the whole contract against real SQL Server 2025 and the fake blob store —
`StoreAsync_Artifact_LandsInTheGeneratedContainerAndNotTheUploadsOne`,
`StoreAsync_Artifact_CreatesAGeneratedRowWithNoChunks`,
`Download_GeneratedDocument_IsSignedAgainstItsOwnContainerWithItsOwnType`,
`Download_GeneratedDocumentOfAnotherUsersConversation_IsNotFound`,
`StoreAsync_ConversationBelongingToSomeoneElse_StoresNothing`, and, for §4's withdrawal path, that a
discarded document leaves no live row and no blob behind.

**Frontend** (`enterprise-gpt-ui`): `features/chat/transcript/generated-files.spec.ts` covers the chip's
rendering, its accessible name, the absence of a remove control, mint-on-click behaviour, that no link
is prefetched, that the signed URL never lands in the DOM or storage, and per-chip download-pending
isolation. `features/chat/turn-store.spec.ts` gained coverage for `parseAttachments` and for both the
live and reload paths landing on the same `attachments` shape.

## 11. What this contract does not cover yet

This document describes storage and delivery only. Wave 2 is the first to call it for real — every one of
the agent's four verbs (create, edit, compare, convert; see [`the-agent.md`](the-agent.md)) now stores
through exactly the path above — but the contract itself did not have to change to be exercised that way,
which was the point of building and testing it against a hand-inserted `Generated` row two stories before
the agent existed. Specifically out of scope here:

- **Project-scoped generated documents.** The discriminator sits on `ConversationDocument`, never
  `BaseDocument`; `ProjectDocument` is untouched by design.
- **A permission gate.** Nothing in this contract checks a grant — the feature is reachable end to end
  once `FileAgent:Enabled` is on, and the dedicated permission arrives in a later wave.
- **Verifying an artifact is the caller's job, not this contract's.** `StoreAsync` still validates only
  size and extension, never that a `.xlsx` actually parses as one. That check now exists — it did not when
  this line was first written — but it runs **before** `StoreAsync` is ever called, on the API host rather
  than as a sandbox pass; see [`the-agent.md` §5.3](the-agent.md#53-verification-on-this-host-not-in-the-sandbox).
  Deliberately kept out of this contract, which stays reusable by anything that already has bytes it has
  already verified.

## 12. Key files

| Concern | File |
| --- | --- |
| The discriminator | [`Enterprise.Gpt.Dto/Enums/ConversationDocumentTypes.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/ConversationDocumentTypes.cs) |
| Its entity and configuration | [`Enterprise.Gpt.Entity/ConversationDocument.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/ConversationDocument.cs), [`Enterprise.Gpt.Repository/Configurations/ConversationDocumentConfiguration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/ConversationDocumentConfiguration.cs) |
| The migration | [`Enterprise.Gpt.Repository/Migrations/20260828050720_AddConversationDocumentType.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260828050720_AddConversationDocumentType.cs) |
| The shared container/content-type helper | [`Enterprise.Gpt.Service/DocumentStorage.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/DocumentStorage.cs) |
| The write path | [`Enterprise.Gpt.Service/GeneratedDocumentService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/GeneratedDocumentService.cs) (`StoreAsync`) |
| The withdrawal path | [`Enterprise.Gpt.Service/GeneratedDocumentService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/GeneratedDocumentService.cs) (`DiscardAsync`), [`Enterprise.Gpt.Api/Agents/FileAgentToolProvider.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Agents/FileAgentToolProvider.cs) (`DiscardGeneratedAsync`), [`Enterprise.Gpt.Service/ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs) (the streaming `finally`) |
| The download route's container choice | [`Enterprise.Gpt.Service/DocumentService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/DocumentService.cs) (`BuildDownload`) |
| Retrieval isolation | [`Enterprise.Gpt.Service/Tool/DocumentRetrievalService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tool/DocumentRetrievalService.cs) (`GetScopeAsync`) |
| The transcript reference | [`Enterprise.Gpt.Entity/Transcripts/TranscriptDocuments.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/Transcripts/TranscriptDocuments.cs), [`Enterprise.Gpt.Api/Startup/CosmosBootstrapper.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Startup/CosmosBootstrapper.cs) |
| The `Finished`-frame metadata key and the turn-finalization write | [`Enterprise.Gpt.Service/ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs) |
| The client read paths | [`features/chat/turn-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/turn-store.ts) |
| The chip | [`features/chat/transcript/generated-files.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/generated-files.ts), [`shared/chip/attachment-chip/attachment-chip.ts`](../../enterprise-gpt-ui/src/app/shared/chip/attachment-chip/attachment-chip.ts) |
