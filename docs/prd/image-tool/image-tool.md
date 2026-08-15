# PRD: Image Tool

## 1. Overview

**Problem.** Enterprise GPT handles exactly one kind of content: text it can read. `Enterprise.Gpt.Dto/Enums/FileExtensions.cs` holds 26 values and **not one of them is an image** — no `png`, no `jpg`, no `jpeg`, no `webp` — and `Service/Validators/UploadedFileValidator.cs` admits a file only when `IDocumentTextExtractorFactory.IsSupported(extension)` returns true, so the upload surface is derived end to end from what something can extract *text* from. `DocumentEndpoints.GetFileExtensions` advertises that same extractor-derived list, and the client's root-scoped `SupportedExtensionsStore` reads it to decide what the picker will even offer. A user therefore cannot put a picture into this platform, and the assistant cannot produce one: asked for a chart, a diagram or a mock-up, it writes a paragraph describing the image it cannot make. Meanwhile `docs/prd/file-agent/file-agent.md` §6 treats image generation as a hard prerequisite it cannot satisfy for itself — the Code Interpreter sandbox has no outbound network access, so it can neither call an image API nor fetch a picture — which means the File Agent is blocked on a capability nobody has scheduled.

**Solution.** One tracked function the main assistant calls, that generates images from text, generates images from one or more **source images** (a picture the user attached, or one the tool made earlier), does either in **batches** in a single call, persists what it makes as `ConversationDocument` rows that are structurally unreachable from retrieval, and renders the results **inline in the transcript** as a preview grid rather than as a download chip. It ships on its own, ahead of the File Agent, which later reuses the same implementation from a second caller. Underneath it sits the storage foundation the File Agent also needs — the document-type discriminator, the `generated` container, the non-ingesting write path and the assistant-message attachment reference — extracted here because the image capability is the first thing that needs it.

**Success criteria.**

- **Token attribution completeness**: 100% of image tool calls write a `Core.ConversationUsageToolCall` row whose `InputTokens`, `OutputTokens` and `TotalTokens` are the values the image API's own `usage` block returned, and **0 rows** carry a zero, an estimate or a pixel-derived count — measured by a query over that table joined on `CallId` to the `image.generation.count` counter, so a call the meter saw and the audit did not is visible as a mismatch rather than as an absence.
- **Retrieval isolation**: **0 rows** exist in `Core.ConversationDocumentChunk` for any `ConversationDocument` whose `Type` is `Generated` or `Attachment`, and 0 citations in any answer name one — measured by a scheduled query over those two tables and pinned by the integration test US-106 ships.
- **Batch fidelity**: a request for N images yields exactly N persisted `Generated` documents **or** a failed activity, never a silent partial — measured as 0 turns where `image.persisted.count` is strictly between 1 and N-1 for the same `scopeId`, over a rolling 7-day window.
- **Single-image latency**: p95 elapsed time from the tool activity's `ActivityStarted` to its `ActivityCompleted` is ≤ 25 s for one `1024x1024` generation at `input_fidelity: low`, measured by the `image.generation.duration` histogram tagged `n=1`.
- **Delivery**: ≥ 99% of inline previews resolve to a `200` from the preview byte route (open question 1) and ≥ 99% of explicit download clicks resolve to a `200` from `GET /api/documents/conversations/{conversationId}/{documentId}` within the link's five-minute lifetime — measured by those two routes' success rates filtered to `Generated` and `Attachment` documents.

A user drags a photograph of a whiteboard into the composer. It uploads, and nothing else happens to it — no extraction, no chunking, no embedding, no background job — because it is an `Attachment`, a file the platform holds and deliberately does not read. The user types "clean this up and give me four colour variations". The assistant calls one tool; an activity card appears with a sub-status line, and one `/images/edits` request goes out carrying that image and `n: 4`. Four pictures come back as base64, are written to the `generated` container as four `ConversationDocument` rows with zero chunk rows between them, and render as a 2×2 preview grid beneath the answer. The user clicks one, it opens full size, and a second click saves it. Tomorrow the conversation reopens, the grid renders again from the references persisted on the assistant message, and the whole turn — the assistant's tokens and the image model's own reported `input_tokens`, `output_tokens` and `total_tokens` — is one row and its child in the usage audit.

## 2. Goals & non-goals

**Goals.**

- Let a user get a real image out of a conversation — generated from a description, or from a picture they already have — without leaving the platform.
- Accept an image upload for the first time, through a path that stores the file and deliberately does not ingest it, so the retrieval corpus is unchanged by a feature that adds files to conversations.
- Do batches in **one call**: `n` outputs per request and an `image[]` array of inputs, because `Program.cs`'s shared `ConfigureFunctionInvocation` caps a turn at `MaximumIterationsPerRequest = 5` and fanning out four generations would spend four of those five iterations on one user request.
- Attribute every token to the turn that caused it, using the API's own reported counts and never an estimate.
- Keep both a generated image and a non-ingested uploaded image structurally unreachable from retrieval — each has an owner and an identity and **no chunks**, so neither can be ranked or cited.
- Show the result where the user is looking: inline under the assistant message, not behind a chip they have to click to find out what they got.
- Ship the storage foundation once, so `docs/prd/file-agent/file-agent.md` consumes it rather than rebuilding it — one implementation reached from two callers.
- Keep the client's initial bundle under its budget: every surface this PRD adds rides the lazy `chat` chunk.

**Non-goals.**

- **Masking and inpainting.** The API supports a `mask` parameter — a PNG under 4 MB with the same dimensions as the image, applied to the first image when several are provided — and it needs a region-selection UI that no board in `docs/design/` covers. It is beyond "generate an image from an image" and is deferred whole.
- **Image-to-text.** The assistant still cannot *see* an attached image. An uploaded image is an input to the image tool and nothing else; it is not added to the model's context, not described, not OCR'd. Multimodal chat input is a separate feature with its own model-catalog and cost consequences.
- **WEBP.** The API's `output_format` lists `png`, `jpeg` and `webp`, but Microsoft states plainly that "WEBP images are not supported in Azure OpenAI in Azure AI Foundry Models". Output is PNG; accepted uploads are `.png`, `.jpg` and `.jpeg`.
- **Streaming partial images.** `stream: true` with `partial_images` between 1 and 3 exists and would need a second frame path through an SSE contract this PRD is forbidden to extend. The activity card's running state is the progress indicator.
- **New SSE frame kinds, or any change to the `Andes.Extensions.AI.UI` event contract.** `AssistantUiEvent` already carries `ActivityStarted`/`ActivityProgress`/`ActivityCompleted`/`ActivityFailed` with `scopeId`, `parentScopeId`, `displayName`, `toolKind` and `depth`. Adding a kind is a breaking change to a third-party package shape and a drift failure in `npm run check:contract`.
- **New RFC 9457 problem type URIs.** Every failure this feature introduces is either mid-stream — a failed activity card — or reuses `/problems/provider-not-configured`, `/problems/storage-not-configured`, `/problems/validation-error` or `/problems/resource-not-found`. Problem types are opaque identifiers clients match verbatim, and this PRD adds none.
- **Generated or attached images on projects.** The type discriminator goes on `ConversationDocument`, not on `BaseDocument`, so `ProjectDocument` is untouched. The symmetry is deliberately deferred.
- **Editing an image in the browser.** No crop tool, no region selector, no canvas. The deliverable is a picture rendered inline and downloadable.
- **A second usage API.** `ConversationUsage` has no HTTP surface today, and this PRD does not invent one; EP-5 reports through the audit table and the telemetry backend.
- **Video, animation, or any non-still output.**
- **Documentation stories.** Docs under `docs/` and the `CHANGELOG.md` entry are the SE Technical Writer flow's job, after implementation.

## 3. Users & access

**Personas.**

- **Chat user**: a signed-in employee holding a conversation. Asks for a picture in natural language and attaches one from the composer; never selects the image tool, because it is an implicit tool the assistant chooses like document retrieval.
- **Administrator**: holds `PermissionIds.Administrator` (`a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d`). Grants the image permission and reads what the feature costs.
- **Operator**: runs the deployment. Holds the feature flag, owns the per-turn and per-user ceilings, and consumes the telemetry this PRD adds.
- **Backend engineer**: owns EP-0, EP-1, EP-2, EP-3 and EP-5, working in `enterprise-gpt-api/` under `.claude/rules/csharp.md` and `aspnet-rest-apis.md`.
- **Frontend engineer**: owns EP-4, working in `enterprise-gpt-ui/` under the `ngrx-signal-store` and `angular-developer` skills.

**Role-based access.**

- **Anonymous**: no access. Every route this feature touches is inside a group already carrying `.RequireAuthorization()`.
- **Chat user without the image grant**: conversations behave exactly as they do today. The image tool is **not attached** to their turns and the assistant is told nothing about it, so it cannot advertise a capability the user does not have. This mirrors how `Upload File` gates the upload affordance rather than producing a 403 on a resource the caller never named.
- **Chat user with the image grant**: the tool is attached to turns on a tool-enabled model. They can attach an image, see what the assistant produced, and download any image in a conversation they own — through the same route and the same ownership rule that already serves uploaded documents. Ownership is read off the **parent conversation**, and a miss is a 404, never a 403 (`docs/documents/download-workflow.md` §3.1, §3.3).
- **Administrator**: grants and revokes the permission through the existing `api/permissions` surface. Administrators are **not** implicitly granted it, matching the codebase's standing rule that an administrator holds only what they were given.

The grant is resolved through the singleton `IUserPermissionCache`, the same path `PermissionEndpointFilter` uses, and is invalidated per user by `PermissionService` exactly as every other grant already is. Whether it is the existing `Upload File` permission (`b1c2d3e4-f5a6-4b7c-8d9e-0f1a2b3c4d5e`) or a new `Generate Image` one is **open question 3**; US-504 ships whichever the product owner picks and states both consequences.

## 4. Functional requirements

