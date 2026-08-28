# File Attachments

How the rebuilt Angular client at `enterprise-gpt-ui/` gets a file from a user's disk into a conversation the model can read, and back out again: a paperclip and a drop zone that only exist for a user who may upload, a transfer that reports its own progress because `HttpClient` cannot, a staged poll that follows ingestion to a conclusion, one chip that renders every state along the way, four distinct refusal shapes, and a download whose URL is a credential the client is careful never to keep.

Audience: a developer building the next surface that attaches files (US-905's project files panel reuses almost all of this), reviewing what the client does with a signed link, or debugging an attachment that never becomes usable. Read [Conversation Turn Lifecycle](../conversations/turn-lifecycle.md) first for `TurnStore` and the send pipeline §7 hooks into, [Authentication and Session](authentication-and-session.md) §10 for the `Upload File` gating primitives this consumes, and [Frontend Foundation](frontend-foundation.md) for the store features and `core/errors` conventions everything here composes.

The server side is the authority on everything past the request: [Document Upload and Ingestion](../documents/upload-workflow.md) and [Document Download](../documents/download-workflow.md).

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference.

## 1. Overview

Five stories landed together as EP-8, and they are one feature rather than five — every one of them is a state the same chip can be in:

| Story      | What it delivers                                                                                                                                                                             |
| ---------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **US-801** | A paperclip in the composer and drag-and-drop on the prompt box, both absent without the `Upload File` grant; files refused client-side against the deployment's own extension list           |
| **US-802** | A staged poll of the job — 500 ms → 1 s → 2 s → 4 s, held at 5 s — moving the chip from a progress bar to a spinner to **Ready to use**, and retaining the `documentId` on success           |
| **US-803** | A 404 on the status route renders a neutral **unknown** chip, never a failure: nothing went wrong, the status simply is no longer kept                                                        |
| **US-804** | A download control on a ready chip that fetches a signed link **on click**, hands it to a detached `<a download>`, and lets go of it in the same function                                     |
| **US-805** | Every refusal explained in terms the user can act on, across four distinct shapes — and a Retry on the toast that finally does something, because `ToastStore` gained a handler registry (§5.4) |

Seven decisions shape everything here, and each looks removable until you know what it prevents:

1. **Uploads start as early as a conversation id exists, and Send waits for them.** Retrieval attaches `document_search` from what the conversation _already_ holds, so a file posted as the stream opens is invisible to the very turn that asked about it. Attaching to an open conversation uploads immediately; a brand-new conversation defers until US-401's create hands over an id; `TurnStore` gates the stream on `UploadStore.settled$()` (§3.1, §7).
2. **The upload POST is raw `XMLHttpRequest`, not `HttpClient`.** `HttpEventType.UploadProgress` is emitted by `HttpXhrBackend` only, and this app runs `provideHttpClient(withFetch())` — so frame `4l`'s percentage during byte transfer is unobtainable through `HttpClient` here. Two interceptor behaviours are restated by hand, and exactly two (§3.2).
3. **Concurrency is capped at three transfers.** An HTTP/1.1 origin allows six connections, and the SSE stream and the status polls draw on the same budget. Saturating it queues the polls behind the transfers and every chip looks frozen (§3.3).
4. **A 404 on the status route is `unknown`, not `failed`.** The server deliberately makes expired, evicted and another user's job indistinguishable, so the client does too — and says so in words that claim nothing (§3.5).
5. **The signed download URL never lands anywhere.** Not in store state, not in `localStorage`, not in a router URL, not in a log. It is fetched at the click and gone when the function returns (§6).
6. **`failed` and `unsupported` are separate chip states that look alike.** Only `failed` offers Retry, because re-posting a `.mov` produces the same refusal — the reasoning `canRetry` in `core/errors` already records. This is a deliberate departure from frame `4l` (§8).
7. **The supported set is built from the endpoint, not from the board.** Frame `4l`'s example copy names XLSX and CSV — both accepted today — and omits PPTX, which is also accepted. Nothing in the app quotes the board's string; it reads the deployment's actual list instead (§8).

### 1.1 Where each piece lives

| Concern                                                                | Where                                                                                                                                                                                                                                                             |
| ---------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| The wire shapes — `JobDto`, `JobStatusDto`, `DocumentDownloadDto`, `UploadTarget` | [`domain/api/document.ts`](../../enterprise-gpt-ui/src/app/domain/api/document.ts) — framework-free                                                                                                                                                          |
| The poll schedule and the sub-status line                              | [`domain/documents/upload-schedule.ts`](../../enterprise-gpt-ui/src/app/domain/documents/upload-schedule.ts) — framework-free                                                                                                                                     |
| `formatBytes`, shared by the chip and the too-large toast              | [`domain/format/bytes.ts`](../../enterprise-gpt-ui/src/app/domain/format/bytes.ts) — framework-free                                                                                                                                                                |
| The deployment's readable extensions, and `isSupported`                | [`core/documents/supported-extensions-store.ts`](../../enterprise-gpt-ui/src/app/core/documents/supported-extensions-store.ts)                                                                                                                                     |
| The upload transport, and its `XMLHttpRequest` factory token           | [`core/documents/document-upload-client.ts`](../../enterprise-gpt-ui/src/app/core/documents/document-upload-client.ts), [`upload-xhr.token.ts`](../../enterprise-gpt-ui/src/app/core/documents/upload-xhr.token.ts)                                               |
| The staged poll                                                        | [`core/documents/upload-status-client.ts`](../../enterprise-gpt-ui/src/app/core/documents/upload-status-client.ts)                                                                                                                                                 |
| The four refusal shapes and their copy                                 | [`core/documents/upload-rejection.ts`](../../enterprise-gpt-ui/src/app/core/documents/upload-rejection.ts)                                                                                                                                                         |
| The signed link, and everything the client refuses to do with it       | [`core/documents/document-download-store.ts`](../../enterprise-gpt-ui/src/app/core/documents/document-download-store.ts)                                                                                                                                           |
| Attachment state, the concurrency budget, the settle gate              | [`features/chat/upload-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/upload-store.ts)                                                                                                                                                                   |
| The paperclip, the picker, the drop zone, the composer's notes         | [`features/chat/composer/composer.ts`](../../enterprise-gpt-ui/src/app/features/chat/composer/composer.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/chat/composer/composer.html)                                                                       |
| The chip and its state union                                           | [`shared/chip/attachment-chip/attachment-chip.ts`](../../enterprise-gpt-ui/src/app/shared/chip/attachment-chip/attachment-chip.ts), [`attachment.model.ts`](../../enterprise-gpt-ui/src/app/shared/chip/attachment-chip/attachment.model.ts)                     |
| The drop zone directive and frame `2a`'s overlay (both pre-existing, from US-204) | [`shared/upload/file-drop-target.ts`](../../enterprise-gpt-ui/src/app/shared/upload/file-drop-target.ts), [`drop-overlay.ts`](../../enterprise-gpt-ui/src/app/shared/upload/drop-overlay.ts)                                                     |
| The toast retry registry                                               | [`core/notifications/toast-store.ts`](../../enterprise-gpt-ui/src/app/core/notifications/toast-store.ts)                                                                                                                                                           |
| The send gate                                                          | [`features/chat/turn-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/turn-store.ts)                                                                                                                                                                       |
| A drivable `XMLHttpRequest` for specs                                  | [`src/testing/upload-xhr.ts`](../../enterprise-gpt-ui/src/testing/upload-xhr.ts)                                                                                                                                                                                   |

## 2. Quick start

### 2.1 Attaching files from a surface that is not the composer

`UploadStore` is **component-scoped** — provide it on the screen that owns the draft, tell it what the files hang off, and hand it `File` objects:

```ts
@Component({
  providers: [UploadStore],
  // …
})
export class ProjectFiles {
  protected readonly uploads = inject(UploadStore);
}
```

```html
<app-attachment-chip
  [attachment]="attachment"
  [removeAction]="attachment.removeAction"
  [downloadable]="attachment.downloadable"
  [downloading]="attachment.downloading"
  (removed)="uploads.remove($event)"
  (retried)="uploads.retry($event)"
  (downloaded)="uploads.download($event)"
/>
```

Root scope would be wrong twice over: the chips belong to one draft on one screen, and US-905's project files panel needs a second, independently-targeted instance beside the chat one.

The `attachments` computed already answers every per-row question the template would otherwise ask — bind it and pass the row through. Do **not** write a `removeActionFor(id)` method for the template to call: it runs an `Array.find` per chip on every change-detection pass, and a progress event triggers one, which is O(n²) on a surface whose whole purpose is to update continuously.

### 2.2 Turning a surface into a drop zone

Two lines, and both were already built by US-204:

```html
<div
  [appFileDropTarget]="session.canUploadFiles() && !turn.inFlight()"
  (filesDropped)="uploads.attach($event)"
  #drop="appFileDropTarget"
>
  @if (drop.isDragOver()) { <app-drop-overlay /> }
</div>
```

The permission is an **input, not an injected `SessionStore` read** — that is the directive's own decision, so the authorization check stays visible at the call site. Bind the in-flight condition too, or a drop can do what the paperclip cannot.

### 2.3 Downloading a document from anywhere

`DocumentDownloadStore` is root-scoped and needs nothing but the parent and the ids:

```ts
downloads.download({ kind: 'conversation', id: conversationId }, documentId, fileName);
```

Call it from a **click handler and never from a render**. Every call issues a fresh five-minute credential; a list that prefetched forty of them would have forty live credentials in flight for a user who wanted one file.

### 2.4 Rules that are not style preferences

- **Never route the upload POST through `HttpClient`.** The progress events do not exist on the fetch backend (§3.2).
- **Never restate `retryInterceptor` on the upload path.** It replays idempotent GETs; a replayed upload queues the file twice.
- **Never persist, log, or navigate to `downloadUrl`.** It is a bearer credential (§6.1).
- **Never hard-code the accepted extension list.** Read `SupportedExtensionsStore`; the deployment derives it from its registered extractors (§3.6).
- **Never latch `completedUnits`/`totalUnits` across polls.** Any report that does not supply them clears them, so a latched pair shows a stale "3 of 12" through a stage that is not counting anything.

## 3. The upload lifecycle, end to end

One file's journey, with the state each step leaves on the chip:

| Step                                          | Chip state                        | Where                                                     |
| --------------------------------------------- | --------------------------------- | --------------------------------------------------------- |
| Picked or dropped, extension checked locally  | `uploading 0 %` or `unsupported`  | `UploadStore.attach`                                       |
| Queued behind the concurrency cap             | `uploading 0 %`                   | `UploadStore.pump`                                         |
| Bytes on the wire                             | `uploading n %`                   | `DocumentUploadClient.upload`                              |
| 202 received, job id retained                 | `processing` — "Waiting to start" | `UploadStore.begin`                                        |
| Polled to a conclusion                        | `processing` → `ready` / `failed` | `UploadStatusClient.follow`                                |
| Status no longer kept                         | `unknown`                         | `UploadStatusClient` 404 arm (§3.5)                        |

### 3.1 Uploads start as early as a conversation id exists

This is the decision the whole epic hangs off, and it is not about latency. Retrieval reaches an attached document through a tool call, and the `document_search` tool is attached to a turn based on what the conversation **already holds** — so a file posted at the same moment the stream opens is invisible to the very turn that asked about it. The user would see their file on screen and get an answer that ignored it.

So the store uploads as early as it can:

- **An open conversation** already has an id, so `attach` starts the transfer immediately.
- **A brand-new conversation** has none. The files sit with their chips showing `uploading 0 %` until US-401's create returns, at which point `TurnStore` calls `bindConversation(created.id)` **synchronously** — before navigating — and the deferred queue drains. `Chat`'s route effect calls the same method with the same id a change-detection pass later, where it is a no-op.
- **Send then waits** for everything to settle (§7).

`bindConversation` is idempotent for the same id because it has two callers. A **different** id is a different draft, so its chips are cleared: they describe files that belong to the conversation the user just left.

The dependency runs one way only. `TurnStore` injects `UploadStore`; the upload store learns its conversation by being told. Injecting `TurnStore` back for the id would be a DI cycle.

### 3.2 Why the POST is raw `XMLHttpRequest`

`HttpEventType.UploadProgress` is emitted by `HttpXhrBackend` **only**, and this application configures `provideHttpClient(withFetch())` — Angular's fetch backend reports download progress and nothing else. Frame `4l`'s percentage during byte transfer is therefore unobtainable through `HttpClient` here, and a 50 MB file on a slow link would upload with no feedback at all. So the one request that needs upload progress goes over `XMLHttpRequest` directly, exactly as the chat stream goes over raw `fetch` for the same class of reason.

Being off the `HttpClient` pipeline means interceptor behaviour is restated by hand, and the interesting part is **what is not restated**:

| Interceptor behaviour | Restated? | Why                                                                                                                                                                            |
| --------------------- | --------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Bearer token          | Yes       | From `TokenService`, the one source both transports share                                                                                                                      |
| Single forced-refresh replay on a 401 | Yes | Gated by `authErrorDecision`, the same predicate `authInterceptor` consults — which is what structurally keeps a 403 `permission-required` out of the refresh path            |
| `retryInterceptor`    | **No**    | It replays idempotent GETs only. A replayed upload would queue the file **twice**, and the user would see one chip and get two documents                                        |

Four details in the transport are load-bearing and easy to undo:

- **`Content-Type` is deliberately unset.** The browser has to append the multipart boundary itself; setting the header by hand produces a body no parser can read.
- **The file name is passed explicitly** — `body.append('file', file, file.name)`. `FormData.append(field, blob)` without a name sends `"blob"`, which passes the server's 256-character name rule and which the user never recognises in a documents list.
- **A transport failure is rejected as a `TypeError`**, which is what `fetch` reports one as — so both transports classify a dropped connection identically through `toAppError`.
- **Unsubscribing aborts the request**, and the abort surfaces as a `DOMException` named `AbortError`, which is how `toAppError` tells a user's cancel apart from a failure. Removing a chip mid-transfer cancels it rather than orphaning it.

A 202 with no job id is reported as a **failure**, which is the honest reading: nothing can poll it, so nothing can ever say the document is ready.

The factory behind the request is a DI token, `UPLOAD_XHR`, for the same reason `STREAM_FETCH` is one — specs substitute a fake through a provider override rather than monkey-patching a global that then leaks between them. A factory rather than an instance, because an `XMLHttpRequest` is single-use and the 401 replay needs a second one.

### 3.3 Three at a time

`MAX_CONCURRENT_UPLOADS` is 3. The picker takes `multiple` and a drop can carry a folder's worth, so "start them all" is a real risk: an HTTP/1.1 origin allows six connections in total, and the SSE stream and the status polls draw on the same budget. Fifty concurrent transfers queue every status read behind them and make every chip look frozen, while competing with the turn the user is waiting on.

Two subtleties in the queue:

- **The budget spans transfer _and_ ingest.** A file keeps its slot past the 202, until its job reaches a terminal state. Freeing it at the 202 is the obvious alternative and the wrong one: ingestion is the longer phase, so an early release caps the transfers at three and then lets an unbounded number of poll loops accumulate behind them — the same saturation the cap exists to prevent, arriving by a different route.
- **`_files`, `_subscriptions`, `_active` and `_waiting` live in `withProps`, not in state.** A `Map` in state is either mutated — which produces no signal write, so nothing re-renders — or replaced, which re-renders every chip on each byte of progress. `ToastStore` holds its timers the same way and for the same reason.

### 3.4 Watching the job (US-802)

`UploadStatusClient.follow(jobId)` polls `GET api/documents/upload-status/{jobId}` on a staged schedule and emits every poll, completing on the first terminal one — so a consumer renders each step and needs no stop condition of its own.

```
500 ms → 1 s → 2 s → 4 s → 5 s → 5 s → …
```

`pollDelayMs(attempt)` is a **pure function of the attempt index**, not a stateful generator, so the whole sequence is provable in one assertion with no fake timers. A fixed interval is what it replaces: 500 ms forever hammers the API through a 20-minute OCR job, and 5 s forever makes a 300 ms text file look slow. The cap is 5 s rather than 8 s because the ceiling is also the worst case for how stale "Ready to use" can be.

Nothing in the client retries a poll: `GET` is idempotent, so `retryInterceptor` already replays a 502/503/504 with jittered backoff underneath it.

Three things about `JobStatusDto` are easy to get wrong, and the type's own documentation says so:

- **`state` and `status` do not use the same words.** `state` is the coarse four-value projection; `status` is the fine-grained `JobStatus` member name. `state: 'Succeeded'` pairs with `status: 'Processed'`. **Branch on `state`; render `status` only as prose.** Every stage that is neither queued nor finished maps to `Processing`, which is what lets the server add stages without breaking a client written today.
- **`completedUnits`/`totalUnits` are cleared by any report that does not supply them.** `subStatusOf` reads them fresh on every poll for exactly that reason.
- **`progress` is already monotonic server-side**, and a failure leaves it where it stopped.

`subStatusOf` prefers the server's `message` over the raw counts and does not append to it: the message already reads as a sentence and already contains the numbers where they exist — "Embedded 12 of 24 chunk(s)" — so composing a second "12 of 24" onto it would say the same thing twice. The counts are the fallback for a stage that reports numbers with no words; `Processing…` is the fallback for a stage that reports neither.

On success the `documentId` is retained on the item. That is the only way to learn it, and it is what makes US-804's download a click rather than a lookup.

### 3.5 Expired is not failed (US-803)

A 404 on the status route means the job is expired, evicted by a restart, **or someone else's** — and the server makes those indistinguishable on purpose, so a job id is never confirmed as valid. The client mirrors that exactly: `JobPoll` has an `expired` arm carrying no detail, and the chip renders `unknown`:

> **Status is only kept for a limited time.**

That is not a hedge, it is the accurate statement. The document may well be perfectly usable; only the status record is gone. The chip uses a muted question-mark glyph rather than the red cross, and offers **no Retry** — re-uploading a file that probably succeeded is the wrong suggestion.

A poll that fails for any *other* reason takes the same `unknown` state plus an error toast, and for the same reason: the file may have been ingested, and only the status *read* failed. Claiming a failure there would be inventing one.

### 3.6 The accepted extension list comes from the deployment

`SupportedExtensionsStore` reads `GET api/documents/file-extensions`, which the server derives from its **registered extractors** rather than from configuration — so it cannot advertise a format nothing can parse. Today that is `.csv`, `.doc`, `.docx`, `.md`, `.pdf`, `.pptx`, `.txt`, `.xlsx`.

Three behaviours worth knowing:

- **It is loaded lazily and only for a user who can upload.** The composer runs an effect gated on `session.canUploadFiles()`, so a user who will never see the paperclip never spends a request on what it would have offered. An effect rather than a bare call, because the grant can flip mid-session.
- **Memoized on success only.** A failure would otherwise make one transient blip last the whole session; a later composer simply asks again.
- **A failed load does not block uploading.** `isSupported` answers `true` when the list is unknown, so the file goes to the server and is refused there with a real reason. Refusing everything locally because a side request failed would be worse than the round trip it saves. The same rule drives the picker: `accept` is bound to `extensions.accept() || null`, and a `null` `accept` means "any file".

The store is root-scoped because the list is deployment-shaped rather than user-shaped, but a sign-out still clears it — the next user's deployment is only *usually* the same one, and a stale list would be silently wrong rather than visibly so. Its memo lives in `withProps`, where `withResetOnSignOut` cannot reach it, so an `onInit` subscription clears that by hand.

`extensionOf` treats a leading-dot file (`.gitignore`) as having **no** extension, which matches the server's own `FileDto.FileExtension` parse.

## 4. The chip state machine

`AttachmentState` is a discriminated union rather than a bag of booleans, so `@switch` over `kind` is exhaustive and "uploading *and* expired" is unrepresentable — the same modelling discipline `withRequestStatus` established for request state, and the same reason: the old client's upload chips tracked flags independently and got it wrong.

| State         | Carries          | Renders                                    | Retry | Download |
| ------------- | ---------------- | ------------------------------------------ | ----- | -------- |
| `uploading`   | `percent`        | Determinate progress bar                   | —     | —        |
| `processing`  | `subStatus`      | Spinner + the server's own sub-status line | —     | —        |
| `ready`       | —                | Green check, "Ready to use"                | —     | ✅       |
| `failed`      | `reason`         | Red cross + the job's sanitized message    | ✅    | —        |
| `unsupported` | `reason`         | Red cross + the extension and what works   | —     | —        |
| `unknown`     | —                | Muted `?`, "Status is only kept…"          | —     | —        |

`failed` and `unsupported` render alike and are deliberately separate arms. One is a job the pipeline could not finish, carrying the server's sanitized `errorMessage`; the other is a file this deployment cannot read at all, refused before any request was made. Only the first offers Retry (§8).

**Every transition replaces the item rather than mutating it.** `provideCheckNoChangesConfig({ exhaustive: true })` turns an in-place edit into a failing test rather than a chip that silently stops updating, which is the failure US-801's last acceptance criterion names.

**Progress is clamped upward.** The server's own progress is monotonic and the chip's must be too, so a re-reported `loaded` can never make the bar retreat and read as the upload restarting.

**The chip is never blank between the 202 and the first poll.** The store seeds `processing` with "Waiting to start", which is the message the server seeds every job with anyway — so it is what a first poll would have shown.

### 4.1 Remove means two different things

Once a document is ingested it belongs to the conversation, and **there is no API to detach it** — only project documents have a `DELETE`. So the control's accessible name changes with what it can actually do:

| `removeAction` | When                                          | Accessible name    | Effect                              |
| -------------- | --------------------------------------------- | ------------------ | ----------------------------------- |
| `cancel`       | No `documentId` yet — transferring or queued  | "Remove {file}"    | Aborts the transfer, drops the chip |
| `dismiss`      | The job succeeded and a `documentId` exists   | "Dismiss {file}"   | Drops the chip only                 |

Naming it "Remove" at that point would claim an effect it does not have. This is a known backend gap, recorded in §10.

### 4.2 What a screen reader is told

A chip **appearing** is announced to nobody by the chip itself: a live region inserted together with its content is not reliably read out, so a `role="status"` on the chip's own reason line would be coverage that is not there. The composer therefore renders one visually-hidden `role="status"` summary that exists from first render, and the chip carries none:

> 3 files attached, 1 ready, 1 could not be used

A summary rather than per-file progress, and deliberately free of the transfer percentage — a live region that re-reads on every progress event is unusable.

The chips are a `role="list"` with an `aria-label`, because `list-style: none` strips list semantics in Safari, and the list semantics are what make the count announceable.

Frame `2a`'s drop overlay is `aria-hidden`, and deliberately: a drag is a pointer gesture with no keyboard equivalent, so there is no assistive-technology user for whom it is ever on screen. The accessible path to the same outcome is the paperclip.

### 4.3 The composer's note line

The composer shows at most one note, in a fixed precedence, and EP-8 added two rungs to it:

1. The model catalog failed and nothing is selected
2. The conversation's last model is unavailable
3. **"This model can't read attachments"** — files are attached and the selected model cannot call tools
4. The MCP selection was cleared by a model change
5. **"Preparing N files — Send will be available once they're ready."**
6. The microphone was blocked

Rung 3 sits near the top because a model that cannot call tools makes the attachment pointless *and* the answer quietly ungrounded — retrieval is a tool call, so the model never sees the file. `toolsDisabled` is the same computed the MCP picker is gated on, for the same underlying reason.

Rung 5 sits below the selection warnings and above the microphone: it explains why Send is disabled, which the disabled control has already half-said. Its wording describes the disabled control rather than promising an auto-send — nothing sends when the files settle, the user still presses Send.

## 5. Refusals: four shapes, not two (US-805)

`describeUploadRejection(error, file)` turns a normalized `AppError` into the title, detail, trace line and retryability a toast needs. It is deliberately richer than `userMessage` for the size case, because the acceptance criterion and the board both quote *both* numbers and only the caller knows the second one.

### 5.1 The client-side refusal

An unsupported extension never becomes a request at all. `attach` checks the name against `SupportedExtensionsStore` and writes the `unsupported` state directly, naming the extension and the supported set:

> `.mov` isn’t supported — PDF, DOCX, MD, PPTX, TXT

A file with no extension reads "Files need a recognised extension". When the list has not loaded, the reason degrades to just the refusal — but that path is unreachable in practice, because an unknown list means `isSupported` answers `true` and the file goes to the server.

### 5.2 The four server shapes

The story names two; the API produces four, and all four are handled:

| Shape                                   | Where it comes from                                                                    | What the user reads                                                                    | Retry |
| --------------------------------------- | -------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------- | ----- |
| `upload-too-large` **with** `maxBytes`  | `MaxUploadSizeEndpointFilter`, before a byte is buffered                                | "Files can be up to 50 MB — this one is 212 MB."                                       | ✅    |
| A bare **413** with no `maxBytes`       | Kestrel, when a chunked upload trips a body-size limit mid-request                      | "This file is 212 MB, which is more than this deployment accepts."                     | ✅    |
| `permission-required` (+ `permissions`) | `PermissionEndpointFilter`, a 403                                                       | "Your account is missing the “Upload File” permission — ask an administrator."         | —     |
| `validation-error` (+ `errors`)         | `UploadedFileValidator` — empty file, over-long name, magic bytes contradicting the extension | The **server's own message**, which names the actual problem                       | —     |

The `validation-error` arm takes the server's words rather than `userMessage`'s, and that is the point of it: `UploadedFileValidator` writes its messages for users, where `userMessage` says "correct the highlighted fields" — pointing at fields this surface does not have.

Both numbers in the size copy go through the shared `formatBytes` in `domain/format/`, which is why it moved there out of `core/errors`. Two surfaces have to agree digit for digit — the chip's size label and the toast complaining about it — and a second implementation that rounded differently would make the toast contradict the chip. It is MB/KB only, matching the server's own `FormatSize`: the upload cap is capped at 500 MB, so a GB branch is unreachable and would only be a place for the two sides to drift.

### 5.3 Where Retry is offered, and where it is not

`RETRYABLE_REJECTIONS` is a `satisfies Record<AppErrorKind, boolean>` table, so a new arm cannot be added to `AppError` without deciding what it means for an upload. The principle: **a grant is acquired from an administrator, not by asking again**, and a file the validator rejected is wrong in a way a second identical POST cannot fix. A size failure *is* retryable, because the cap is a deployment setting and the bare-413 variant is a property of the request rather than of the bytes.

Frame `4l` draws its permission toast with no Retry for exactly this reason, and its size toast with one. The chip follows the same rule one level down: Retry rides the `failed` arm and not the `unsupported` one (§8).

`retry(id)` clears the item's `jobId` and `documentId` and re-queues the file from the handle the store kept — a genuinely new upload under a new job, not a replay of the old request.

### 5.4 A Retry that does something: the toast handler registry

`ToastStore` previously took a `retryLabel` and emitted a `retried` event nothing listened to. EP-8 closed that loop:

- `ToastInput` gained `onRetry?: () => void`, and **a label with no handler renders nothing**: `retryLabel: input.onRetry ? (input.retryLabel ?? 'Retry') : null`. The affordance and the action are one decision, and letting them come apart is how a dead Retry ships.
- Handlers live in a `Map` in `withProps`, beside the timers and for the same reason — a function is not serializable state, nothing renders from it, and `withResetOnSignOut` would clone-compare it pointlessly.
- `retry(id)` **dismisses first, then calls**, so the handler is free to raise a fresh toast for the second attempt without the failed one lingering beneath it.
- `App` wires `<app-toast-region (retried)="toasts.retry($event)" />`. The region reports which toast; the store owns what that means.
- Handlers are cleared on dismiss, on sign-out and on destroy. A retry closure captures the previous user's request and must not survive into the next session any more than the toast it belonged to does.

`fromError(error, onRetry?)` replaced `fromError(error, retryLabel?)`. No caller passed the old parameter, so nothing changed behaviour — but the new signature makes the dead-button state unrepresentable.

One consequence is easy to miss and `UploadStore` handles it explicitly: an upload's Retry re-posts **the file the store still holds**, and error toasts never self-dismiss while `ToastStore` is root-scoped. A toast whose chip has been removed — or whose whole chat screen has gone — would therefore outlive the bytes its Retry needs, and pressing it would silently do nothing. So `_fileToasts` maps each attachment to the toast it currently owns, and the toast is withdrawn whenever its file is: on `remove`, on a conversation change, and on destroy.

## 6. Download (US-804)

### 6.1 The URL is a credential, and the store is shaped around that

`GET api/documents/{conversations|projects}/{parentId}/{documentId}` answers with a `downloadUrl` that is a **bearer credential for roughly five minutes**: anyone holding it reads the file with no sign-in, and [an issued link cannot be withdrawn](../documents/download-workflow.md#54-not-handled-revocation). Four rules follow, and every one of them is a thing the store deliberately does not do:

| Rule                             | Why                                                                                                                                        |
| -------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------- |
| **Nothing is prefetched**        | A list of forty documents issues zero requests until one is clicked. Prefetching would put forty live credentials in flight for a user who wanted one file |
| **The URL never lands anywhere** | Not in store state, `localStorage`, `sessionStorage`, a router URL, or a log. It exists inside `save()` and is gone when that returns       |
| **The link is followed, not fetched** | It carries its own SAS credential and must not receive the app's `Authorization` header, so it deliberately does not go through `HttpClient` |
| **`mergeMap`, not `switchMap`**  | Two downloads are two files. The second must not cancel the first the way a second search supersedes the first                              |

`withPendingIds` keyed by document id is what lets one row spin without disabling the other thirty-nine.

### 6.2 A detached anchor, not a navigation

The [download workflow](../documents/download-workflow.md#6-client-integration) prescribes "navigate to `downloadUrl`; do not fetch it", and the client follows the second half exactly. It does **not** navigate:

```ts
// A direct `href` assignment bypasses Angular's DomSanitizer, so the scheme is
// checked first: the value is server-issued, which makes this defence in depth
// rather than a live hole — and the same standard the markdown pipeline holds.
const parsed = new URL(download.downloadUrl, document.baseURI);
if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') {
  return false;
}

const anchor = document.createElement('a');
anchor.href = parsed.href;
anchor.download = download.fileName;
anchor.rel = 'noopener';
document.body.append(anchor);
anchor.click();
anchor.remove();
```

A navigation to a `Content-Disposition: attachment` URL works, and it also puts the signed URL **in the address bar** for the instant before the browser cancels it, and in session history afterwards — which is exactly what "never placed in a router URL" exists to prevent. A programmatic click on a detached anchor initiates the same download with the same absence of an `Authorization` header and the same absence of a CORS preflight, and leaves nothing behind.

The `download` attribute is set for completeness and is ignored cross-origin, which costs nothing: the route already signs `Content-Disposition`, so the browser saves the right file name either way.

### 6.3 Failure copy names nothing it cannot know

| Status                          | What the toast says                                                                            | Why                                                                                                                            |
| ------------------------------- | ---------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| 404 `resource-not-found`        | "{file} isn't available" / "This document can no longer be retrieved."                          | **Never "deleted".** A 404 also covers a document belonging to someone else, and asserting deletion would leak which of the two it was |
| 503 `storage-not-configured`    | "Downloads aren't available in this deployment" / "Document storage is not configured. Ask an administrator." | A deployment fact, not a failure of this file — and not retryable, which is why `retryInterceptor` leaves an app-typed 503 alone too |
| Anything else                   | `ToastStore.fromError`                                                                          | The normal path                                                                                                                |

## 7. Send waits for the files

`TurnStore`'s pipeline gained one `switchMap` between "the conversation exists" and "open the stream":

```ts
switchMap((conversationId) => {
  store._awaitingUploads.value = true;

  return store._uploads.settled$().pipe(
    takeUntil(merge(store._signedOut$, store._stop$)),
    take(1),
    map(() => conversationId),
    finalize(() => (store._awaitingUploads.value = false)),
  );
}),
```

`settled$()` completes **synchronously** when nothing is outstanding, so a turn with no attachments costs exactly what it did before EP-8.

Four details make this safe rather than a place for a turn to get stuck:

- **A refused or failed file counts as settled.** It will not change again, and holding Send hostage to it would leave the user with no way to send at all.
- **`blocksSend` is false until a target exists.** Before that the files are deferred *because* no conversation exists, and Send is the thing that creates one — blocking it would deadlock the first turn of every new conversation against its own attachments.
- **Cancelling completes the inner observable without emitting**, so the whole pipeline completes and `exhaustMap` frees its slot for the next send. Emitting anyway would open a stream nobody wants and hold the slot for as long as that took to fail.
- **The gate is a second pre-stream window, and `stop()` settles it like the first.** `phase === 'creating'` and `_awaitingUploads` both mean nothing has streamed: there is no partial output to card, and the prompt goes straight back to the composer. An abort would have nothing to reach. `bindRoute`'s reset fires `_stop$` for the same reason — aborting alone would leave an orphaned wait holding the `exhaustMap` slot and silently drop every later send.

`clearAll()` in `UploadStore` emits on `_settled$` even though the list is empty, and that is the other half of the same guard: a `TurnStore` gate already waiting when the user moves to another conversation would otherwise never be released.

## 8. Two deliberate departures from the design boards

[`docs/design/`](../design/) is the visual authority and §9 of the [design system](design-system.md) gives the board the last word on everything but accessibility. EP-8 departs from frame `4l` twice, and both are recorded here because a departure nobody wrote down is drift.

**1. The supported-formats copy comes from the API, not from the board.** Frame `4l`'s example chip reads `.mov isn’t supported — PDF, DOCX, XLSX, CSV, MD, TXT`. At the time this departure was written, the API accepted neither XLSX nor CSV, and did accept PPTX, which the board omits — rendering the board's string would have told a user that a spreadsheet would have worked. The app builds the string from `GET api/documents/file-extensions` instead, so it is right in every deployment including ones with different extractors registered, and needed no change when the API started accepting `.xlsx` and `.csv`: the board and the API now agree on those two, and PPTX is still the one accepted format the board's example string omits. (The chip's *glyph* table stays wider than the accepted set on purpose — a `.xlsx` refused as unsupported should still look like a spreadsheet while it says so; that fallback path now fires only for `.xls`, the one spreadsheet extension the table still glyphs but the API still refuses. `.xlsm` has no glyph entry of its own and draws the generic file icon.)

**2. The `unsupported` chip carries no Retry, where the board draws one.** Re-posting a `.mov` produces the same refusal, so the control would be a button that cannot succeed — the reasoning `canRetry` in `core/errors` already records for `permission-required` and friends. US-802 ties Retry to `Failed`, and US-805 asks only that the unsupported chip name the extension and the supported set, so neither story's criteria are weakened. Retry stays on the `failed` arm, where a second attempt genuinely can succeed.

One addition rather than a departure: `bi-file-earmark-ppt` is the 75th entry in the icon manifest, added because the boards never drew a PPTX chip.

## 9. Bundle cost

Nothing EP-8 added is in the initial graph. `UploadStore` is provided by `Chat`, and the `core/documents/` code is reachable only from the chat chunk, so the whole feature rides the lazy chat route:

|                    | Initial raw   | Initial transfer | Budget (warn / error) |
| ------------------ | ------------- | ---------------- | --------------------- |
| After EP-7         | 663.45 kB     | 163.11 kB        | 670 kB / 720 kB       |
| **After EP-8**     | **663.92 kB** | **163.32 kB**    | 670 kB / 720 kB       |

0.47 kB of initial growth, which is `ToastStore`'s retry registry and the glue around it — the only EP-8 code the root injector reaches. No re-baseline, no threshold change, and **no new dependency of any kind**: the transport is `XMLHttpRequest`, the poll is `HttpClient`, and the download is an anchor element.

## 10. Deliberately not here

- **Detaching a document from a conversation.** There is **no `DELETE` route** for a conversation document; only project documents have one. Once a job succeeds, dismissing the chip hides it and nothing more, and the control renames itself to say so (§4.1). Closing this needs a backend enabler, and it is recorded in the [build order](../prd/enterprise-ui-rebuild-build-order.md)'s interim-behaviours table.
- **Dropping a folder.** `FileDropTarget` emits nothing for a drop whose `dataTransfer.files` is empty, which is what several browsers report for a directory; reading one needs `webkitGetAsEntry()`. Emitting nothing is a visible no-op the user can retry, where emitting `[]` would be a silent one.
- **A conversation's existing documents.** The chips describe *this draft's* attachments. Listing what a conversation — or the user — already holds shipped with US-1001/US-1002: `GET api/conversations/{id}/documents` and the `/documents` library over `GET api/documents` ([Documents Library](documents-library.md)). The chips deliberately stay draft-only.
- **A project's files panel.** US-905, which provides a second `UploadStore` with `{ kind: 'project', id }` and reuses everything in `core/documents/` unchanged — which is why `UploadTarget` is a discriminated pair rather than a bare id.
- **Downloading the conversation itself.** US-1502, whose control takes the composer's `composer__aux` class and inherits the in-flight dimming for free.

## 11. Troubleshooting

| Symptom                                                              | Cause                                                                                                                                                                                          |
| -------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| No paperclip in the composer                                         | The user lacks the `Upload File` grant. It is **absent, not disabled** — frame `2h`. `Administrator` implies nothing here (§2.2)                                                                |
| The progress bar never moves off 0 %                                 | Something routed the POST through `HttpClient`. The fetch backend emits no upload progress; the transport must stay on `XMLHttpRequest` (§3.2)                                                  |
| One file uploaded twice                                              | `retryInterceptor` was restated on the upload path. It replays idempotent GETs only (§3.2)                                                                                                       |
| The server received a file named `blob`                              | `FormData.append` was called without the third argument (§3.2)                                                                                                                                  |
| The multipart body is unparseable                                    | `Content-Type` was set by hand, so the browser could not append its boundary (§3.2)                                                                                                              |
| Every chip freezes while several files upload                        | The concurrency cap was raised or removed: the status polls are queued behind the transfers on a saturated origin (§3.3)                                                                        |
| The sub-status shows a stale "3 of 12"                               | `completedUnits`/`totalUnits` were latched across polls. Any report that omits them clears them (§3.4)                                                                                           |
| The chip reads "Failed" where the job was fine                       | The 404 arm is being treated as an error. Expired, evicted and another user's job are one indistinguishable case, and it is `unknown` (§3.5)                                                     |
| A chip stopped updating and nothing failed                           | An item was mutated in place instead of replaced. `provideCheckNoChangesConfig({ exhaustive: true })` catches this in a spec (§4)                                                               |
| The progress bar went backwards                                      | The upward clamp in the progress handler was removed (§4)                                                                                                                                       |
| Send stays disabled forever                                          | An item is stuck in `uploading` or `processing`. Every terminal state — including `failed`, `unsupported` and `unknown` — counts as settled; a new non-terminal state must be added to `isTerminal` (§7) |
| Send does nothing after the user navigated mid-turn                  | The upload gate held the `exhaustMap` slot. `bindRoute`'s reset must fire `_stop$`, not just abort — there is no stream for an abort to reach (§7)                                              |
| A toast's Retry does nothing                                         | `retryLabel` was passed without `onRetry`. The store refuses to render a label with no handler, so this now cannot happen through `show` (§5.4)                                                  |
| The signed URL appeared in the address bar or history                | Someone replaced the detached anchor with `location.href` (§6.2)                                                                                                                                |
| A download said the file was "deleted"                               | The 404 copy was changed. The same 404 covers another user's document, and naming deletion leaks which case it was (§6.3)                                                                        |
| The picker offers formats the server rejects                         | The extension list was hard-coded instead of read from `GET api/documents/file-extensions` (§3.6, §8)                                                                                            |
| Every file is refused locally right after sign-in                    | `isSupported` was changed to answer `false` on an unknown list. It must answer `true`, so a failed side request costs a round trip rather than the whole feature (§3.6)                          |
