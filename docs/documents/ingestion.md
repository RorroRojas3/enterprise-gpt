# Document Ingestion

Upload to searchable chunks: validation, blob storage, text extraction, chunking, embedding, and the
background job that runs it.

## API surface

| Route | Purpose |
| --- | --- |
| `POST api/documents/conversations/{conversationId}` | Upload into a conversation |
| `POST api/documents/projects/{projectId}` | Upload into a project |
| `DELETE api/documents/conversations/{conversationId}/{documentId}` | Remove an uploaded conversation document, its summary, sheets and chunks |
| `GET api/documents/upload-status/{jobId}` | Poll a running job |
| `DELETE api/documents/upload-status/{jobId}` | Cancel a queued or running job and remove whatever it produced |
| `GET api/documents/file-extensions` | What this deployment can read |
| `GET api/documents` | The caller's documents |

Both upload routes carry `PermissionEndpointFilter.Require(PermissionIds.UploadFile)` and
`MaxUploadSizeEndpointFilter.Require(...)`.

The delete route accepts an **uploaded document only**; a generated one is reported as missing
rather than removed. A generated file's identity also lives in the Cosmos transcript, on the
assistant message that produced it, and this cascade has no way to patch that — deleting the row
alone would leave a chip that renders forever and 404s when clicked. It carries no permission
filter: ownership of the conversation is the gate, matching the download route and the project
document delete (`DELETE api/projects/{projectId}/documents/{documentId}`), not `Upload File` —
gating it on that grant would strand a user's own documents the moment the grant was revoked.
Project documents already had a delete route; this is the conversation-side counterpart, and the
cascade it runs mirrors `ProjectService`'s.

## Supported formats

Eight extensions, **derived from the registered extractors rather than configured**.
`DocumentTextExtractorFactory` builds a map from every `IDocumentTextExtractor` in DI, and both the
upload validator and the extensions endpoint read that map — so configuration cannot advertise a
format nothing can read, and two extractors claiming one extension fail at startup rather than
silently picking a winner.

| Extension | Reader | Segment | `SourceNumber` |
| --- | --- | --- | --- |
| `.csv` | CsvHelper | schema card plus header-repeating row windows | `1` |
| `.doc` | DocSharp to `.docx`, then Document Intelligence | whole document | `null` |
| `.docx` | Document Intelligence | page, or whole document | page when paginated |
| `.md`, `.txt` | `StreamReader` | whole file | `null` |
| `.pdf` | Document Intelligence, `prebuilt-read` | page | page number |
| `.pptx` | `DocumentFormat.OpenXml` | slide, plus speaker notes | slide number |
| `.xlsx` | `DocumentFormat.OpenXml` | schema card plus row windows, per sheet | sheet ordinal |

`.xls` and `.xlsm` are not accepted.

Reader choices worth knowing:

- **`.doc` is converted in memory** because Document Intelligence does not accept the binary OLE2
  container. Malformed input surfaces as a wide, undocumented range of exception types, so the catch
  is deliberately broad and every one becomes the same 400-class `ValidationException`.
- **`.pptx` and `.xlsx` are read locally, not sent to OCR** — both already store their text as text,
  so parsing is free, lossless, and yields a real slide or sheet number for provenance.
- **Slides come from `SlideIdList` and sheets from the workbook's `Sheets` list**, not from
  `SlideParts` / `WorksheetParts` — only the former are in document order. A chart or dialog sheet
  is skipped but still consumes its ordinal, so a later sheet's number always matches Excel's own
  tab strip.
- **CSV delimiter resolution is in-house.** `,`, `;` and tab are ranked by how many opening lines
  contain each, because CsvHelper's own detector prefers the culture's list separator and would
  mis-resolve a semicolon-delimited European export whose decimal commas appear on every line.
- **Encoding** honours a UTF-16/UTF-32 BOM, falls back to UTF-8, and normalizes line endings to
  `\n`, so chunk boundaries and token counts do not depend on the authoring OS.
- **Empty segments are dropped**, so a document with no readable text returns zero segments and the
  job fails with an explanation.

## The pipeline

`DocumentService` owns sequencing, persistence and progress; format-specific reading and splitting
sit behind `IDocumentTextExtractor` and `ITextChunker`. It runs on a background scope and therefore
takes `userId` as a parameter — there is no `HttpContext` to read it from.

**Store.** The blob path is `{userId}/{conversationId}/{documentId}{extension}`. Keying on the
document id rather than the file name is deliberate: two uploads of the same name into one
conversation are legitimate, and a name-based path made the second collide with the first. No
`BlobHttpHeaders` are written — only metadata — which is why a download link has to sign the file
name and content type in as response-header overrides.

**Extract.** `DocumentExtractionProgress.Fraction` is **nullable**, because a remote OCR call is
opaque until it returns. A null fraction pins the reported percentage at the band's floor and
refreshes only the message and timestamp, so the feed shows the job is alive without claiming
progress it cannot know.