| ID | Requirement | Priority | Epic(s) |
| --- | --- | --- | --- |
| FR-1 | The image deployment's availability in this region, its name, its quota and the exact shape of its response `usage` block are established before any code depends on them | P0 | EP-0 |
| FR-2 | The `/images/edits` multipart surface — an `image[]` array, `n > 1`, `input_fidelity` — is proven callable from .NET, with the exact package ids and versions that worked recorded | P0 | EP-0 |
| FR-3 | `ConversationDocument` carries a document-type discriminator (`Uploaded = 1`, `Generated = 2`, `Attachment = 3`); every existing row is backfilled to `Uploaded` by the migration | P0 | EP-1 |
| FR-4 | A generated image is persisted as a `ConversationDocument` with `Type = Generated` in a separate `generated` blob container, with **zero** chunk rows | P0 | EP-1 |
| FR-5 | Neither a `Generated` nor an `Attachment` document is ever extracted, chunked, embedded, retrieved, ranked or cited | P0 | EP-1, EP-3 |
| FR-6 | An image downloads through the existing document download route; no signed URL is ever persisted, streamed, or written to a log | P1 | EP-1, EP-4 |
| FR-7 | The download content-type map covers `.png`, `.jpg` and `.jpeg`, so no image is served as `application/octet-stream` | P1 | EP-1 |
| FR-8 | The persisted assistant message carries a structured reference to each image the turn produced — id, name, extension, MIME type, size, type — and never a URL | P0 | EP-1 |
| FR-9 | Generated blobs are reclaimed under a retention policy rather than accumulating indefinitely | P2 | EP-1 |
| FR-10 | The image client, its options class and its `Enabled` flag are registered with `ValidateOnStart`, matching the gating shape the four chat providers already use | P0 | EP-2 |
| FR-11 | The assistant can generate an image on request, and the API's own reported token counts are recorded verbatim — never estimated, never derived from pixel dimensions, never defaulted to zero | P0 | EP-2 |
| FR-12 | One tool call can request several images through the API's `n`, rather than fanning out into several calls against the turn's five-iteration budget | P1 | EP-2 |
| FR-13 | No tool this PRD adds can have its usage misattributed to an MCP server by the longest-`{server}_`-prefix rule | P0 | EP-2, EP-3 |
| FR-14 | A tool that cannot run stands down with a warning rather than failing the turn; a tool that fails at run time surfaces as a failed activity card, never as a corrupted stream | P1 | EP-2 |
| FR-15 | The upload path accepts `.png`, `.jpg` and `.jpeg` and stores them with **no** extraction, chunking, embedding or background-job enqueue | P0 | EP-3 |
| FR-16 | An uploaded image is validated by its leading magic bytes, not by its extension alone | P0 | EP-3 |
| FR-17 | Source images are resolved by document id under the parent conversation's ownership rule; a miss is a 404, never a 403 | P0 | EP-3 |
| FR-18 | The API's PNG/JPG-only and per-image size limits are enforced before the call is made, not discovered from its rejection | P0 | EP-3 |
| FR-19 | The assistant can edit an image already in the conversation, whether it generated it or the user attached it | P0 | EP-3 |
| FR-20 | Several source images can be combined or varied in one call, producing `n` outputs | P1 | EP-3 |
| FR-21 | An unusable source image is refused with a reason naming which image and which rule | P1 | EP-3 |
| FR-22 | Images the assistant produced render inline under the assistant message — one on its own, several as a grid | P0 | EP-4 |
| FR-23 | An image opens full size and downloads through the existing `DocumentDownloadStore` and route, unchanged | P1 | EP-4 |
| FR-24 | Images survive a reload, rendered from the reference persisted on the message | P1 | EP-4 |
| FR-25 | A user can attach an image in the composer, through the existing upload affordance | P0 | EP-4 |
| FR-26 | Every image surface has designed loading and failure states, distinct from one another | P1 | EP-4 |
| FR-27 | Every image surface is operable by keyboard, announced to assistive technology, and honours `prefers-reduced-motion` | P0 | EP-4 |
| FR-28 | No component this PRD adds reaches the initial bundle graph; all of them ride the lazy `chat` chunk | P0 | EP-4 |
| FR-29 | Image tokens and image counts roll up to the causing turn, so a conversation's totals include what the tool spent | P1 | EP-5 |
| FR-30 | Image activity emits spans and metrics through the existing OpenTelemetry registration | P1 | EP-5 |
| FR-31 | What one turn and one user can generate is bounded, because images bill per image and `n` reaches 10 | P1 | EP-5 |
| FR-32 | Image generation is gated on an explicit permission grant, resolved through `IUserPermissionCache` | P0 | EP-5 |
| FR-33 | The capability is behind a configuration flag with a documented rollback that requires no redeploy | P0 | EP-5 |
| FR-34 | No new `AssistantUiEvent` kind and no new RFC 9457 problem type is introduced | P0 | EP-2, EP-3, EP-4 |

## 5. User experience

**Entry points & first-time flow.** There is no new screen, no new menu item and no picker. A user with the grant asks — "draw me a diagram of this", "make four variations of that logo", "clean up this photo" — and the assistant decides to call the tool, exactly as it already decides to call document retrieval. The one visible change is in the composer: the existing upload affordance now offers image files, because `documents/file-extensions` advertises them. A user without the grant sees no difference from today; nothing renders an unavailable panel, because there is no surface that promised one.

**Core experience.**

1. The user optionally attaches an image in the existing composer. It uploads through the same route as any document, shows the same progress, and lands as a chip — but its job status resolves immediately, because nothing ingests it.
2. The user sends a prompt. An activity card appears in the timeline with the tool's `displayName` and a **Function** kind badge, rendered as separate elements — a pre-composed label like "Generating image" is a defect per the streaming contract.
3. A sub-status line beneath the card comes from `ChatProgress.Report(...)`, which is the only channel that reaches the feed. It never carries the prompt text, because the tool-tracking middleware's whole posture is that tool arguments stay out of activity frames.
4. The card completes with a duration. The answer text continues streaming around it.
5. **A preview grid renders under the assistant's message.** One image renders on its own at a bounded size; two to four render in a grid; more than that wraps. Each tile shows the picture, not a file name — the point of an image is that you can see it.
6. Clicking a tile opens it full size in an overlay, with a download control. The download calls `GET /api/documents/conversations/{conversationId}/{documentId}` **at that moment** and hands the returned URL to a detached anchor. The link is never prefetched, never stored, never placed in a router URL. This is the shipped US-804 path, unchanged.
7. Reopening the conversation later replays the transcript, the grid renders again from the references persisted on the assistant message, and a download click mints a **fresh** five-minute link. The activity tree is not replayed — only the answer text and the image references are transcribed, matching the existing rule that reopening a conversation replays the answer and never the work that produced it.

**Edge cases & UI states.**

- **Working**: the activity card shows a running ring. The preview grid renders skeleton tiles sized to the requested count, so a request for four images does not look like a request for one that is running late.
- **Partial batch**: the tool asked for four and the API returned three. This is a **failed** turn for the batch-fidelity criterion, not a partial success: the activity renders failed, the assistant says how many it got, and the images that did land are still shown. Nothing silently renders three tiles where four were promised.
- **Content filter**: the prompt or the source image was rejected. The card renders failed with the reason, the assistant explains it in prose, and no tile renders. This is the most common failure and reads as a refusal, not an outage.
- **Unusable source image**: wrong format, too large, not owned, not actually an image. The refusal names which image and which rule — a single generic "could not read the image" makes the wrong file get replaced.
- **Feature off, or the model is not tool-enabled**: the tool is not attached and the turn proceeds normally. There is no "image generation is unavailable" panel, because there is no surface that promised it.
- **Download expired or gone**: a `404` from the download route reads as "no longer available", not "deleted", because the same status covers a document that was never the caller's. A `503` `/problems/storage-not-configured` reads as a deployment fact about the platform, not a fault of that image. Both behaviours already exist in `DocumentDownloadStore` and are inherited.
- **Preview fails to load**: the tile falls back to a named placeholder with a retry, and the failure is distinguishable from "still loading" — a spinner that never resolves is the state this criterion exists to prevent.
- **Busy**: a second turn while one is streaming still returns 409 `conversation-busy`, surfaced as a retryable warning and never auto-retried. A four-image batch makes this window wider, which is why US-405's copy must name the running turn rather than read as a generic error.

**UI/UX highlights.**

- The preview grid is a **new** presentational component under `shared/`, not a variant of `shared/chip/attachment-chip` — a chip is a file identity and an image is the content itself, and forcing one into the other would give a picture a file-name label and a 32-pixel glyph. The chip is still what renders an *attached* image in the composer, where the file has not been sent yet.
- `attachment.model.ts`'s `fileGlyph` table covers nine extensions and **none of them is an image**, so an attached `.png` falls through to `bi-file-earmark` today. EP-4 adds the image entries, and `npm run lint`'s `check:icons` gate over the checked-in sprite manifest is what keeps the new glyph names real.
- Keyboard: each preview tile is a real `<button>` in tab order, activated by Enter and Space; the full-size overlay traps focus, closes on Escape, and returns focus to the tile that opened it.
- Announcements: the grid's appearance is announced once through the transcript's existing `aria-live` region, stating how many images arrived. Individual tiles are not announced; a polite region that fires per tile is unusable. Each tile's accessible name states that the image was generated and gives its ordinal, because the picture itself carries no text.
- Motion honours `prefers-reduced-motion`, using the four keyframes already defined in the token layer. No new keyframe is introduced.
- `docs/design/` carries no board for an inline image grid — the closest is `01 Chat and Streaming.dc.html`. The layout is therefore derived from the existing transcript spacing and `theme.css` tokens rather than quoted from a frame, and `docs/design/project/theme.css` stays the token source of record.

## 6. Technical considerations

**Two verified facts shape everything below.**

**1. The platform accepts no images at all today.** `Enterprise.Gpt.Dto/Enums/FileExtensions.cs` has 26 values and no `png`, `jpg`, `jpeg` or `webp`. `Service/Validators/UploadedFileValidator.cs` admits a file only when `IDocumentTextExtractorFactory.IsSupported(extension)` is true — that is, only if something can extract *text* from it — and it carries magic-byte checks for exactly three container families: `_pdfSignature` (`%PDF`), `_zipSignature` (`PK`, for OOXML `.docx`/`.pptx`), `_ole2Signature` (legacy `.doc`), plus a `LooksBinary` NUL-byte rejection for `.md` and `.txt`. `DocumentEndpoints.GetFileExtensions` returns `extractorFactory.SupportedExtensionNames` with a comment recording that the advertised formats and the readable formats must not drift apart, and the client's root-scoped `SupportedExtensionsStore` reads that route to decide what the picker offers. The upload surface is extractor-derived end to end. **Image input therefore needs a genuinely new path**: a user-provided file that is stored and deliberately not ingested, admitted by a validator branch that does not consult the extractor factory at all, and advertised by an endpoint that no longer equates "acceptable" with "readable".

**2. The Azure image API batches natively.** Verified against Microsoft Learn (`https://learn.microsoft.com/azure/foundry/openai/how-to/dall-e` and the v1-preview REST reference, api-version `2025-04-01-preview`):

- `/images/edits` takes `image` as *"string or array … The image(s) to edit. Must be a supported image file or an array of images"*, each a PNG or JPG.
- `n` is *"The number of images to generate. Must be between 1 and 10."*
- `input_fidelity` is `high` or `low`, *"only supported for gpt-image-1 series models"*, defaulting to `low`.
- `mask` is a PNG under 4 MB with the same dimensions as the image; *"if there are multiple images provided, the mask will be applied to the first image"* — carried as a non-goal in §2.
- `prompt` allows 32000 characters for the `gpt-image-1` series.
- The edits endpoint is **`multipart/form-data`, not JSON**, and is a **different HTTP surface** from `/images/generations`. Microsoft states this explicitly, twice.
- `gpt-image-1` **always returns base64** in `b64_json`; `response_format` is not supported for it.
- DALL·E 3 caps `n` at 1 and requires parallel requests for more; GPT-image does not.