Reports are delivered through a private `InlineProgress<T>` rather than `System.Progress<T>`, which
posts each callback to the thread pool — letting reports arrive out of order and a stale message
overwrite a newer one. The callback only touches a concurrent dictionary, so there is nothing to
offload.

**Chunk, embed, persist.** The final write is a **single `SaveChangesAsync`** covering the document
and all its chunks, so a document is never half-indexed. Nothing is wrapped in `try`/`catch`:
anything thrown reaches `BackgroundJobProcessor`, which logs it and marks the job `Failed`.

After the save, both paths **re-check that the parent is still active**. Ingestion spans seconds to
minutes, and a conversation or project deactivated meanwhile has already run its document cascade,
which never re-runs — so a row persisted after it would linger as an active orphan. On failure the
just-inserted rows are deactivated set-based via `ExecuteUpdateAsync` (idempotent, no rowversion
involved) and the job fails with the same not-found message an upload into a deleted parent gets.

## Spreadsheet ceilings

A `.xlsx` is a deflate archive: a few kilobytes of compressed markup can expand to hundreds of
megabytes, so `Documents:MaxFileSizeBytes` — which bounds the *uploaded* bytes — bounds nothing
about what a workbook costs to read.

| Key | Default | Bounds |
| --- | --- | --- |
| `Sheets:MaxRowsPerSheet` | 20,000 | Populated rows from one sheet |
| `Sheets:MaxRowsPerUpload` | 50,000 | Populated rows from one upload, all sheets |
| `Sheets:MaxColumnsPerSheet` | 200 | Columns from one sheet |
| `Sheets:MaxCharactersPerUpload` | 8,000,000 | Total cell text, shared strings included |

`MaxRowsPerUpload` closes the gap the per-sheet ceiling leaves: nothing caps how many sheets a
workbook holds, so a hundred sheets each just under the per-sheet limit would still land millions of
rows in one `SaveChangesAsync`.

A crossed ceiling **refuses the upload outright, before anything is persisted — never silently
truncates**. A shortened sheet would still look complete to a later reader, and every answer drawn
from it would be quietly wrong.

Two non-configurable guards ride on the character ceiling:

- Serialized cell data may not exceed **8x** it, because every populated cell repeats its column's
  name once persisted as JSON.
- Before `SpreadsheetDocument.Open` runs at all, every zip entry is inflated and the **real**
  decompressed bytes counted, budgeted at **32x** the character ceiling and floored at 256 KB. A
  zip's declared entry length is attacker-controlled and never verified while inflating, so the
  count has to come from the bytes. A 132 KB workbook holding one row of a million cells, and a
  382 KB workbook holding a single 400-million-character shared string, are both refused in tens of
  milliseconds at near-zero peak heap.

Cells are read one at a time rather than a row at a time, so the column ceiling can refuse a row
before every cell in it is materialized.

## Status model

```csharp
public enum JobStatus
{
    Queued = 1, Uploading = 2, Extracting = 3, Embedding = 4, Processed = 5, Failed = 6,
    Chunking = 7, Persisting = 8,
}
```

**Values 1-6 are frozen** — deployed clients compare against them. `Chunking` and `Persisting` were
**appended** as 7 and 8 rather than inserted in pipeline order, which is why numeric order no longer
matches execution order. Never renumber; always append.

`DocumentEndpoints.MapState` projects the stage onto a four-value vocabulary:

| `JobStatus` | `state` |
| --- | --- |
| `Queued` | `Enqueued` |
| `Processed` | `Succeeded` |
| `Failed` | `Failed` |
| everything else, appended stages included | `Processing` |

The `_ => "Processing"` default is what makes appending safe: a client written before `Chunking`
existed still sees a sensible progression.

## Background jobs

A queue plus a hosted processor, replacing an earlier Hangfire dependency:
`IBackgroundJobQueue` / `BackgroundJobQueue`, `BackgroundJobProcessor` (with a `SemaphoreSlim`
concurrency cap), `IJobStatusStore` / `JobStatusStore` behind the polling endpoint, and
`IJobCancellationRegistry` / `JobCancellationRegistry` behind the cancel route below.

## Cancelling an upload

`DELETE api/documents/upload-status/{jobId}` stops a queued or running job and removes whatever it
had already produced, so one call always leaves nothing behind. It carries no permission filter —
owning the job, the same test the status read applies, is the gate — and 404s an unknown or another
user's job with the same message the status read gives, so a job id cannot be probed for existence.
It is idempotent: cancelling a job that already succeeded deletes the document it produced; one
that already failed, or was already cancelled, is left alone.

The before/after-202 split still matters, because only one side of it now involves the server at
all:

- **Before the 202** — the file is still on the wire. Aborting the HTTP request is enough, because
  the API buffers the whole upload before it enqueues anything; an aborted transfer creates no job
  to worry about.