So "several images at a time" is **one call**, not a fan-out. That matters here specifically: `Program.cs`'s shared `ConfigureFunctionInvocation` sets `MaximumIterationsPerRequest = 5`, so a turn gets five tool calls in total. Fanning out four generations would spend four of them, leaving one for everything else the turn needs — which is the explicit rationale US-204 and US-304 record.

**Integration points.** All verified against the working tree.

| Concern | Where |
| --- | --- |
| The single tool-assembly funnel | `Enterprise.Gpt.Service/ConversationService.cs` — `CreateChatOptionsAsync` at line 1106 builds one `List<AITool>` at line 1151 and assigns `chatOptions.Tools` **exactly once** at line 1253, under a comment recording that assigning per source would have the last writer discard the others |
| The tool shape to copy | `Enterprise.Gpt.Service/Tool/WeatherTool.cs` — a static `Create(options, logger)` returning `AIFunctionFactory.Create(invoker.Method, ToolName, ToolDescription)` over a per-turn invoker that reports sub-status via `ChatProgress.Report(...)` |
| The stand-down pattern for an implicit tool | `Enterprise.Gpt.Service/Tool/DocumentTool.cs` — a non-tool-enabled model logs a warning and the turn proceeds; it never fails with a 400 |
| The registration shape to copy | `Enterprise.Gpt.Api/Program.cs` L190–228 — `AddOptions<AzureAIFoundryOptions>().Bind(...).Validate(...).ValidateOnStart()`, then `if (config.GetValue<bool>("…:Enabled"))` guarding the registration itself |
| Options class precedent | `Enterprise.Gpt.Service/Settings/WeatherToolOptions.cs` — `public const string SectionName`, beside `DocumentOptions` and the four provider option classes |
| Activity frames, already free | the tool-tracking middleware installed by `.UseToolTracking(ConfigureToolTracking)` on every provider chain in `Program.cs`; nesting and folding are described in `docs/conversations/streaming-contract.md` §5.1 |
| Function-invocation bounds shared by every provider | `Program.cs` — `ConfigureFunctionInvocation`: `AllowConcurrentInvocation = false`, `IncludeDetailedErrors = true`, `MaximumIterationsPerRequest = 5`, `MaximumConsecutiveErrorsPerRequest = 5` |
| MCP attribution rule the tool names must not trip | `Enterprise.Gpt.Service/Chat/UsageReportTranslator.cs` — `ResolveMcpServerId`, longest `{sanitizedServerName}_` prefix match wins |
| Usage audit shape | `Enterprise.Gpt.Entity/ConversationUsageToolCall.cs` — `Kind`, `Depth`, `ParentId`, `CallId`, `InputTokens`, `OutputTokens`, `TotalTokens`, `SubtreeTotalTokens`, `DurationMs`, `Succeeded` |
| Document entity and its EF configuration | `Enterprise.Gpt.Entity/ConversationDocument.cs` (`Chunks`), `BaseDocument.cs` (`UserId`, `Name`, `Extension`, `MimeType`, `Size`, `Path`), `Repository/Configurations/ConversationDocumentConfiguration.cs` |
| Upload validation to extend | `Enterprise.Gpt.Service/Validators/UploadedFileValidator.cs` — `HasContentMatchingExtension` and its three signature constants |
| Advertised formats | `Enterprise.Gpt.Api/Endpoints/DocumentEndpoints.cs` — `GetFileExtensions`, mapped at `documents/file-extensions` |
| Retrieval candidate set, which neither new type may enter | `Enterprise.Gpt.Service/Tool/DocumentRetrievalSql.cs` — a `UNION ALL` of `ConversationDocumentChunk` and `ProjectDocumentChunk`; and `Tool/DocumentRetrievalService.GetScopeAsync`, whose `HasDocuments` gates the whole retrieval path |
| Content-type map for downloads | `Enterprise.Gpt.Service/DocumentService.cs` — `ResolveContentType` over a `FrozenDictionary` covering `.doc .docx .md .pdf .pptx .txt` |
| Container configuration precedent | `Enterprise.Gpt.Service/DocumentService.cs` — the `DocumentsContainer` property reading `AzureStorage:DocumentsContainer` on each use, logging and throwing when unset |
| Signed link generation | `Enterprise.Gpt.Service/BlobStorageService.cs` — `GenerateSasUri`, guarded by `CanGenerateSasUri` |
| Download route and its contract | `Enterprise.Gpt.Api/Endpoints/DocumentEndpoints.cs` — `GET conversations/{conversationId:guid}/{documentId:guid}`; documented end to end in `docs/documents/download-workflow.md` |
| Transcript shape | `Enterprise.Gpt.Entity/CosmosConversation.cs` — `CosmosConversationMessage` carries `id`, `role`, `content`, `dateCreated`, `tokens`, `model`, `usage` and **no attachments today** |
| Permission ids and their names | `Enterprise.Gpt.Dto/Enums/PermissionIds.cs` — `Administrator`, `UploadFile`, and the `Names` `FrozenDictionary` the endpoint filter validates against at map time |
| Telemetry registration to extend | `Enterprise.Gpt.Api/Observability/TelemetryRegistration.cs` — `AddEnterpriseTelemetry`'s `WithTracing`/`WithMetrics`, the only place a backend is named |
| Test double that must track any `IBlobStorageService` change | `enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/TestInfrastructure/FakeBlobStorageService.cs` |
| Client download store | `enterprise-gpt-ui/src/app/core/documents/document-download-store.ts` |
| Client upload path and the format list it reads | `core/documents/upload-store.ts`, `document-upload-client.ts`, `upload-rejection.ts`, and the root-scoped `supported-extensions-store.ts` |
| Client chip, glyph table and composer | `shared/chip/attachment-chip/` (including `attachment.model.ts`'s `fileGlyph`), `shared/composer/`, `shared/upload/`, `core/chat/composer-host.ts` |
| Transcript rendering | `features/chat/transcript/assistant-turn.ts` and `activity-card.ts` |

**Data storage & privacy.**

- **The type discriminator goes on `ConversationDocument`, not `BaseDocument`.** Putting it on the base would change `ProjectDocument` too, which nothing in this PRD produces. The enum belongs in `Enterprise.Gpt.Dto/Enums/` beside `JobStatus` and `FileExtensions`, is numbered from 1, and follows the same append-only rule: values are frozen once deployed and new ones are appended, never renumbered. The column name equals the property name — **no `HasColumnName` exists anywhere in this repository**, and the hand-written retrieval SQL in `Tool/DocumentRetrievalSql.cs` depends on that holding.
- **Three values, not two.** `Uploaded = 1`, `Generated = 2`, `Attachment = 3`. Without the third, an uploaded image is indistinguishable from an ingested source document that merely failed to chunk, and US-106's invariant cannot be expressed as a query.
- **Generated images live in their own container**, `AzureStorage:GeneratedContainer`, separate from `AzureStorage:DocumentsContainer`. **Uploaded images live in the existing `documents` container** — they are uploads, and only platform output goes to `generated`, which keeps the retention question (open question 4) scoped to one container.
- **Neither new type has chunks. This is the invariant that must never regress.** `DocumentRetrievalSql` builds its candidate set by `UNION ALL`-ing the two *chunk* tables; a document with zero chunk rows contributes nothing to either side and cannot be retrieved, ranked or cited. No type predicate is needed and **no SQL change is required** — which is precisely why the invariant has to be stated and tested rather than assumed, since nothing in the query would break if chunks appeared. `DocumentRetrievalService.GetScopeAsync` is the second surface: a non-ingested image must not contribute to `HasDocuments` or to the document-name list injected into the retrieval prompt, or the assistant will be told a file exists that it cannot read.
- **The transcript carries an identity, never a credential.** `DocumentDownloadDto.downloadUrl` is a bearer credential that cannot be withdrawn (`docs/documents/download-workflow.md` §5.4) and dies in five minutes. A URL written into an assistant message would be both a persisted secret and dead by the time anyone re-read the conversation. `CosmosConversationMessage` gains an `attachments[]` array of `{ id, name, extension, mimeType, size, type }` — no URL. The conversation-level `CosmosConversation.Documents[]` is not reused: it answers "what files does this conversation hold", carries no message linkage, and a generated image placed there would masquerade as conversation-scope input. The addition is small against the Cosmos 2 MB item cap even for an eight-image turn.
- **Prompt content never leaves the turn's normal path.** Image prompts and tool arguments are not written to progress events; `ToolTrackingOptions.IncludeToolArguments` stays off. No log statement carries a prompt, an image byte, a base64 payload or an API key.

**Security.**

- **`Content-Disposition` stays `attachment`**, with a `Content-Type` derived from the extension and signed into the SAS — never taken from the stored `MimeType`. This is the posture the upload path already takes (`docs/documents/download-workflow.md` §4.3), and it matters more here: an `inline` disposition would let a generated or uploaded SVG or HTML file execute on the storage origin. Adding image formats to the content-type map does not soften that rule.
- **An inline preview must not put a SAS in the DOM.** The standing posture is that a signed link is never prefetched, persisted, or written into a URL, and `file-agent.md`'s US-602 required that a transcript with ten chips issue **zero** requests until one is clicked. An `<img src="{sas}">` on every image in a transcript breaks that rule outright. The proposed resolution is **open question 1**: a new authenticated byte-streaming route serves previews while the SAS route stays for explicit downloads only.
- **Ownership is read off the parent conversation.** Resolving a source image by document id uses the same rule the download route enforces, and a miss is a 404 with `/problems/resource-not-found`, never a 403 — matching `docs/documents/download-workflow.md` §3.1 and §3.3. A user cannot use another user's picture as an edit source by guessing an id.
- **An uploaded image is untrusted bytes.** It is validated by its leading magic bytes as well as its extension, capped at `Documents:MaxFileSizeBytes`, and stored without ever being decoded, re-encoded or thumbnailed by the API — no image-processing library is introduced, because an image decoder is an attack surface and nothing in this PRD needs one.
- **SAS signing still requires a shared-key connection string.** `BlobBaseClient.CanGenerateSasUri` is true only for a `StorageSharedKeyCredential`; moving to `DefaultAzureCredential` would require a user delegation SAS. Adding a second container changes nothing about that constraint and does not resolve it.
- **The permission gate is the last gate.** Until US-504 lands, the capability is reachable by anyone in an environment where the flag is on — which is why US-505 defaults the flag off everywhere, including development.

**Scalability & performance.**

- **The image deployment is rate-limited by requests, not tokens.** Microsoft's quota table gives `gpt-image-1` **N/A TPM** and **6 requests per minute** on the Default tier, 20 on an Enterprise agreement. That is a hard concurrency ceiling measured in *calls*, which is a second, independent reason to batch inside one call: four images through `n: 4` costs one request against that quota, and four separate generations cost four. US-001 confirms the real numbers for the target deployment.
- **`MaximumIterationsPerRequest = 5` bounds the turn**, not the batch. A turn that fans out cannot also retrieve documents and call an MCP tool.
- **Images bill per image**, and `n` reaches 10, so an unbounded tool is a cost incident waiting for one curious user. US-503 bounds it; the defaults proposed in §9 are `n ≤ 4` per call and 8 images per turn.
- The dominant latency is the API call itself. `gpt-image-1` returns base64, so a four-image batch is a single large response the API must decode and write to blob storage; the write is per image and parallelisable, the call is not.
- **Client bundle**: production warns above **670 kB** initial raw and fails above **720 kB**, and the baseline after US-908 is **668.90 kB raw / 168.23 kB transfer — 1.1 kB under the warning line**. Every EP-4 component and store must ride the lazy `chat` chunk; a root-scoped store here trips the budget on its own. That is an acceptance criterion in US-401, not a note.

**AI system requirements.**

- **Tools the system needs**: one image deployment from the `gpt-image-1` series (limited access) or `gpt-image-2` (generally available), reachable on both `/images/generations` and `/images/edits`. EP-0 gates every downstream story on confirming which.
- **The primary evaluation is deterministic and cheap**: a fixed benchmark of 20 prompts — 8 text-to-image spanning single and batched requests, 8 image-to-image spanning one source and several, and 4 designed to be refused (a source that is not an image, a source over the size limit, a source the caller does not own, and a prompt the content filter rejects). Pass threshold before EP-3 is accepted: **100% of the 16 valid prompts either persist exactly the requested number of images or surface a failed activity**, and **100% of the 4 invalid prompts produce a refusal naming which rule was broken**. A silent partial batch counts as a failure regardless of how good the images are.
- **Activity labels are never pre-composed.** `displayName` and `kind` are rendered separately by the client; the tool supplies a name, not a sentence.
- **The tool names must not collide with the MCP attribution rule.** `UsageReportTranslator.ResolveMcpServerId` matches the longest `{sanitizedServerName}_` prefix, so `generate_` and `edit_` must not be reachable prefixes of any catalogued server name — the same reasoning `WeatherTool.ToolName` records for `get_weather`.

## 7. Epics & user stories

| ID | Epic | Goal | Priority | Estimate | Depends on |
| --- | --- | --- | --- | --- | --- |
| EP-0 | Provisioning gate | Prove the image deployment and its edit surface exist and are callable from .NET before anything is built on them | P0 | M | — |
| EP-1 | Generated-file storage | A file the platform produces has an identity, an owner, a container and a download link — without polluting retrieval | P0 | L | — |
| EP-2 | Image generation | The assistant produces images from a description, one or several at a time, with real token usage recorded | P0 | L | EP-0 |
| EP-3 | Image input and editing | A user's own picture becomes an input the assistant can work from | P0 | L | EP-0, EP-1, EP-2 |
| EP-4 | Frontend surface | A user sees what the assistant made, opens it, saves it, and can attach one of their own | P0 | M | EP-1, EP-2 |
| EP-5 | Usage, cost and governance | Every image is attributed, bounded, permissioned and switchable off | P0 | M | EP-2 |

EP-0's two stories are a gate rather than a phase, and EP-1 carries an unusually high proportion of `[enabler]` stories. Both are properties of a feature whose first user-visible behaviour needs a schema change, a new container and a new SDK registration underneath it; each enabler names the stories it unblocks, and none exceeds L. EP-1 depends on no Azure capability at all and can therefore start during the gate.

One priority pairing looks inverted and is not. US-104 (download a generated file) is **P1** while US-401 (see the images inline) is **P0** and does not depend on it: previews are served by the byte route open question 1 proposes, so a user can see everything the assistant made before the download path exists. Saving a file is a degradation, not a blocker — whereas an image capability whose output is invisible is not a capability. US-501 is P1 for the same shape of reason: US-202 is what *records* the API's counts, and US-501 is the story that asserts the arithmetic rolls up.

### EP-0: Provisioning gate

#### US-001: `[enabler]` Confirm the image deployment

- **Story**: `[enabler]` Establish which image model can actually be deployed in this project's region, under what name and quota, and what its response `usage` block contains — so every downstream estimate is made against a real deployment rather than a documentation page. Unblocks US-002 and US-201.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Status**: Not started
- **Acceptance criteria**:
  - Given the target project, when its deployments are listed, then the outcome records which of the `gpt-image-1` series (limited access) and `gpt-image-2` (generally available) can be deployed here, the deployment name US-201 will target, and the quota — which Microsoft's table gives as **requests per minute** with TPM `N/A`, so the number that bounds this feature is a call rate, not a token rate.
  - Given a single generation, when its response is read, then the exact contents of the `usage` block are recorded verbatim — the `input_tokens` breakdown into `text_tokens` and `image_tokens`, `output_tokens` and `total_tokens` — because US-202 must write those fields and cannot invent a shape for them.
  - Given the deployment, when a `1024x1024` generation is timed at least five times, then the observed p50 and p95 are recorded, so §1's 25 s p95 target is either confirmed or corrected before anything is measured against it.
  - Given no suitable image model is available in this region, when the gate closes, then the alternative is reported — a second region, or a different image model — rather than the dependent work being started on an assumption.

#### US-002: `[enabler]` Prove the edit surface from .NET

- **Story**: `[enabler]` Call `/images/edits` from a throwaway .NET harness with an **array** of images, `n > 1` and `input_fidelity` set, over `multipart/form-data`, and pin the exact package ids and versions that made it work. Unblocks US-201 and, through it, the whole critical path.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-001
- **Status**: Not started
- **Acceptance criteria**:
  - Given the harness, when it posts `image[]` with two files, `n: 2` and `input_fidelity: low` as `multipart/form-data`, then two base64 images come back in `data[]` — establishing that the array form and the batch form work together, which is the single assumption EP-3's whole design rests on.
  - Given the installed OpenAI and Azure client libraries, when the call is written, then the outcome records whether any of them exposes the edits surface with an image **array**, or whether a typed `HttpClient` against the multipart endpoint is required, and the exact package ids and versions that were used are pinned in the spike's output.
  - Given the same harness, when a generation and an edit are both issued, then the outcome records whether `usage` is returned on the **edits** path as well as on generations — Microsoft documents the block for generations, and US-202's "verbatim counts" criterion is unimplementable on the edit path if it is absent there.
  - Given the per-image size limit, when it is checked, then the outcome records the real number: the how-to page states 50 MB and the v1-preview reference states 25 MB for the same endpoint, and US-302 enforces whichever is true rather than picking the friendlier one.
  - Given the harness completes, when it is reviewed, then it is deleted or left under `tests/` as a skipped integration test; no spike code is merged into `Enterprise.Gpt.Api` or `Enterprise.Gpt.Service`.

### EP-1: Generated-file storage

#### US-101: `[enabler]` Add the document-type discriminator and migrate existing rows

- **Story**: `[enabler]` Add a `ConversationDocumentTypes` enum (`Uploaded = 1`, `Generated = 2`, `Attachment = 3`) to `Enterprise.Gpt.Dto/Enums/`, put the column on `ConversationDocument`, and ship one migration that creates it and backfills every existing row to `Uploaded`. Unblocks US-103, US-105, US-106 and US-301.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Status**: Not started
- **Acceptance criteria**:
  - Given the new enum, when it is placed, then it sits in `Enterprise.Gpt.Dto/Enums/` beside `JobStatus` and `FileExtensions`, is numbered from 1 so an unset column is distinguishable from a legitimate value, and carries the same append-only rule in its XML docs — values are frozen once deployed.
  - Given the third value, when `Attachment = 3` is documented, then the doc comment states what separates it from `Uploaded`: a file the user provided that the platform deliberately does **not** ingest — without it, an uploaded image is indistinguishable from a source document that merely failed to chunk, and US-106's invariant cannot be expressed as a query.
  - Given the entity, when the column is added, then it is on `ConversationDocument` and **not** on `BaseDocument`, so `ProjectDocument` is unchanged; the deferred symmetry is recorded in a comment naming this decision.
  - Given `ConversationDocumentConfiguration`, when the model is built, then the property is configured there with `HasConversion<int>()` and the column name equals the property name — no `HasColumnName` is introduced, because none exists anywhere in this repository and the hand-written SQL in `Tool/DocumentRetrievalSql.cs` depends on that.
  - Given the migration, when it is generated, then it follows `20260813233326_AddModelIsReasoningEnabled` in schema `"Core"`, is applied by the existing startup `Database.Migrate()`, and every pre-existing row reads back as `Uploaded` with no null and no default drift.

#### US-102: `[enabler]` Provision the `generated` container and its configuration

- **Story**: `[enabler]` Add `AzureStorage:GeneratedContainer` and the write path into it, keeping `IBlobStorageService` and its test double in step. Unblocks US-103 and US-107.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Status**: Not started
- **Acceptance criteria**:
  - Given `AzureStorage:GeneratedContainer`, when it is read, then it is read on each use rather than cached at construction, matching how `DocumentService`'s `DocumentsContainer` property consumes `AzureStorage:DocumentsContainer` today.
  - Given the setting is unset, when a generated file would be written, then `StorageNotConfiguredException` is raised and maps to the existing 503 `/problems/storage-not-configured` — no new problem type is introduced, because problem type URIs are opaque identifiers clients match verbatim.
  - Given the blob key, when one is written, then it follows the existing convention shape (`{userId}/{conversationId}/{documentId}{ext}`) so a reader of one container can read the other without a second rule.
  - Given any change to `IBlobStorageService`, when the integration suite runs, then `FakeBlobStorageService` implements the same surface and the suite compiles — a fake that has drifted is a build failure, not a runtime surprise.

#### US-103: `[enabler]` Persist a generated file without ingesting it

- **Story**: `[enabler]` Add the write path that stores bytes in the `generated` container and creates a `ConversationDocument` with `Type = Generated` — and, critically, **no** extraction, chunking, embedding or job enqueue. Unblocks US-104, US-105, US-106 and US-205.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-101, US-102
- **Status**: Not started
- **Acceptance criteria**:
  - Given a generated artifact, when it is persisted, then a blob is written and a `ConversationDocument` row is created carrying `UserId`, `ConversationId`, `Name`, `Extension`, `MimeType`, `Size`, `Path` and `Type = Generated`, in one operation whose failure leaves neither a row without a blob nor a blob without a row.
  - Given the same call, when the pipeline is inspected, then **no** `IDocumentTextExtractor` is resolved, no chunker runs, no embedding is generated, and no background job is enqueued — a generated file is not an upload with a different label.
  - Given `.png`, for which no extractor is registered, when it is persisted, then it succeeds; the extractor-derived supported-extension set that gates uploads is **not** consulted on this path, which is the whole point of the path existing.
  - Given an artifact larger than `Documents:MaxFileSizeBytes`, when it is persisted, then the write is refused and the caller reports the reason, so a runaway batch cannot write an unbounded blob.
  - Given the row after persistence, when the chunk table is queried for it, then it returns **zero** rows.

#### US-104: Download a generated file in its own format

- **Story**: As a chat user, I want an image the assistant made to download with the right name and the right type, so that it opens in an image viewer instead of arriving as an anonymous byte stream.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-103
- **Status**: Not started
- **Acceptance criteria**:
  - Given a `Generated` document, when `GET /api/documents/conversations/{conversationId}/{documentId}` is called by the conversation's owner, then a `DocumentDownloadDto` is returned with `Cache-Control: no-store`, exactly as an uploaded document already is — the route, the DTO and the ownership rule are unchanged.
  - Given `DocumentService.ResolveContentType`, when the map is read, then it also covers `.png`, `.jpg` and `.jpeg` in addition to the six extensions it covers today, so no image falls through to `application/octet-stream`.
  - Given the signed link, when it is built, then it is read-only, single-blob, five-minute, with `Content-Disposition: attachment` and the extension-derived `Content-Type` signed into it — the declared `MimeType` on the row is still ignored, because an `inline` disposition would let a generated SVG or HTML file execute on the storage origin.
  - Given the caller does not own the parent conversation, when they request the link, then the response is 404 `/problems/resource-not-found` and nothing is signed.
  - Given `AzureStorage:GeneratedContainer` is unset, when the link is requested, then the response is 503 `/problems/storage-not-configured`.

#### US-105: `[enabler]` Carry the generated-file reference on the assistant message

- **Story**: `[enabler]` Add an `attachments[]` array to `CosmosConversationMessage` carrying id, name, extension, MIME type, size and type — and no URL — so images announced during a turn are still there after a reload. Unblocks US-205, US-401 and US-403.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-103
- **Status**: Not started
- **Acceptance criteria**:
  - Given `CosmosConversationMessage`, when the shape is extended, then the new array is on the **message**, not on the conversation-level `CosmosConversation.Documents[]` — a preview grid must be placeable back under the message that produced it, and a conversation-level array carries no message linkage and no ordering.
  - Given the array, when an entry is written, then it contains no `downloadUrl`, no SAS token and no storage path — only the identity fields the client needs to render a preview and call a byte route, because a signed URL is an unrevocable credential that would be both persisted and expired by the time anyone re-read the conversation.
  - Given a transcript persisted before this story, when it is read, then the missing array deserializes as empty and the message renders exactly as it does today; no transcript migration is required.
  - Given a turn that produced four images, when the message is persisted, then all four appear, in the order the API returned them, so the grid's order is stable across reloads.
  - Given a cancelled turn, whose answer is transcribed nowhere, then no attachment entry is written either — the reference and the message share a fate.

#### US-106: A generated or attached file is never retrieved or cited

- **Story**: As a chat user, I want images kept out of the assistant's retrieval corpus, so that a picture it drew yesterday, or one I attached that it cannot read, is never quoted back to me as if it were a source document.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-103
- **Status**: Not started
- **Acceptance criteria**:
  - Given a conversation holding one uploaded document, one generated image and one attached image, when document retrieval runs, then only the uploaded document's chunks appear in the candidate set and only it can be cited.
  - Given `DocumentRetrievalSql`, when it is compared before and after this feature, then it is **unchanged** — the exclusion holds because neither new type has chunk rows, not because a type predicate was added.
  - Given a `Generated` or `Attachment` document, when `DocumentRetrievalService.GetScopeAsync` builds the scope, then neither contributes to `HasDocuments` nor to the document-name list injected into the retrieval prompt — a conversation holding only images must not flip retrieval on, and the assistant must not be told a file exists that it cannot read.
  - Given an integration test that inserts one row of each type and asserts **zero** chunk rows exist for the `Generated` and `Attachment` ones, when the suite runs, then it passes — this test is the regression guard for the whole invariant and must fail loudly if any code path ever chunks an image.

#### US-107: Reclaim generated blobs under a retention policy

- **Story**: As an operator, I want generated images to stop accumulating forever, so that a container nothing ever deletes does not become the platform's largest cost line.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-102
- **Status**: Not started
- **Acceptance criteria**:
  - Given the retention window the product owner sets (open question 4), when a generated blob passes it, then it is removed and its `ConversationDocument` row is marked with `DateDeactivated` in the same pass, so a reachable row never points at a missing blob.
  - Given a soft-deleted generated document, when the download route is called for it, then it already answers 404 — this story changes nothing about that behaviour, only about the bytes.
  - Given uploaded documents and attached images in the `documents` container, when this policy runs, then they are untouched: the pre-existing gap that nothing reclaims that container is not silently fixed here, because the two have different retention profiles and only one of them is platform output.
  - Given no policy is yet configured, when the feature ships, then generated blobs simply accumulate and the growth rate is visible in the counters US-502 adds — an unbounded container that nobody is watching is the failure this criterion prevents.

### EP-2: Image generation

#### US-201: `[enabler]` Register the image client, options and flag

- **Story**: `[enabler]` Add the image client registration to `Program.cs` with its own options class, `Enabled` flag and `ValidateOnStart` validators, following the shape L190–228 already establishes for `AzureAIFoundryOptions`. Unblocks US-202, US-302 and US-505.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-002
- **Status**: Not started
- **Acceptance criteria**:
  - Given a new options class under `Enterprise.Gpt.Service/Settings/`, when it is written, then it carries `public const string SectionName = "Tools:ImageGeneration"` and mirrors `WeatherToolOptions`' shape, so the settings surface has one convention rather than two.
  - Given `AddOptions<T>().Bind(...).ValidateDataAnnotations().Validate(...).ValidateOnStart()`, when the app starts with the flag set, then the deployment name, endpoint, credential, size and output format are required and validated at startup; with the flag unset **no validator fires and no client is registered** — matching the `if (config.GetValue<bool>("…:Enabled"))` gating the four chat providers already use.
  - Given the edit surface needs `multipart/form-data` and generations do not, when the client is registered, then both surfaces are reachable from one registration and the packages pinned by US-002 are referenced at exactly the versions it recorded — the two are different HTTP surfaces on the same deployment, and discovering that after the tool is written is a rewrite.
  - Given the registration, when it is reviewed, then it does not disturb the four existing `AddKeyedChatClient` chains or the shared `ConfigureToolTracking`/`ConfigureFunctionInvocation` callbacks.
  - Given no image deployment is configured in an environment, when the app starts, then it starts — an unconfigured optional tool is not a broken deployment.

#### US-202: `[enabler]` Build the `generate_image` function with real usage reporting

- **Story**: `[enabler]` Write the image tool as an `AIFunction` over a per-turn invoker, following `Service/Tool/WeatherTool.cs`, taking a `count` parameter that maps to the API's `n`, reporting sub-status through `ChatProgress.Report(...)` and reporting the API's own token counts rather than an estimate. Unblocks US-203, US-302, US-501, US-502 and US-503.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-201
- **Status**: Not started
- **Acceptance criteria**:
  - Given the function, when it is created, then it is built with `AIFunctionFactory.Create(invoker.Method, ToolName, ToolDescription)` over an invoker holding the per-turn state, exactly as `WeatherTool.Create` does, and it reports at least one progress line that carries no prompt text — tool arguments never reach an activity frame.
  - Given a `count` parameter, when it is declared, then it maps to the API's `n`, is clamped to the configured ceiling before the call, and its `[Description]` tells the model that asking for several images costs one call rather than several — `MaximumIterationsPerRequest = 5` bounds the whole turn, so a fan-out spends the turn's budget on one request.
  - Given a successful generation, when usage is recorded, then the `input_tokens` (including its `text_tokens`/`image_tokens` split), `output_tokens` and `total_tokens` the API returned are used **verbatim** — no token count is estimated, derived from pixel dimensions, or defaulted to zero, because a zero here is indistinguishable from a free call in the audit.
  - Given the tool name `generate_image`, when it is checked against `UsageReportTranslator.ResolveMcpServerId`'s longest-`{server}_`-prefix rule, then a test asserts that no MCP server name in the catalog sanitizes to `generate_`, so its usage can never be attributed to a server — the same reasoning `WeatherTool.ToolName` records for `get_weather`.
  - Given the API rejects the prompt with a content-filter error, when the function returns, then it surfaces a message the model can relay to the user and does not throw an unhandled exception.

#### US-203: Ask the assistant for an image

- **Story**: As a chat user, I want to ask the assistant for a picture or a diagram and get one back in the conversation, so that I do not have to leave the platform to illustrate something.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-202
- **Status**: Not started
- **Acceptance criteria**:
  - Given `ConversationService.CreateChatOptionsAsync`, when the flag is on, the caller holds the grant and the model is tool-enabled, then the image function is added to the single `List<AITool>` built at line 1151 before `chatOptions.Tools` is assigned once at line 1253 — **no second assignment is introduced**, because assigning per source would have the last writer discard the others.
  - Given a prompt asking for an image, when the turn runs, then an `ActivityStarted`/`ActivityCompleted` pair reaches the stream with `toolKind` `Function` and the tool's display name, and no new `AssistantUiEvent` kind is added — `npm run check:contract` still passes against the vendored `Andes.Extensions.AI.UI` contract.
  - Given the selected model reports `IsToolEnabled == false`, when the turn runs, then the image tool stands down with a logged warning and the turn proceeds — it is an implicit tool, like document retrieval, not an explicit user selection like an MCP server, so it never fails the turn with a 400.
  - Given a `ConversationUsage` row for that turn, when it is read back, then a `ConversationUsageToolCall` row exists with `Kind = Function`, `Succeeded = true` and non-zero token counts.

#### US-204: Ask for several images at once

- **Story**: As a chat user, I want to ask for several variations in one go and get all of them, so that I can pick one instead of asking four times and waiting four times.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-203
- **Status**: Not started
- **Acceptance criteria**:
  - Given a request for N images within the configured ceiling, when the turn runs, then **one** call is made with `n: N` and N images come back — not N calls. `MaximumIterationsPerRequest = 5` bounds the whole turn's tool calls, so a fan-out of four generations would leave one iteration for everything else the turn needs; batching inside the call is why the parameter exists.
  - Given a request above the ceiling, when the tool is invoked, then `n` is clamped to the configured maximum and the answer states how many were produced — silently returning fewer than asked, with no explanation, is the behaviour this criterion prevents.
  - Given a batch of N, when the response is read, then either exactly N images are persisted or the activity renders **failed**; a partial batch is never reported as a success, which is the direct source of §1's batch-fidelity criterion.
  - Given the same batch, when its usage row is written, then it is **one** tool-call row carrying the API's reported counts for the whole call, not N rows — one call is one row, and splitting it would double-count the shared input tokens.
  - Given the API rejects `n` as out of range, when the failure surfaces, then the message names the accepted range (1–10) rather than relaying a raw SDK error.

#### US-205: Keep a generated image after the turn ends

- **Story**: As a chat user, I want images the assistant generated to still be there when I come back, so that I can use them later instead of regenerating them and paying twice.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-203, US-103
- **Status**: Not started
- **Acceptance criteria**:
  - Given a successful generation, when the turn completes, then each image is written to the `generated` container and persisted through US-103's path as a `ConversationDocument` with `Type = Generated`, extension `.png`, and **zero** chunk rows.
  - Given those documents, when the assistant message is persisted, then their references appear in the message's `attachments[]` array — id, name, extension, MIME type, size, type — in the order the API returned them, and no URL is written anywhere.
  - Given the storage write fails for one image in a batch, when the turn continues, then the failure is logged, the whole batch is reported as failed per US-204, and no orphaned blob is left behind — a row without a blob or a blob without a row is the inconsistency this criterion prevents.
  - Given a cancelled turn, when it unwinds, then no `ConversationDocument` row is committed and any blob already written is removed.

#### US-206: Image generation fails without taking the turn with it

- **Story**: As a chat user, I want a failed image request explained rather than silently missing, so that I know whether to rephrase or give up.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-203
- **Status**: Not started
- **Acceptance criteria**:
  - Given the API returns a content-filter rejection, when the tool returns, then the activity card renders failed with a reason and the assistant tells the user the image could not be generated — the turn still completes with an answer.
  - Given the API is unreachable, rate-limited or times out, when the tool returns, then the card renders failed, the turn still completes, and a `429` is reported as a capacity limit rather than as a rejection of what was asked — the deployment's quota is measured in requests per minute, so this is the failure a busy hour produces.
  - Given the tool fails, when the usage report is translated, then the tool-call row is written with `Succeeded = false` rather than omitted — a failed call still cost tokens.
  - Given any failure, when the logs are inspected, then no prompt text, image bytes, base64 payload or API key appears in any log statement.

### EP-3: Image input and editing

#### US-301: `[enabler]` Accept an image upload without ingesting it

- **Story**: `[enabler]` Add `.png`, `.jpg` and `.jpeg` to `FileExtensions`, add a validator path that does **not** consult `IDocumentTextExtractorFactory`, add PNG and JPEG magic-byte signatures beside the existing three, and store the row with `Type = Attachment` and no ingestion of any kind. Unblocks US-302 and US-404.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-101
- **Status**: Not started
- **Acceptance criteria**:
  - Given `UploadedFileValidator`, when an image is uploaded, then the rule requiring `extractorFactory.IsSupported(extension)` does not apply to it — that rule is what makes the whole upload surface extractor-derived, and an image is by definition a file nothing can extract text from.
  - Given `HasContentMatchingExtension`, when an image is validated, then PNG (`89 50 4E 47 0D 0A 1A 0A`) and JPEG (`FF D8 FF`) signatures are checked beside the existing `_pdfSignature`, `_zipSignature` and `_ole2Signature` — an extension is attacker-controlled, and this is the same defence the three container formats already have.
  - Given a valid image upload, when it is stored, then it lands in the existing `documents` container with `Type = Attachment`, and **no** extractor is resolved, no chunker runs, no embedding is generated and no background job is enqueued; its upload status resolves as complete immediately rather than sitting in a queue nothing will drain.
  - Given `DocumentEndpoints.GetFileExtensions`, when it is called, then it advertises the image extensions as well as the extractor-derived list — the client's root-scoped `SupportedExtensionsStore` reads this route to decide what the picker offers, so an image the API accepts but the endpoint does not advertise is unreachable from the UI.
  - Given a file named `.png` whose bytes are a renamed executable, when it is uploaded, then it is refused with a 400 `/problems/validation-error` whose `errors` dictionary is keyed by the failing property name, exactly as a renamed `.docx` already is.

#### US-302: `[enabler]` Resolve and supply source images to the edit call

- **Story**: `[enabler]` Take document ids, enforce ownership off the parent conversation, download the bytes, and enforce the API's PNG/JPG-only and per-image size limits before the call is made. Unblocks US-303, US-304 and US-305.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-301, US-202
- **Status**: Not started
- **Acceptance criteria**:
  - Given a document id, when it is resolved, then ownership is read off the **parent conversation** and a miss is a 404 `/problems/resource-not-found`, never a 403 — matching `docs/documents/download-workflow.md` §3.1 and §3.3, so a user cannot use another user's picture as an edit source by guessing an id.
  - Given a resolved document, when its bytes are fetched, then the API downloads the blob itself and passes the bytes into the multipart request; no SAS is minted, and no signed URL is ever handed to the image API — a link that leaves this process is an unrevocable credential.
  - Given a source that is not PNG or JPG, or that exceeds the per-image ceiling US-002 established, when the request is assembled, then it is refused **before** the call rather than after the API's rejection, and the refusal names the file and the rule.
  - Given several sources, when the request is built, then they are sent as the `image[]` array in one `multipart/form-data` request — not as several requests — and the count is clamped to the configured maximum.
  - Given both a `Generated` and an `Attachment` document as sources, when either is resolved, then it works identically; the discriminator selects the container to read from and changes nothing else about the path.

#### US-303: Edit an image already in the conversation

- **Story**: As a chat user, I want to point at a picture already in the conversation and ask for a change, so that I can iterate on an image instead of describing it again from nothing.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-302
- **Status**: Not started
- **Acceptance criteria**:
  - Given an image in the conversation — one the assistant generated or one the user attached — when the user asks for a change, then the assistant calls the edit surface with that image as the source and the result is persisted as a **new** `Generated` document; the source is never overwritten, so an edit is always recoverable.
  - Given the turn, when the timeline is read, then the edit renders as its own activity card with a duration, and the resulting image's identity reaches the persisted assistant message through US-105's `attachments[]`.
  - Given `input_fidelity`, when it is set, then it takes the configured default and the tool exposes it to the model only as a described parameter — `high` costs more and matters mainly for faces, so it is a deliberate choice rather than a hidden default the model discovers.
  - Given the conversation holds several images and the user's reference is ambiguous, when the turn runs, then the assistant names which image it used in its answer rather than silently picking one — editing the wrong picture is indistinguishable from a bad edit.
  - Given the source document has been soft-deleted, when the edit is attempted, then it fails with the same 404 path as a missing document and the assistant says the image is no longer available.

#### US-304: Combine or vary several source images at once

- **Story**: As a chat user, I want to hand the assistant two or three pictures and ask for one result, or for several variations, so that I get the combination in one turn instead of orchestrating it myself.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-303
- **Status**: Not started
- **Acceptance criteria**:
  - Given two or more source images within the configured cap, when the turn runs, then **one** `/images/edits` request carries all of them as the `image[]` array — the array form is what the API documents, and issuing one call per source would spend the turn's five-iteration budget on a single user request.
  - Given `n` greater than 1 alongside several sources, when the call is made, then both apply together and N results come back from one request, each persisted as its own `Generated` document.
  - Given more sources than the configured cap, when the request is assembled, then it is clamped and the answer states which images were used — an unexplained subset is worse than a stated one.
  - Given one source in the set is unusable, when the request is assembled, then the whole call is refused with a message naming that image, rather than being silently sent with the remaining ones.
  - Given the batch, when its usage row is written, then it is one row carrying the API's reported counts for the whole call.

#### US-305: An unusable source image is refused clearly

- **Story**: As a chat user, I want to be told exactly why a picture cannot be used, so that I can fix the file rather than guess at what the assistant dislikes about it.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-302
- **Status**: Not started
- **Acceptance criteria**:
  - Given four distinct refusals — wrong format, over the per-image size limit, not owned by the caller, and not an image at all — when each occurs, then the message differs and names both the file and the rule; a single generic "could not read the image" fails this criterion.
  - Given a refusal, when it reaches the user, then it arrives as a failed activity card plus the assistant's own explanation, and **no new problem type URI is introduced** — the pre-stream cases reuse `/problems/validation-error` and `/problems/resource-not-found`.
  - Given a "not owned" refusal, when it is worded, then it reads as "no longer available" rather than "you do not have permission", because the same 404 covers a document that never existed and one that was never the caller's — distinguishing them in copy would leak whether an id exists.
  - Given any refusal, when the logs are read, then no image bytes, file content or prompt text appears in them.

### EP-4: Frontend surface

#### US-401: See the images the assistant made

- **Story**: As a chat user, I want the pictures the assistant made to appear under its answer, so that I can see what I got without clicking anything.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-105
- **Status**: Not started
- **Acceptance criteria**:
  - Given an assistant message carrying `attachments[]` entries of type `Generated` with an image extension, when the transcript renders, then the pictures render inline beneath the message — one on its own at a bounded size, two to four as a grid, more than four wrapping — each as a real image, not as a file chip.
  - Given the bytes, when they are fetched, then they come from the authenticated route open question 1 settles and **no SAS token is ever placed in an `img` `src`, in store state, or anywhere in the DOM** — a signed link is an unrevocable bearer credential, and the standing posture is that it is never prefetched or persisted.
  - Given the store holding the grid's state, when it is inspected, then it is an NgRx Signal Store composing the features in `core/state/` with `withResetOnSignOut` **last**, and it is provided on the chat feature rather than in root — `withResetOnSignOut` snapshots `getState` during construction, so a state-contributing feature after it would survive sign-out.
  - Given `npm run build`, when it runs after this story, then `check-initial-chunk.mjs` shows no new module on the initial graph and the initial bundle stays under the 670 kB warning line — the baseline is **668.90 kB raw, 1.1 kB under it**, so a root-scoped store here fails the build.
  - Given a message with no attachments, when it renders, then nothing changes about it — no empty container and no placeholder.

#### US-402: Open an image full size and download it

- **Story**: As a chat user, I want to open a picture at full size and save it, so that the image ends up somewhere I can actually use it.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-401, US-104
- **Status**: Not started
- **Acceptance criteria**:
  - Given a preview tile, when it is activated, then the image opens full size in an overlay that traps focus, closes on Escape, and returns focus to the tile that opened it.
  - Given the download control, when it is used, then `DocumentDownloadStore` requests the link **at that moment** from the unchanged `GET /api/documents/conversations/{conversationId}/{documentId}` route and hands it to a detached anchor — no link is prefetched on render, and a transcript with ten images issues **zero** download requests until one is asked for.
  - Given the returned URL, when the download starts, then it is never written to store state, `localStorage`, `sessionStorage`, a router URL or a log, and it never travels through `HttpClient` with the app's `Authorization` header attached.
  - Given one image is downloading, when the rest of the transcript is used, then only that tile shows a pending state — `withPendingIds` keyed by document id, as the store already does.
  - Given a `404`, then the message reads "no longer available" rather than "deleted"; given a `503` `/problems/storage-not-configured`, then it reads as a platform configuration fact. Both behaviours are inherited from the existing store and re-asserted here.

#### US-403: Images survive a reload

- **Story**: As a chat user, I want the pictures still there when I come back to the conversation tomorrow, so that images announced only while I was watching are not lost.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-401
- **Status**: Not started
- **Acceptance criteria**:
  - Given a conversation reopened after a reload, when the transcript replays, then every image renders from the persisted message reference, in the same order, under the same message.
  - Given the reopened conversation, when a download is requested, then a **fresh** five-minute link is minted; no link from the original turn is reused, because none was ever persisted.
  - Given the activity tree, when the conversation is reopened, then it is **not** replayed — only the answer text and the images are, matching the existing rule that reopening replays the answer and never the work that produced it.
  - Given an image whose document row has since been soft-deleted, when its tile renders, then the failure state from US-405 applies and the tile reports it rather than showing a broken image.

#### US-404: Attach an image in the composer

- **Story**: As a chat user, I want to attach a picture in the composer, so that the assistant has something to work from instead of only a description.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-301
- **Status**: Not started
- **Acceptance criteria**:
  - Given the existing upload affordance in `shared/composer`, when the picker opens, then image formats are offered because `SupportedExtensionsStore` reads them from `documents/file-extensions` — the list is **not** hard-coded in the client, which is the rule that keeps the picker and the server from drifting apart.
  - Given an attached image, when its chip renders, then `fileGlyph` returns an image glyph rather than the `bi-file-earmark` fallback it returns today, and every icon name it can return is in the checked-in sprite manifest so `npm run lint`'s `check:icons` gate passes.
  - Given an attached image, when its upload completes, then it resolves as complete immediately rather than showing an ingestion progression — nothing extracts it, and a progress bar for work that will never happen is a lie about what the platform is doing.
  - Given a file over the size limit or of an unaccepted type, when it is selected, then the existing `upload-rejection` path refuses it with the server's reason, and the client-side check does not refuse anything the server would accept.
  - Given any form input this story needs, when it is built, then it uses Signal Forms (`@angular/forms/signals`); `ReactiveFormsModule` and `FormsModule` are not introduced.

#### US-405: Loading and failure states for the image surface

- **Story**: As a chat user, I want a picture that is still coming to look different from one that failed, so that I am not waiting for something that is never going to arrive.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-401, US-206
- **Status**: Not started
- **Acceptance criteria**:
  - Given a turn that asked for N images, when it is running, then N skeleton tiles render at the target aspect ratio — a request for four that shows one placeholder reads as a slow single image rather than as a batch in progress.
  - Given the four distinct outcomes — still generating, generation failed, a partial batch, and a preview whose bytes would not load — when each renders, then the treatment and the copy differ; a single generic "something went wrong" fails this criterion.
  - Given a preview whose byte request fails, when it renders, then a named placeholder with a retry appears in the tile and the rest of the grid is unaffected — one broken tile must not blank the grid.
  - Given an `ActivityFailed` on the tool's scope, when it renders, then the card shows failed with its reason and **no tile renders** for that turn.
  - Given a turn cancelled by Stop, when it renders, then the existing detached "Stopped — not saved" card appears and no tiles do.

#### US-406: Use the image surface without a mouse

- **Story**: As a chat user relying on a keyboard or a screen reader, I want to reach, open and save the images in a conversation, so that the feature is usable rather than decorative.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-401, US-404
- **Status**: Not started
- **Acceptance criteria**:
  - Given the grid, when the transcript is navigated by keyboard, then each tile is a real `<button>` in tab order, activated by Enter and Space, with a visible focus ring meeting the app's focus-indicator contrast.
  - Given a screen reader, when a tile is focused, then its accessible name states that the image was generated and gives its position in the set ("Generated image 2 of 4") — the picture itself carries no text, so the accessible name is the only thing that distinguishes one tile from another.
  - Given a grid appearing at the end of a turn, when it renders, then it is announced **once** through the transcript's existing `aria-live` region, stating how many images arrived; individual tiles are not announced, because a polite region that fires per tile is unusable.
  - Given the axe-core run on the chat route in both themes, when this story lands, then it reports 0 serious or critical violations attributable to the grid, the overlay or the composer's image affordance.
  - Given `prefers-reduced-motion: reduce`, when the skeleton tiles and the overlay render, then their animation is suppressed using the four keyframes already in the token layer; **no new keyframe is added**.

### EP-5: Usage, cost and governance

#### US-501: Image tokens and image counts are attributed to the turn

- **Story**: As an administrator, I want an image turn's cost to add up, so that a conversation's totals are not quietly missing everything the image model spent.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-202
- **Status**: Not started
- **Acceptance criteria**:
  - Given a turn that called the image tool, when the audit rows are read, then there is one `Kind = Function` row per call carrying the API's own `input_tokens`, `output_tokens` and `total_tokens`, and its `SubtreeTotalTokens` equals its own plus any descendants'.
  - Given the conversation's running totals, when they are compared against the sum of its usage rows, then they agree exactly, including what the image tool spent.
  - Given a batch of four images, when the row is read, then the **number of images** is recorded alongside the tokens, because images bill per image and a token count alone does not tell an administrator what a call cost.
  - Given a turn where the tool was never called, when the usage report is translated, then no image tool-call row is written.

#### US-502: Emit image spans and metrics

- **Story**: As an operator, I want image activity in the traces and metrics I already collect, so that a slow or failing image path is diagnosable without reading application logs.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-202
- **Status**: Not started
- **Acceptance criteria**:
  - Given a new `ActivitySource`/`Meter` pair, when it is registered, then it is added by name in `TelemetryRegistration.AddEnterpriseTelemetry`'s `WithTracing` and `WithMetrics` calls, following the one-line-per-source pattern `ChatTelemetrySourceName` already establishes.
  - Given the instruments, when they are constructed, then they live in `Enterprise.Gpt.Service` and never in `Api`, because `Service` does not depend on `Api`.
  - Given a call, when it completes, then `image.generation.duration` records elapsed time tagged with `n` and the outcome, and `image.generation.count` and `image.persisted.count` increment tagged by the same `scopeId` — these three are the direct sources for §1's latency and batch-fidelity criteria, which are otherwise unmeasurable.
  - Given any instrument, when its tags are inspected, then none carries prompt content, a file name that is user content, image bytes or a signed URL.
  - Given no telemetry backend is configured, when the app runs locally, then the instruments still exist and can be asserted in tests, matching the existing behaviour of the chat telemetry source.

#### US-503: Bound what one turn and one user can generate

- **Story**: As an operator, I want a ceiling on how many images a turn and a user can produce, so that one person's afternoon cannot become the month's cost anomaly.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-202
- **Status**: Not started
- **Acceptance criteria**:
  - Given the ceilings the product owner sets (open question 2), when they are configured, then a request that would exceed the per-turn ceiling is clamped and one that would exceed the per-user ceiling does not run, and the assistant tells the user the limit was reached.
  - Given a ceiling is unset, when the feature runs, then it behaves exactly as it does without this story, so a ceiling is opt-in rather than a surprise default.
  - Given a user at the ceiling, when they hit it, then the refusal is a stood-down tool and a plain explanation, not a 403 or a failed turn — the same shape as an implicit tool that is simply absent.
  - Given the deployment's request-per-minute quota, when the ceiling is chosen, then it is documented as a separate constraint from the per-user one: the quota bounds concurrency across every user at once, and a per-user ceiling does nothing about a busy hour.

#### US-504: Gate image generation on a permission

- **Story**: As an administrator, I want to decide who can make the platform generate images, so that a capability that bills per image is granted deliberately.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-203
- **Status**: Not started
- **Acceptance criteria**:
  - Given the permission decision from open question 3, when it is implemented, then either the existing `Upload File` grant gates the tool, or a new built-in permission is seeded in a migration, added to `PermissionIds` **and** to `PermissionIds.Names`, and granted deliberately — a new id absent from `Names` fails the endpoint filter's map-time validation at app start, which is the behaviour that prevents a nameless 403.
  - Given a user without the grant, when a turn runs, then the image tool is not attached, the assistant is told nothing about it, and nothing renders an unavailable state — the same shape as an upload affordance that is simply absent.
  - Given the grant is resolved, when it is read, then it comes from the singleton `IUserPermissionCache`, not a per-request database query, and it is invalidated per user by `PermissionService` when changed.
  - Given an administrator holding no image grant, when they ask for an image, then they do not get the capability implicitly — administrators are deliberately not granted every permission.
  - Given a user whose grant is revoked mid-conversation, when their next turn runs, then the tool is absent from that turn; a turn already streaming is not interrupted.

#### US-505: Feature-flag the capability with a documented rollback

- **Story**: As an operator, I want to switch image generation off without a deployment, so that a misbehaving image deployment or a cost spike is a configuration change rather than an incident.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-203
- **Status**: Not started
- **Acceptance criteria**:
  - Given the flag `Tools:ImageGeneration:Enabled`, when it is turned off, then no client is registered, no tool is attached, and no validator fires — the whole capability disappears, including the edit path, because both ride the same registration.
  - Given the flag off, when a conversation runs, then behaviour is identical to the pre-feature baseline and a regression test asserts it.
  - Given a fresh environment with no explicit setting, when it starts, then the flag defaults to **off**, including in development — a feature that bills per image should be switched on deliberately rather than inherited.
  - Given the flag off, when a user attaches an image, then the upload still succeeds and is still stored as an `Attachment` — image *input* is an upload-path change that does not depend on the image deployment, and coupling them would make a rollback destroy files users had already sent.
  - Given a flag change, when the app restarts, then no code change or redeploy is needed, and the rollback path is exercised at least once before production enablement rather than documented and left untested.

## 8. Milestones & rollout

**Phases**, derived from the epic dependency graph. Waves rather than weeks, because the shape of this plan is a gate, then two independent lanes, then a convergence.

| Phase | Contents | Relative estimate |
| --- | --- | --- |
| **Wave 0 — the gate** | EP-0 in full (US-001, US-002). Not a phase, a gate: the `gpt-image-1` series is limited-access, `gpt-image-2` is generally available, the edits endpoint is a different HTTP surface from generations, and no story below is estimated in detail until both close | ~0.5 week |
| **Wave 1 — two parallel lanes** | **Lane A (Storage)**: EP-1's US-101 … US-106. Touches `Entity/`, `Repository/`, `DocumentService` and the Cosmos shape, needs no Azure capability, and can therefore start during Wave 0. **Lane B (Generation)**: EP-2's US-201 and US-202 | ~2 weeks |
| **Wave 2 — attachment, batching and the first frontend surface** | EP-2's US-203 … US-206, which put the first new tool through the single funnel; EP-4's US-401 … US-403, which need only Lane A's contract and are testable against a hand-inserted `Generated` row with no image deployment in existence | ~1.5 weeks |
| **Wave 3 — image input and editing** | EP-3 in full (US-301 … US-305) plus EP-4's US-404 … US-406. The upload path and the picker are one contract with two ends and are scheduled together | ~2 weeks |
| **Wave 4 — governance and hardening** | EP-5 in full (US-501 … US-505) plus EP-1's US-107. Nothing here has an edge to anything else here, and none of it may be skipped before the feature is enabled for real users | ~1 week |

**The one real collision.** US-201 adds a registration block to `Program.cs`, and US-203 adds the first new tool to `CreateChatOptionsAsync` — the single funnel where `chatOptions.Tools` is assigned exactly once at line 1253. Those are two edits to the same two files, and worse, two people each adding a tool to that funnel can each believe they own the assignment. US-203 is sequenced into wave 2, after the registration block has settled, so the funnel gains its first new tool once and from one hand.

**Critical path.** `US-001 → US-002 → US-201 → US-202 → US-302 → US-303 → US-304`, seven deep, with `US-101 → US-301` joining ahead of US-302. Lane A is the natural place to put anyone idle during wave 0, since it needs no Azure capability at all; every link of the critical path runs through Lane B or the epic that consumes it.

**Risks & mitigations.**

| Risk | Mitigation |
| --- | --- |
| The image model is limited-access or absent in the target region | This is what wave 0 exists to find out, before any of waves 1–4 is estimated in detail. US-001 requires the alternative be reported rather than the work started on an assumption |
| The installed client libraries do not expose `/images/edits` with an image **array**, and a typed `HttpClient` over multipart is required | US-002 proves the exact call before US-201 writes a registration around it, and pins the package ids and versions that worked. The whole path is behind `Tools:ImageGeneration:Enabled`, defaulting off |
| `usage` is returned on generations but not on edits, making US-202's "verbatim counts" criterion unimplementable on half the feature | US-002 checks both surfaces explicitly. If it is absent on edits, the gap becomes a stated limitation on the audit rather than a silently zeroed row |
| The per-image size ceiling is 25 MB, not the 50 MB the how-to page states — two Microsoft pages disagree | US-002 establishes the real number and US-302 enforces it before the call. Enforcing the friendlier number would turn a local validation into a remote rejection users cannot act on |
| The deployment's 6-requests-per-minute default quota throttles a busy hour and reads as an outage | US-206 makes a `429` read as a capacity limit rather than a rejection; batching through `n` means a four-image request costs one call against that quota rather than four; US-503 bounds what one user can add to the pressure |
| A partial batch renders as a success and nobody notices images went missing | US-204's criterion makes a partial batch a **failure**, US-405 gives it its own UI state, and US-502's paired counters make it queryable — the §1 criterion has a direct source rather than an intention |
| An image is chunked by a future refactor and becomes retrievable and citable | US-106's integration test is the guard, and it is written precisely because `DocumentRetrievalSql` needs **no** change for the invariant to hold — nothing in the query would break if chunks appeared, which is what makes the regression silent |
| An inline preview puts a five-minute SAS in the DOM, persisting an unrevocable credential in the page | Open question 1 must be answered before US-401 starts, and US-401's criterion forbids a SAS in an `img` `src` outright rather than leaving it to review |
| The initial bundle crosses its budget: the baseline is 1.1 kB under the warning line | US-401 makes the lazy-chunk placement an acceptance criterion and `check-initial-chunk.mjs` fails the build; the store is provided on the chat feature, not in root |
| Image generation is enabled before the permission gate lands, making it reachable by everyone | The flag defaults **off** in every environment, US-505 asserts the pre-feature baseline with a regression test, and US-504 is the last gate before general availability |
| A user attaches an image and the assistant appears to have read it | §2 makes image-to-text an explicit non-goal, US-106 keeps the attachment out of `HasDocuments` and the injected document-name list, and open question 5 settles whether it appears in the user-facing document list at all |

**Rollout & rollback.** One flag, `Tools:ImageGeneration:Enabled`, defaulting **off** in every environment including development. With it off, a conversation behaves exactly as it does today and US-505 asserts that with a regression test. Enablement is staged: generation first, then editing, then the batch ceilings raised from a conservative default — each observed on its own before the next is layered on. Rollback is a configuration change with no redeploy, exercised at least once before production enablement rather than documented and left untested. **Image input is deliberately not behind that flag**: it is an upload-path change that does not touch the image deployment, and coupling them would make a rollback orphan files users had already sent. The schema change rides the ordinary migration path `Database.Migrate()` already runs at startup and is additive — the type column defaults every existing row to `Uploaded`, and the `attachments[]` array deserializes as empty on transcripts written before it existed — so rolling the **code** back leaves both stores readable.

**This PRD is a hard predecessor of `docs/prd/file-agent/file-agent.md`.** That PRD's "The File Agent" epic cannot start until EP-1 and EP-2 here close: it needs the document-type discriminator, the `generated` container, the non-ingesting write path and the assistant-message attachment reference, and its "embed a generated image in a document" story depends on US-202 and US-203 — one implementation of the image function, reached from two callers. The sandbox has no outbound network access, so the File Agent cannot call an image API for itself; the API places bytes into its container from what this tool returned.

## 9. Assumptions & open questions

**Assumptions.** Each is a guess a reviewer can veto.

- **`ConversationDocumentTypes` gains `Attachment = 3`** beside `Uploaded = 1` and `Generated = 2` — a user-provided file that is deliberately not ingested. Numbered from 1 and append-only, matching `JobStatus`. Without it, an uploaded image is indistinguishable from an ingested source document that merely failed to chunk, and US-106's invariant cannot be expressed as a query.
- **Uploaded images live in the existing `documents` container**, not `generated` — they are uploads. Only platform output goes to `generated`, which keeps the retention question scoped to one container.
- **No mask and no inpainting in v1.** The API supports `mask`, but it needs a region-selection UI that no board in `docs/design/` covers, and it is beyond "generate an image from an image". Carried as a non-goal in §2.
- **No image-to-text.** The assistant still cannot *see* an attached image; an uploaded image is an input to the image tool only. Multimodal chat input is a separate feature with its own model-catalog and cost consequences. Non-goal in §2.
- The discriminator goes on `ConversationDocument`, **not** `BaseDocument`, so `ProjectDocument` is untouched — inherited from `file-agent.md`, whose EP-2 this PRD absorbs.
- Generated and uploaded images are capped by the existing `Documents:MaxFileSizeBytes` (50 MB), which happens to match the image API's own per-image ceiling as stated on the how-to page. **The v1-preview REST reference states 25 MB for the same endpoint**, so US-002 establishes which is true and US-302 enforces that number; two limits for "how big may a file in this system be" is still a worse answer than one.
- Numeric defaults proposed for veto: `n` capped at **4** per call (the API allows 10), **8** images per turn, source images capped at **4** per edit call, PNG output, `1024x1024` default size, and `input_fidelity` defaulting to `low`.
- Upload formats accepted: **`.png`, `.jpg`, `.jpeg`** only — the two the API accepts, plus the common alias for one of them. WEBP is excluded because Microsoft states it is not supported in Azure OpenAI in Azure AI Foundry Models.
- Image generation defaults **off** in every environment including development; image *input* is not behind that flag, because it does not depend on the image deployment.
- The preview grid is a **new** `shared/` component rather than a variant of `attachment-chip`. A chip is a file identity and an image is the content itself; forcing one into the other gives a picture a file-name label and a 32-pixel glyph. The veto-able part is that this adds a second file-bearing component to the transcript.
- Numeric targets not supplied in the request, proposed here for veto: the p95 ≤ 25 s single-image latency, the ≥ 99% preview and download delivery rates, and the 20-prompt benchmark with its 100% thresholds in §6. US-001 is required to confirm or correct the latency figure from real timings before anything is measured against it.
- Phase durations in §8 are relative sizing derived from the story estimates, not a commitment, and assume no team size — none was specified and none is invented here.
- No new RFC 9457 problem type and no new `AssistantUiEvent` kind is introduced. Every failure this feature adds is either mid-stream — a failed activity card — or reuses `/problems/provider-not-configured`, `/problems/storage-not-configured`, `/problems/validation-error` or `/problems/resource-not-found`.

**Open questions.**

- **How does an inline preview get its bytes?** The download route mints a five-minute SAS, and the standing posture (`docs/documents/download-workflow.md` §5.4) is that a SAS is an unrevocable bearer credential that is never prefetched, persisted, or written into a URL — `file-agent.md`'s US-602 explicitly required that a transcript with ten chips issue **zero** requests until one is clicked. An `<img src="{sas}">` on every image in a transcript breaks that rule outright. The proposed assumption, stated here for veto: a new authenticated byte-streaming route, `GET /api/documents/conversations/{cid}/{did}/content`, serves previews while the SAS route stays for explicit downloads only. It costs API bandwidth on every transcript render but keeps no credential in the DOM — *product owner and backend engineer, before US-401 starts*.
- **What are the per-turn and per-user image ceilings?** Images bill per image and `n` reaches 10, so an unbounded tool is a cost incident waiting for one curious user. The deployment's own quota is a request rate, not a token rate, so it bounds concurrency across everyone at once and does nothing about one user's afternoon. US-503 implements whichever answer comes back and is P1 rather than P0 precisely because the answer is not yet given — *product owner, before US-503 starts*.
- **Reuse the existing `Upload File` permission, or add a `Generate Image` one?** Inherited verbatim from `file-agent.md`'s open question 2, which now lives here because this PRD ships first and is the first consumer. The cost profiles differ sharply: an upload costs storage and one ingestion, a generation costs a model call billed per image. That argues for a separate grant, but a second permission is a second thing to administer and a seeded row that can never be renumbered. US-504 is written to take either — *product owner, before US-504 starts*.
- **Does the `generated` container get a lifecycle policy that `documents` lacks, and what is the window?** Inherited from `file-agent.md`'s open question 3. Blobs are never reclaimed on soft delete today — the code defers it to a retention job that does not exist — and generated images will accumulate faster than uploads, four at a time. US-107 needs a number — *product owner and operator, before US-107 starts*.
- **Do uploaded images appear in the conversation's user-facing document list?** A user who attaches a photograph sees a file the assistant cannot read, sitting beside documents it can. Showing it is honest about what the conversation holds and invites the assumption that the assistant read it; hiding it makes an uploaded file invisible to the person who uploaded it. The answer changes US-301's response shape and the composer's chip behaviour — *product owner, before US-301 starts*.