- **After the 202** — a job now exists, and the route above stops it.

**The registry.** `JobCancellationRegistry` (a `Dictionary` under a `System.Threading.Lock`,
modelled on `ConversationLockService`) maps a job id to the `CancellationTokenSource` it runs
under. Registration happens at **enqueue**, not when the job starts: the queue is bounded, so the
enqueue call itself can block, and a caller who can already poll a queued job must also be able to
cancel it. The channel has no way to remove an item by id, so the *absence* of a registration is
the whole record of a job that was cancelled while still queued — `BackgroundJobProcessor` checks
for one before it creates a DI scope, and runs nothing if it is gone. Whoever removes an entry
under the registry's lock owns disposing it.

`BackgroundJobProcessor` runs each job under a token linking that job's own source to the host's
stopping token, and tells a caller's cancel apart from a shutdown with
`!stoppingToken.IsCancellationRequested` — both throw `OperationCanceledException` off the same
linked token, so the host token is the only thing that tells them apart.

`JobStatus.Cancelled = 9` is terminal and, like `Chunking` and `Persisting` before it, **appended**
rather than inserted in pipeline order. `DocumentEndpoints.MapState` maps it **explicitly** to
`state: "Failed"` — falling through to the `Processing` default would leave a client polling a job
that will never move again — while the free-form `message`/`status` still reads `"Cancelled"`. The
four-value client vocabulary is unchanged.

**The race that shapes the design.** A cancel can arrive while a job is mid-commit, and the
database cannot order the two: a delete issued before the insert commits matches no rows. The
linearization point is instead the `ConcurrentDictionary` inside `JobStatusStore` — `Complete` now
returns whether it won the terminal slot, and `Cancel` returns whichever snapshot ended up
installed, its own or the completion that beat it. `DocumentService.CancelUploadAsync` writes the
store **before** signalling `JobCancellationRegistry`, so the store write, not the cancellation
signal, is what decides the outcome. Exactly one party compensates in every interleaving:

- If the cancel wins the terminal slot, `Complete` returns `false` and the worker throws
  `JobCancelledException` — deliberately not an `OperationCanceledException`, since nothing was
  interrupted by the time it is thrown and the job has already undone what it persisted.
  `BackgroundJobProcessor` treats it as an ordinary end, not a failure.
- If the job had already finished, `Cancel` returns the completed snapshot — `DocumentId` and all —
  and `CancelUploadAsync` deletes those rows itself.

**Where the compensation runs.** `DocumentService` wraps each `CreateXDocumentAsync` in one
compensation boundary spanning the blob write, the persist, the parent re-check and `Complete`,
closing two leaks a cancel used to open: one during the blob upload orphaned the blob, and one
landing after `SaveChangesAsync` left the blob *and* the active rows behind. Cleanup always runs
under `CancellationToken.None` — the caller's own token is usually what is cancelling — and the
blob is deleted only once the rows are confirmed gone.

**The blob rule.** A worker path that cleans up after itself deletes the blob, because no
surviving row references it. The cancel endpoint's already-finished path does not: the row is
soft-deleted the same as any other document delete, so reclaiming its bytes stays a retention
concern rather than something a cancel does inline.

**Scale-out.** The registry is process-local, like the queue and the status store beside it — not
a new limitation, since `GET upload-status/{jobId}` already 404s from any instance that did not
accept the upload. Worth stating anyway: a distributed status store must not ship ahead of a
distributed cancel signal, or a cancel would appear to succeed while the work kept running on
another replica, which is worse than the honest 404 today's process-local pair gives.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Service/DocumentService.cs` | Sequencing, persistence, the parent re-check, `DeactivateConversationDocumentAsync`'s cascade, `CancelUploadAsync`'s race |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Extraction/` | The five extractors and the factory |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Extraction/SheetSegmentBuilder.cs` | Shared shaping and refusals for `.xlsx` and `.csv` |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Chunking/TokenTextChunker.cs` | Splitting |
| `enterprise-gpt-api/Enterprise.Gpt.Service/BackgroundJobs/` | Queue, processor, status store, cancellation registry |
| `enterprise-gpt-api/Enterprise.Gpt.Service/BackgroundJobs/JobCancellationRegistry.cs` | Per-job `CancellationTokenSource`s, registered at enqueue |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Exceptions/JobCancelledException.cs` | Signals a job that lost the terminal-slot race |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Validators/UploadedFileValidator.cs` | Extension and size validation |
| `enterprise-gpt-ui/src/app/core/documents/upload-cancel-client.ts` | Fire-and-forget `DELETE` for the composer's Stop control |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Extraction/` | Extractor and ceiling tests |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/BackgroundJobs/BackgroundJobProcessorTests.cs` | The cancel-vs-shutdown and terminal-slot-race tests |

## Related

- [retrieval.md](retrieval.md)
- [sheet-query.md](sheet-query.md)
- [downloads.md](downloads.md)
