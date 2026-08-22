# PRD: Image Input in Conversations

## 1. Overview

**Problem.** Enterprise GPT holds one kind of turn: text in, text out. `Enterprise.Gpt.Dto/Enums/FileExtensions.cs` has 26 values and none of them is an image, `Service/Validators/UploadedFileValidator.cs` admits a file only when `IDocumentTextExtractorFactory.IsSupported(extension)` is true, and `ConversationService.cs:855-858` assembles every `ChatMessage` with the `(ChatRole, string)` constructor overload — there is no other call site in the whole solution, test code included, that builds a `ChatMessage` any other way. A user who wants the assistant to look at a screenshot, a whiteboard photo, a scanned form, or a chart cannot put one into this platform at all; they either paste a description by hand or leave. `Model.cs` carries exactly two capability flags, `IsToolEnabled` and `IsReasoningEnabled`, and neither says anything about whether a deployment can see a picture.

Two further facts shape the whole design and are why this is the *foundation* PRD rather than a small add-on. First, `docs/prd/image-tool/image-tool.md` — a sibling PRD for image **generation**, zero stories implemented — already assumed it would build the document-type discriminator, a container, the non-ingesting write path and the message-level attachment reference for its own generated pictures. It got the dependency backwards: a platform that cannot accept an image someone hands it has no business generating one it then can't look at either, and building the foundation twice — once for output, once for input — is the redundant path. This PRD builds it once, for input, first; `image-tool` consumes it unchanged when its turn comes, exactly as `docs/prd/file-agent/file-agent.md` already consumes `image-tool`'s planned foundation. Second, everything a document does today attaches to the **conversation** — `ConversationDocument.ConversationId` — never to one specific message. An image is different in a way that matters immediately: the user says "what's in this picture" pointing at *this* upload, not at every file the conversation has ever seen, so the reference has to live on the transcript message itself, something `TranscriptMessageDocument` has never carried (`Content` is a bare `string`).

**Solution.** A user attaches an image through the existing upload affordance; it is validated by its magic bytes, stored in a new `images` blob container, and referenced on the message that carried it — never re-ingested through the document pipeline, never chunked, never retrievable. A background job — the same `Channel<JobWorkItem>` / `BackgroundJobProcessor` / `JobStatusStore` infrastructure ingestion already runs on, with a new stage appended rather than a new pipeline built — calls a separately configured vision deployment once, producing a natural-language description and any text the image contains (OCR), and persists both directly on the `ConversationDocument` row, since an image structurally has no chunks to hold them. From that moment the image has a **permanent, cheap, textual presence** in every future turn's assembled prompt: it is never re-explained, and it is never silently absent, however long the conversation runs. On top of that baseline, a **bounded** number of the most recent images are also re-attached as real pixels — genuine `DataContent` on the assembled `ChatMessage` — to a vision-capable model, so a question that needs actual pixels ("what's the value at the red dot", "read the handwriting in the margin") can still be answered instead of failing against a caption written before anyone asked it. No image is ever generated; that capability is `image-tool`'s alone.

**Why hybrid, argued rather than asserted.** The user flagged the reinsertion question as "a point of discussion we need to argue," so here is the argument, not just the conclusion.

- **Extraction-only is lossy in a way nothing can recover.** The description is written once, at ingest, before anyone knows what will be asked. A generic caption survives "what is this?" and fails every question that needs the actual pixels — a value at a marked point, a row count in a photographed table, handwriting in a margin. The user experiences this as the model going blind after the first message about the image, and because the original bytes were never sent again, no amount of rephrasing fixes it — the model is reasoning from a paragraph, not a picture.
- **Always re-attaching is faithful and unbounded.** Image content is billed as input tokens on every turn it rides, and it does not shrink as a conversation grows: a ten-turn conversation holding three images that are always re-attached pays for thirty image renders, all of it landing inside a window `Model.ContextWindowSize` has to cover alongside everything else the turn needs. `ApplyContextBudget`/`PromptOverheadCalculator` (`ConversationService.cs:1503-1561`, `Tokenization/PromptOverhead.cs`) already have no term for this at all — an unbounded re-attachment policy would make that gap a certainty rather than a risk.
- **The hybrid keeps both properties, and neither replaces the other.** Extraction guarantees an image is never *absent* from the model's awareness, at a fixed, small, predictable cost, for the life of the conversation. Re-attachment is a bounded, optional upgrade on top — real pixels for the images most likely to still be under discussion, on a model that can use them, inside a budget the context-budget calculator can see and enforce. Dropping either half loses a property the other cannot supply: drop extraction and old images go blind; drop re-attachment and no question can ever be answered at pixel fidelity.

The bounding rules this PRD proposes — each independently vetoable, and each with a story that implements exactly the rule the product owner confirms:

- Real pixels are only ever sent to a model whose catalog row sets the new `Model.IsVisionEnabled` — never assumed, and never silently degraded to "sent anyway and hope the provider ignores it," because `IsReasoningEnabled`'s own doc comment already records the failure mode: *a deployment that does not support an option rejects the whole request rather than ignoring it*. An unsupported image part is the same failure shape.
- Only the most recent **N** images in the conversation are re-attached (proposed default: 2 — open question, §9).
- Only within a per-turn image-token budget derived from `Model.ContextWindowSize` (open question, §9) — the new term `ApplyContextBudget` is missing today.
- The extracted text is **always** included for **every** image the conversation holds, whether or not its bytes were re-attached this turn — so degradation from "sees the picture" to "remembers what it was told about the picture" is graceful and stated, never a silent hole in context.

**A user, end to end.** Priya drags a photo of a whiteboard into the composer. It uploads through the same chip and progress bar a PDF would use, but its status moves through `Uploading` → a new `Describing` stage → `Processed` in a few seconds, because nothing chunks or embeds it — there is no retrieval corpus for a whiteboard photo to join. She asks "turn the left column into a task list." The assistant's prompt for that turn carries the image's own description and any handwriting the vision call read off it, plus — because this is one of the two most recent images in the conversation and the selected model is vision-enabled — the actual picture, so it can read the specific words rather than a paraphrase. Six turns later, after she has attached two more diagrams, she asks "what was in that first whiteboard photo again?" The raw bytes are no longer re-attached — she is past the bound — but the extracted description and text are still right there in the prompt, because they ride on every turn regardless of age, and the assistant answers correctly instead of saying it cannot recall an image from earlier in the conversation.

**Success criteria.**

- **Context permanence**: 100% of turns in a conversation holding at least one successfully processed image include that image's extracted description in the assembled prompt, for the life of the conversation — measured by an integration test that attaches one image at turn 1 and asserts the extracted text is present in the `ChatMessage` list `ConversationService` assembles at turn 10, and by 0 turns where a `ConversationDocument` with `Type = Attachment` and a non-null `Description` is absent from that turn's assembled content.
- **Re-attachment boundedness**: 0 turns whose image-token contribution to the assembled prompt exceeds the configured per-turn image budget — measured by a unit test suite over `ApplyContextBudget`'s new image term across conversations holding more than the re-attachment ceiling, and by the `image_input.reattached_tokens` histogram (§6) never exceeding the configured ceiling in production.
- **Retrieval isolation**: 0 rows in `Core.ConversationDocumentChunk` for any `ConversationDocument` with `Type = Attachment`, and 0 citations in any answer name one — measured the same way `image-tool`'s US-106 pins it: a scheduled query over the chunk table and an integration test asserting the invariant directly.
- **Processing visibility**: ≥ 99% of image uploads reach a terminal `state` (`Succeeded` or `Failed`) reported by `GET api/documents/upload-status/{jobId}` within a p95 of 15 seconds — measured by the same job-duration telemetry ingestion already emits, filtered to jobs whose document is `Type = Attachment`.
- **Format integrity**: 100% of accepted uploads are `.png`, `.jpg` or `.jpeg` validated by leading magic bytes rather than extension alone, and 0 files whose declared extension does not match their content signature are ever persisted — measured by `UploadedFileValidatorTests`' new theory cases and a scheduled audit query comparing `ConversationDocument.Extension` against a magic-byte re-scan of a sample.

## 2. Goals & non-goals

**Goals.**

- Accept an image upload, in the formats a target vision-capable deployment actually accepts (settled by EP-0, proposed `.png`/`.jpg`/`.jpeg`), validated by content rather than by a caller-chosen file name.
- Extract what an image shows — a description and any legible text — once, at ingest, so the platform never has to re-send the original bytes just to remember what a picture contained.
- Keep that extracted presence permanent: every image a conversation holds contributes to every future turn's prompt, for as long as the conversation exists, at a bounded and predictable token cost.
- On top of that floor, re-attach genuine image bytes to a vision-capable model for a bounded, recent subset of images, so a question that needs actual pixels can still be answered.
- Store an uploaded image the way a generated one will be stored: in its own container, as a `ConversationDocument` row, with an identity a message can reference — the foundation `docs/prd/image-tool/image-tool.md` assumed it would build for itself, built once, here, first.
- Show the user that an image is being processed, through the upload status mechanism that already exists — no new SSE frame kind, no new polling loop.
- **Reverse the dependency `docs/prd/image-tool/image-tool.md` assumed.** That PRD's §9 assumed uploaded images would land in the existing `documents` container and that its own US-101/US-105/US-106/US-301 (and part of US-404) would build the discriminator, the message reference, the retrieval exclusion and the non-ingesting upload path. This PRD ships all four first, for a different container and a different purpose (understanding, not sourcing a generation), and `image-tool` is expected to consume them unchanged rather than rebuild them. **Amending `image-tool.md` itself is out of scope here** — flagged as a follow-up in §9.
- Keep the client's initial bundle under budget: every surface this PRD adds rides the lazy `chat` chunk.

**Non-goals.**

- **Image generation, in any form.** That is `docs/prd/image-tool/image-tool.md` in full — the `generate_image` tool, the `generated` container, the inline preview grid. Nothing here creates an image; everything here is about a picture the user already has.
- **A second retrieval corpus for images.** An image is never chunked, embedded, or made searchable. `document_search` and `Tool/DocumentRetrievalSql.cs` see nothing this PRD adds; the retrieval-isolation success criterion above exists to keep it that way.
- **General-purpose image editing, cropping, or annotation.** The picture is read, not touched.
- **Video, audio, or any non-still input.** One still frame per attachment; nothing here processes a moving image or extracts frames from one.
- **WEBP, HEIF, BMP, TIFF or any format beyond what EP-0 confirms.** Microsoft's own documentation disagrees with itself across pages on the accepted set for Azure OpenAI/Azure AI Foundry vision models, and `image-tool` §2 already excludes WEBP outright, citing Microsoft stating it is unsupported in Azure OpenAI in Azure AI Foundry Models. This PRD proposes `.png`, `.jpg`, `.jpeg` for the same reason `image-tool` does — so the two features cannot disagree on what an "image" is — gated on EP-0 confirming it against the real target deployment.
- **A user-facing usage/cost API.** `ConversationUsage` has no HTTP surface today; this PRD does not invent one. Ingest-time vision-call cost and re-attachment token cost are reported through telemetry (EP-6), not a new endpoint.
- **New `AssistantUiEvent` kinds or any change to the vendored `Andes.Extensions.AI.UI` contract.** Processing feedback is served by extending the existing upload-status polling path, not by adding stream frames — the user's own requirement is satisfied without touching the frozen contract `npm run check:contract` guards.
- **New RFC 9457 problem type URIs.** Every failure this feature introduces reuses `/problems/validation-error`, `/problems/resource-not-found`, `/problems/storage-not-configured`, or is an asynchronous job failure surfaced through the existing `errorMessage` field — exactly as document ingestion's own failures are.
- **Editing or amending `docs/prd/image-tool/image-tool.md`.** That document's superseded stories (§9) are recorded here as a fact about this PRD's scope; updating the sibling document itself is deliberately left to a separate, later pass.
- **Documentation stories.** Docs under `docs/` and the `CHANGELOG.md` entry are the SE Technical Writer flow's job, after implementation.

## 3. Users & access

**Personas.**

- **Chat user**: a signed-in employee holding a conversation. Attaches an image the way they would attach a document today, and never interacts with the vision deployment, the extraction job, or the re-attachment policy directly — the assistant simply knows about the picture.
- **Administrator**: holds `PermissionIds.Administrator` (`a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d`). Marks a catalog model as vision-capable and grants or revokes the image-input permission.
- **Operator**: runs the deployment. Configures the ingest-time vision deployment, holds the feature flag, sets the re-attachment ceiling and the retention window, and reads the telemetry this PRD adds.
- **Backend engineer**: owns EP-0 through EP-4 and EP-6, working in `enterprise-gpt-api/` under `.claude/rules/csharp.md` and `aspnet-rest-apis.md`.
- **Frontend engineer**: owns EP-5, working in `enterprise-gpt-ui/` under the `ngrx-signal-store` and `angular-developer` skills.

**Role-based access.**

- **Anonymous**: no access. Every route this feature touches is inside a group already carrying `.RequireAuthorization()`.
- **Chat user without the image-input grant**: conversations behave exactly as they do today. The composer's upload affordance does not offer image files, because `GET api/documents/file-extensions` does not advertise them to a caller who cannot use them — mirroring how the codebase already gates the upload affordance itself rather than producing a 403 on a resource the caller never named.
- **Chat user with the grant**: can attach an image to any conversation they own, see it processed, and have it participate in every future turn — through the same ownership rule that already serves uploaded documents. A miss on the parent conversation is a 404, never a 403 (`docs/documents/download-workflow.md` §3.1, §3.3).
- **Administrator**: grants and revokes the permission through the existing `api/permissions` surface, and marks a catalog model `IsVisionEnabled` through the existing admin models screen. Administrators are **not** implicitly granted the image-input permission, matching the codebase's standing rule that an administrator holds only what they were given.

The grant is resolved through the singleton `IUserPermissionCache`, the same path `PermissionEndpointFilter` uses, and is invalidated per user by `PermissionService` exactly as every other grant already is. Whether it is the existing `Upload File` permission (`b1c2d3e4-f5a6-4b7c-8d9e-0f1a2b3c4d5e`) or a new `Understand Image` grant is **open question 3** (§9) — inherited from `image-tool`'s own open question 2, which now lands here because this PRD ships first; US-601 ships whichever the product owner picks and states both consequences.

## 4. Functional requirements

| ID | Requirement | Priority | Epic(s) |
| --- | --- | --- | --- |
| FR-1 | A vision-capable chat completion — real image content on a `ChatMessage`, not text describing one — is proven callable from .NET for at least the primary target provider, with the exact package versions and accepted formats recorded | P0 | EP-0 |
| FR-2 | The ingest-time vision call's feasibility, latency and per-image size ceiling are established against a real deployment before any code depends on them | P0 | EP-0 |
| FR-3 | `ConversationDocument` carries a document-type discriminator (`Uploaded = 1`, `Attachment = 2`, reserving `3` for `image-tool`'s future `Generated`); every existing row is backfilled to `Uploaded` | P0 | EP-1 |
| FR-4 | An uploaded image is persisted as a `ConversationDocument` with `Type = Attachment` in a new `images` blob container, with **zero** chunk rows | P0 | EP-1 |
| FR-5 | An image is never extracted through the document pipeline, chunked, embedded, retrieved, ranked, or cited | P0 | EP-1 |
| FR-6 | An uploaded image is validated by its leading magic bytes, not by its extension alone | P0 | EP-1 |
| FR-7 | The accepted-format surface widens to include images without breaking the invariant that an *unaccepted* format is never advertised — while accepting that "accepted" no longer means "extractable" | P0 | EP-1 |
| FR-8 | An image downloads through the existing document download route with the correct name and content type; no signed URL is ever persisted, streamed, or logged | P1 | EP-1 |
| FR-9 | An inline preview of a stored image is served without ever placing a SAS in the DOM | P1 | EP-1 |
| FR-10 | Images are reclaimed under a retention policy rather than accumulating indefinitely in the `images` container | P2 | EP-1 |
| FR-11 | The model catalog carries a vision capability flag, off by default, that a deployment not supporting it would otherwise reject the whole request for | P0 | EP-2 |
| FR-12 | An administrator can mark a catalog model as vision-capable from the existing admin screen | P1 | EP-2 |
| FR-13 | Ingest-time image understanding runs against a separately configured deployment, independent of whichever model the conversation's user has selected | P0 | EP-3 |
| FR-14 | A processed image carries a persisted natural-language description and any legible text (OCR) the vision call produced | P0 | EP-3 |
| FR-15 | An image whose understanding call fails surfaces as a failed job with a sanitized message, and does not corrupt the conversation or block the attachment from existing | P1 | EP-3 |
| FR-16 | The ingest-time vision call is bounded in wall-clock time so a stalled call cannot hold a processing slot indefinitely | P1 | EP-3 |
| FR-17 | The stream request carries the ids of images attached to the turn's prompt | P0 | EP-4 |
| FR-18 | A transcript message carries a structured reference to every image it introduced — id, name, extension, MIME type, size, type — and never a URL | P0 | EP-4 |
| FR-19 | `ChatMessage` assembly widens to multimodal content, so an image can ride an assembled message alongside its text | P0 | EP-4 |
| FR-20 | Every image a conversation holds contributes its extracted description to every subsequent turn's prompt, regardless of how long ago it was attached | P0 | EP-4 |
| FR-21 | The context budget accounts for image tokens; a turn is never authorised against a size the provider will then reject because of an image the budget could not see | P0 | EP-4 |
| FR-22 | Real image bytes are re-attached only for the most recent N images, only to a vision-enabled model, and only within a per-turn image-token budget | P1 | EP-4 |
| FR-23 | A turn on a non-vision-enabled or non-tool-enabled model degrades to text-only image context and never fails outright for that reason alone | P0 | EP-4 |
| FR-24 | Every export format renders a sensible answer for a message that carries an image attachment | P2 | EP-4 |
| FR-25 | A user can attach an image through the existing composer upload affordance | P0 | EP-5 |
| FR-26 | The image's processing state is visible and distinguishable from a document's (uploading, processing, ready, failed, unsupported) | P0 | EP-5 |
| FR-27 | An attached image renders a thumbnail, not merely a generic file chip, in the composer and in the transcript | P1 | EP-5 |
| FR-28 | The composer explains, without a new panel, when the selected model cannot make use of an attached image | P1 | EP-5 |
| FR-29 | An unusable image is refused with a reason naming which rule it failed | P1 | EP-5 |
| FR-30 | Every image surface is operable by keyboard and announced to assistive technology | P0 | EP-5 |
| FR-31 | No component this PRD adds reaches the initial bundle graph | P0 | EP-5 |
| FR-32 | Image understanding is gated on an explicit permission grant, resolved through `IUserPermissionCache` | P0 | EP-6 |
| FR-33 | The capability is behind a configuration flag with a documented rollback that requires no redeploy | P0 | EP-6 |
| FR-34 | What one conversation and one user can upload, and how many images are re-attached per turn, is bounded | P1 | EP-6 |
| FR-35 | Ingest-time vision-call activity and re-attachment token cost emit spans and metrics through the existing OpenTelemetry registration | P1 | EP-6 |

## 5. User experience

**Entry points & first-time flow.** There is no new screen and no new menu item. A user with the grant sees the composer's existing upload affordance now offer `.png`/`.jpg`/`.jpeg` alongside the document formats it already accepts, because `GET api/documents/file-extensions` advertises them. A user without the grant sees no difference from today — nothing renders an unavailable-feature panel, because there is no surface that promised one.

**Core experience.**

1. The user attaches an image through the composer's existing drop target or file picker (`shared/upload/file-drop-target.ts`, `shared/upload/drop-overlay.ts`). It uploads through `DocumentUploadClient`, the same raw `XMLHttpRequest` path a document uses, because `HttpClient` with `withFetch()` cannot report upload progress.
2. The attachment chip shows a thumbnail rather than a generic file glyph the moment the bytes are read locally, and its state moves through the existing `AttachmentState` ladder — `uploading` → `processing` → `ready`, or `failed` / `unsupported` — exactly as a document's chip does today, because `fileGlyph()` and `AttachmentState` already anticipate this shape.
3. `UploadStatusClient` polls `GET api/documents/upload-status/{jobId}` on the same 500 ms → 5 s staged schedule (`domain/documents/upload-schedule.ts`) a document upload already uses. The stage the user sees move is different — `Uploading` → a new `Describing` stage → `Processed` — but the polling code, the coarse `state` projection, and the 404-means-expired reading are all unchanged.
4. Once `Processed`, the user sends a prompt. Nothing in the composer or the send flow changes shape: the stream request now also carries the attached image's id, and the assistant's answer streams exactly as it does today — no new activity card, no new `AssistantUiEvent` kind, because image context rides silently on the prompt rather than announcing itself as a tool call.
5. Later in the same conversation, an older image's actual bytes are no longer re-attached, but the assistant still knows what it showed, because the extracted description rides every turn regardless of age. Nothing in the transcript or the composer indicates this transition — the user experiences it only as "the assistant still remembers," which is the point.

**Edge cases & UI states.**

- **Uploading, then processing**: the chip's sub-status line comes straight from `JobStatusDto.message`, unchanged — "Reading image…" during `Describing` instead of "Extracting…", because the message is server-authored per stage and the client renders it verbatim.
- **Unsupported format**: an attempt to attach a `.webp` or any format outside the accepted set is refused by the same `unsupported` chip state a document's unsupported extension already produces, with `documents/file-extensions`-derived messaging naming what *is* accepted.
- **Corrupt or mislabeled file**: a `.png` extension whose bytes do not start with the PNG signature is refused as a content/extension mismatch, the same 400 shape a renamed `.docx` gets today.
- **Vision call fails**: the job reaches `Failed` with a sanitized `errorMessage` ("The image could not be understood. Try a clearer photo or a different file.") — the attachment still exists as a stored file the user can see, it simply carries no extracted description, and the composer's note ladder states that this image could not be understood rather than pretending nothing was attached.
- **Model cannot use images**: selecting a model that is not `IsVisionEnabled` (or not `IsToolEnabled`, for the same reason document retrieval already stands down) does not block sending. The composer's existing note ladder — which already carries "This model can't read attachments" — extends to state that the assistant will use the image's description but not the picture itself.
- **Feature off, or no permission**: the upload affordance simply does not offer image formats; there is no "image input is unavailable" panel, because there is no surface that promised one.
- **Busy**: a second turn while one is streaming still returns 409 `conversation-busy`, unaffected by anything this PRD adds.
- **Reload**: the image's thumbnail and its presence in the assistant's context both survive a reload, because the reference lives on the persisted transcript message, not in session state.

**UI/UX highlights.**

- The image renders as a **thumbnail inside the existing attachment chip surface** — `shared/chip/attachment-chip/` — not a new dedicated component. Unlike `image-tool`'s assistant-generated preview grid (built for several images the assistant just produced in one turn), this is the user's own single upload appearing where any attachment already appears: in the composer before send, and beside the message that carried it after. `attachment.model.ts`'s `fileGlyph()` table, which today covers nine extensions and none of them an image, gains the three new ones; every icon it returns must already be in the checked-in sprite manifest, which is what `npm run lint`'s `check:icons` gate enforces.
- **Whoever builds `image-tool`'s frontend epic next should reuse this PRD's thumbnail treatment rather than build a second one for the assistant's own output.** This is a coordination note, not a dependency: `image-tool` has zero stories implemented today, so there is nothing to reconcile yet, but the two surfaces solve the same underlying problem — rendering a picture instead of a file name — and a divergent second implementation would be the first place the two features visibly disagree.
- Keyboard and ARIA: the chip is already a real element in tab order; the thumbnail adds no new interactive surface beyond what the chip already provides, so no new focus trap or live region is introduced. The composer's existing note ladder is where the "can't read attachments" / "will read this from memory, not the picture" messaging lives, and it is already announced through the mechanism the composer uses for every other rung.
- `docs/design/` carries no board for an image thumbnail inside a chip; the closest is `02 Composer States.dc.html`. The treatment is derived from the existing chip's spacing and `theme.css` tokens rather than quoted from a frame.

## 6. Technical considerations

**Three verified facts shape everything below.**

**1. `ChatMessage` is built in exactly one place, with no multimodal content anywhere in the codebase.** `ConversationService.cs:855-858` assembles the turn's history with `new ChatMessage(MappingService.MapToChatRole(x.Role), x.Content)` for every replayed message and `new(ChatRole.User, request.Prompt)` for the current one — the `(ChatRole, string)` overload. A repository-wide search for `DataContent` and `UriContent` — the Microsoft.Extensions.AI types that carry image bytes or a reference to them on a message — returns **zero** matches anywhere, including every test project. This seam has to widen to the `IList<AIContent>` overload, and every downstream reader of a `ChatMessage`'s content has to tolerate the new shape.

**2. Documents attach to the conversation; nothing attaches to a message.** `ConversationDocument.ConversationId` is the only linkage a document has ever needed, because "what documents does this conversation hold" was always the right question for retrieval. An image needs a different question answered — "what did *this message* attach" — and `TranscriptMessageDocument` (`Entity/Transcripts/TranscriptDocuments.cs`) has no field for it: `Content` is a bare `string`, and the factory that builds one takes `string content`. `TranscriptHeaderDocument.CurrentSchemaVersion = 1` exists for exactly this kind of change, and Cosmos's 2 MB item cap rules out inlining bytes, so the new field must be a reference — id, name, extension, MIME type, size, type — joining `/content/*` and `/htmlContent/*` on the indexing-policy exclusion list (`docs/conversations/transcript-storage.md:162`).

**3. The context budget has no term for an image, and that is a P0 correctness gap, not a nicety.** `ApplyContextBudget` (`ConversationService.cs:1503-1561`) estimates every replayed message's tokens through `BudgetMessage(Role, Tokens)` and adds `PromptOverheadCalculator.Estimate(messageCount, toolCount, instructionTokens)` (`Tokenization/PromptOverhead.cs`) for framing and tool schemas. Neither term has any notion of an image. Left alone, re-attaching a picture would make `ApplyContextBudget` authorise a turn whose real size the provider then rejects — the exact failure mode the budget exists to prevent for everything else.

**Integration points.** All verified against the working tree.

| Concern | Where |
| --- | --- |
| The single `ChatMessage` assembly seam | `Enterprise.Gpt.Service/ConversationService.cs:855-858` |
| Enforcement precedent for a capability flag | `Model.cs:46`'s `IsReasoningEnabled` doc comment: *"a deployment that does not support it rejects the whole request rather than ignoring the flag"* — the same failure mode an unsupported image part produces |
| Stand-down precedent for an implicit capability | `ConversationService.cs:2173-2183` — a non-tool-enabled model logs a warning and continues without document retrieval, never a 400 |
| Throw precedent for an explicit mismatch | `ConversationService.cs:2186-2193` — `ValidationException` naming the offending request property when the caller explicitly asked for something the model cannot do |
| The context budget to extend | `Enterprise.Gpt.Service/Tokenization/ContextBudgetCalculator.cs` (`BudgetMessage(Role, Tokens)`), `Tokenization/PromptOverhead.cs` (`PromptOverheadCalculator.Estimate`), `Settings/TokenEstimationOptions.cs` (the settings class a per-image token estimate joins, beside `PerMessageFramingTokens` and `PerToolSchemaTokens`) |
| The stream request DTO to widen | `Enterprise.Gpt.Dto/Actions/Chat/ConversationActions.cs:39-64` (`CreateConversationStreamActionDto { Prompt, ModelId, McpServers }` and its `FluentValidation` validator); client body builder at `core/stream/conversation-stream-client.ts:191-194` |
| Transcript message shape to extend | `Enterprise.Gpt.Entity/Transcripts/TranscriptDocuments.cs` — `TranscriptMessageDocument` (no attachment field today), `TranscriptHeaderDocument.CurrentSchemaVersion` (`= 1` today) |
| Document entity and its EF configuration | `Enterprise.Gpt.Entity/ConversationDocument.cs`, `BaseDocument.cs` (`UserId`, `Name`, `Extension`, `MimeType`, `Size`, `Path` — no discriminator today), `Repository/Configurations/ConversationDocumentConfiguration.cs` |
| Upload validation to extend | `Enterprise.Gpt.Service/Validators/UploadedFileValidator.cs` — `HasContentMatchingExtension` (line 94) and its extension-acceptance rule (line 72), which routes through `IDocumentTextExtractorFactory.IsSupported` and has no notion of a format that is accepted but not extractable |
| Advertised formats | `Enterprise.Gpt.Api/Endpoints/DocumentEndpoints.cs` — `GetFileExtensions`, mapped at `documents/file-extensions`, today `extractorFactory.SupportedExtensionNames` verbatim |
| The catalog-wide "acceptable = extractable" enum | `Enterprise.Gpt.Dto/Enums/FileExtensions.cs` — 26 values today, several (`.cs`, `.py`, `.json`, `.xlsm`, …) already unused by any extractor, which is the existing precedent for adding a value nothing extracts from |
| Retrieval candidate set, which this feature must never enter | `Enterprise.Gpt.Service/Tool/DocumentRetrievalSql.cs` (a `UNION ALL` over the two chunk tables); `Tool/DocumentRetrievalService.GetScopeAsync` (`HasDocuments`, line 190) |
| Content-type map for downloads | `Enterprise.Gpt.Service/DocumentService.cs` — `ResolveContentType` over a `FrozenDictionary` covering six extensions today, none of them an image |
| Container configuration precedent | `Enterprise.Gpt.Service/DocumentService.cs:195-211` — the `DocumentsContainer` property reading `AzureStorage:DocumentsContainer` on every use, throwing `StorageNotConfiguredException` (→ 503 `/problems/storage-not-configured`) when unset |
| Signed link generation | `Enterprise.Gpt.Service/BlobStorageService.cs` — `GenerateSasUri`, guarded by `CanGenerateSasUri` |
| Download route and its contract | `Enterprise.Gpt.Api/Endpoints/DocumentEndpoints.cs` — `GET conversations/{conversationId:guid}/{documentId:guid}`; full contract in `docs/documents/download-workflow.md` |
| Cross-conversation document listing | `Enterprise.Gpt.Api/Endpoints/DocumentEndpoints.cs` — `GetUserDocumentsAsync` at the bare `GET api/documents` route, unfiltered by any type today (open question 5, §9) |
| Background job infrastructure to extend, not replace | `Enterprise.Gpt.Service/BackgroundJobs/` — `BackgroundJobQueue` (bounded `Channel<JobWorkItem>`), `BackgroundJobProcessor` (`SemaphoreSlim`-gated `BackgroundService`), `JobStatusStore` (in-memory, monotonic progress) |
| Job stage enum and its append-only rule | `Enterprise.Gpt.Dto/Enums/JobStatus.cs` — `Queued=1 … Persisting=8`, frozen and appended never renumbered; the `_ => "Processing"` coarse-state default (`DocumentEndpoints.MapState`) is what makes appending a new stage safe with no client change |
| Existing per-provider request shaping | `Enterprise.Gpt.Api/Chat/AzureOpenAIChatDefaults.cs`, `AzureAIFoundryChatDefaults.cs`, `AnthropicChatDefaults.cs` — deliberately confined here to keep SDK request types out of the provider-agnostic `ChatOptions` |
| Keyed chat client resolution, reused for the ingest-time vision call | `ConversationService.cs` — `_chatClientResolver.Resolve(model.ProviderId)`; the ingest call resolves the **same** keyed client for the configured vision provider and supplies a distinct deployment name, rather than registering a whole second SDK client |
| Options-class and registration shape to copy | `Enterprise.Gpt.Service/Settings/WeatherToolOptions.cs` (`public const string SectionName`), `Program.cs` lines 197-228 (`AddOptions<T>().Bind(...).Validate(...).ValidateOnStart()` guarded by an `Enabled` flag) |
| Configuration precedent for "a separately configured deployment, not the user's selection" | `DocumentIntelligence:Endpoint` / `:ApiKey` (`Program.cs:477-482`) — no dedicated options class, read directly from `IConfiguration`, used for every ingestion regardless of which chat model the conversation uses |
| Telemetry to extend | `Enterprise.Gpt.Service/Observability/ChatMetrics.cs` (`Meter`, `Histogram<double>`, `Counter<long>`, no high-cardinality dimension), `Api/Observability/TelemetryRegistration.cs` (`AddEnterpriseTelemetry`) |
| Permission ids and their names | `Enterprise.Gpt.Dto/Enums/PermissionIds.cs` — `Administrator`, `UploadFile`, and the `Names` `FrozenDictionary` validated at map time |
| Test double that must track any `IBlobStorageService` change | `tests/Enterprise.Gpt.Integration.Test/TestInfrastructure/FakeBlobStorageService.cs` |
| Client upload path and the format list it reads | `core/documents/upload-store.ts` (`MAX_CONCURRENT_UPLOADS = 3`), `document-upload-client.ts`, `upload-status-client.ts`, `domain/documents/upload-schedule.ts`, the root-scoped `supported-extensions-store.ts` |
| Client chip, glyph table and composer | `shared/chip/attachment-chip/` (`attachment.model.ts`'s `fileGlyph`, `AttachmentState` already `uploading\|processing\|ready\|failed\|unsupported\|unknown`), `shared/composer/` (the `note()` ladder, already carrying a "can't read attachments" rung), `shared/upload/`, `core/chat/composer-host.ts` |
| Export | `Enterprise.Gpt.Service/Export/ConversationExportService.cs` — renders `html`/`md`/`json`/`pdf`/`docx`, none of which has an answer for an image attachment today |

**Data storage & privacy.**

- **The type discriminator goes on `ConversationDocument`, not `BaseDocument`**, so `ProjectDocument` is untouched — the symmetry is deliberately deferred, matching `image-tool`'s own decision for the same reason. `ConversationDocumentTypes` lives in `Enterprise.Gpt.Dto/Enums/` beside `JobStatus` and `FileExtensions`, is numbered from 1, and is append-only: `Uploaded = 1`, `Attachment = 2`. **The value `3` is deliberately reserved, not used** — `image-tool`'s own `Generated` value, when that PRD ships, appends there rather than colliding with whatever this PRD chose. The column is configured in `ConversationDocumentConfiguration` with `HasConversion<int>()`, and the column name equals the property name — no `HasColumnName` exists anywhere in the repository, and the hand-written SQL in `Tool/DocumentRetrievalSql.cs` depends on that holding. The migration follows `20260821224810_AddConversationUsageReportingIndex`, the most recent migration in `Enterprise.Gpt.Repository/Migrations/`.
- **Two new nullable `nvarchar(max)` columns on `ConversationDocument`: `Description` and `OcrText`.** Neither lives in a chunk table, because an image must never have chunk rows (the retrieval-isolation invariant) and there is exactly one row per image, so a child table would be pure overhead. `Description` is the vision call's natural-language account of the image; `OcrText` is any text it read off the image, `null` when it found none — kept separate because a UI might want to show one without the other, and because "the image contains no legible text" is a fact worth being able to state distinctly from "the image could not be described at all."
- **Uploaded images live in a new `AzureStorage:ImagesContainer` container, not `documents`.** This **overrides** `image-tool` §9's assumption that uploaded images would live in the existing `documents` container. Rationale: an uploaded image's retention profile (open question 4, §9) is not the same as a document's, and giving it a distinct container from day one avoids a later migration to separate them. `image-tool`'s own `generated` container remains a third, separate container for its own output — the two features never share a container.
- **The transcript carries an identity, never a credential.** `TranscriptMessageDocument` gains an `attachments[]` array of `{ id, name, extension, mimeType, size, type }` — no URL, no SAS token, no storage path — for the same reason `image-tool` §9 records: a signed link is a bearer credential that cannot be withdrawn (`docs/documents/download-workflow.md` §5.4) and would be both a persisted secret and expired by the time anyone re-read the conversation. `TranscriptHeaderDocument.CurrentSchemaVersion` bumps from `1` to `2`; a transcript persisted before this change deserializes the missing array as empty, so no transcript migration is required.
- **Neither new type has chunks, and this is the invariant that must never regress.** `DocumentRetrievalSql` builds its candidate set by `UNION ALL`-ing the two chunk tables; a document with zero chunk rows contributes nothing to either side and cannot be retrieved, ranked, or cited — no SQL change is required for the exclusion to hold, which is exactly why it has to be tested rather than assumed. `DocumentRetrievalService.GetScopeAsync` is the second surface: an `Attachment` document must not contribute to `HasDocuments` or to the document-name list injected into the retrieval prompt, or the assistant will be told a file exists that `document_search` cannot read.
- **Prompt content never rides a progress event.** The extracted description and OCR text are model output persisted to SQL and injected directly into the assembled prompt; they are never written to a `ChatProgress` line or an activity frame, matching the tracking middleware's standing privacy posture that progress events carry no prompt content.

**Security.**

- **`Content-Disposition` stays `attachment`, with a content type derived from the extension, never from `MimeType`** — the same posture the existing download path already takes (`docs/documents/download-workflow.md` §4.3), for the same reason: an `inline` disposition would let a maliciously renamed file execute on the storage origin. `ResolveContentType` gains `.png`, `.jpg`, `.jpeg` alongside its existing six.
- **An inline thumbnail must not put a SAS in the DOM.** The standing posture is that a signed link is never prefetched, persisted, or written into a URL (`docs/documents/download-workflow.md` §5.4), and `core/documents/document-download-store.ts` already issues no request until a chip is clicked. Rendering a thumbnail on every chip render would mint a credential per image whether or not the user ever looks at it, breaking that rule outright. The proposed resolution — carried as open question 5 (§9), inherited verbatim from `image-tool`'s own open question 1 — is a new authenticated byte-streaming route that serves a thumbnail without ever minting a SAS, leaving the existing SAS route for explicit downloads only.
- **An uploaded image is untrusted bytes.** It is validated by its leading magic bytes as well as its extension, capped by a size limit EP-0 confirms against the target vision deployment, and never decoded, re-encoded, or thumbnailed by the API itself — no image-processing library is introduced, because an image decoder is an attack surface this feature does not need.
- **Ownership for the extraction pipeline is read off the parent conversation**, the same rule the download route already enforces — a background job resolving which image to describe never crosses into another user's conversation.
- **SAS signing still requires a shared-key connection string** (`docs/documents/download-workflow.md` §8.1). Adding a second container changes nothing about that constraint.
- **The permission gate is the last gate.** Until EP-6's permission story lands, the capability is reachable by anyone in an environment where the flag is on — which is why the flag defaults off everywhere, including development.

**Scalability & performance.**

- **The ingest-time vision call adds one more model call per image, off the request path.** It runs inside the existing background job infrastructure, under the same `SemaphoreSlim` concurrency gate every ingestion job already shares, so it competes for the same slots as document extraction rather than needing a second capacity plan.
- **Re-attachment cost grows with conversation length unless bounded — which is the entire reason it is bounded.** Without the N-image ceiling and the per-turn image-token budget, a long conversation holding many images would re-bill every one of them on every turn; §1's argument and FR-22 exist because of this specific growth curve.
- **`JobStatus`'s append-only rule and its `_ => "Processing"` coarse-state default are what let this ship with no client-side polling change at all.** A new `Describing` value is appended (proposed `9`, after `Persisting = 8`), and every existing client — including one written before this feature existed — still sees a sensible `Enqueued → Processing → Succeeded/Failed` progression.
- **Client bundle**: production warns above 675 kB initial raw and fails above 720 kB; baseline is 671.85 kB raw — about 3.15 kB of headroom. The thumbnail treatment reuses the existing `AttachmentChip` component and glyph table rather than adding a new one, so its marginal cost is a handful of icon entries and a template branch, not a new component tree; it must still ride the lazy `chat` chunk like everything else the composer and transcript already load lazily.

**AI system requirements.**

- **The catalog vision flag and the ingest-time vision deployment are two different things, and conflating them is the most likely design mistake.** `Model.IsVisionEnabled` marks a **user-selectable, catalog-listed chat model** as safe to send re-attached image bytes to during a live turn. The **ingest-time extraction deployment** is an operator-configured, non-catalog endpoint — analogous to `DocumentIntelligence:Endpoint` — used only by the background job, never selected by a user, and not required to be the same deployment (or even the same provider) as any catalog row.
- **The primary evaluation is deterministic and cheap**: a fixed benchmark of images spanning photographs, screenshots, scanned documents, and diagrams, each with a known expected description and any expected OCR text, run through the ingest pipeline. Pass threshold before EP-3 is accepted: **100%** of the benchmark's images either persist a non-null `Description` or surface a failed job with a sanitized reason — never a silent empty result mistaken for success.
- **`ApplyContextBudget`'s image term must be measured against real provider-reported usage, not assumed.** Vision APIs price image tokens by resolution and tiling in ways that differ by provider; a flat per-image estimate (mirroring `PerToolSchemaTokens`'s own flat-cost precedent) is proposed for v1, corrected later by the same `ProviderCalibration` mechanism `TokenEstimationOptions` already uses for text.
- **Tool names are irrelevant here.** This feature adds no new `AIFunction`; it widens message content and the assembled prompt. `UsageReportTranslator.ResolveMcpServerId`'s longest-`{server}_`-prefix rule has nothing to collide with.

## 7. Epics & user stories

| ID | Epic | Goal | Priority | Estimate | Depends on |
| --- | --- | --- | --- | --- | --- |
| EP-0 | Vision gate | Prove multimodal `ChatMessage` content is callable from .NET, for real, before anything downstream is sized against it | P0 | M | — |
| EP-1 | Image storage foundation | An uploaded image has an identity, an owner, a container, and is structurally unreachable from retrieval — the foundation `image-tool` later consumes unchanged | P0 | L | — |
| EP-2 | Model catalog vision capability | The catalog can say which deployments are safe to send a picture to | P0 | M | — |
| EP-3 | Image understanding pipeline | An uploaded image gets a permanent, cheap, textual presence before anyone asks about it twice | P0 | L | EP-0, EP-1 |
| EP-4 | Image in the turn | The assembled prompt actually carries what EP-3 extracted, on every turn, plus bounded real pixels on some of them | P0 | L | EP-0, EP-1, EP-2, EP-3 |
| EP-5 | Frontend surface | The user sees an image attach, process, and stay present | P0 | M | EP-1, EP-3 |
| EP-6 | Cost, governance and rollout | Every image is permissioned, bounded, measured, and switchable off | P0 | M | EP-3, EP-4 |

EP-1 depends on no Azure capability at all and can start during the EP-0 gate, mirroring `image-tool`'s own wave structure; anyone idle at the gate has a full epic's worth of schema, container and validator work waiting. EP-3 carries an unusually high proportion of `[enabler]` stories, because its whole first half — registering a vision deployment nobody has called yet, adding a job stage nothing has emitted before, adding columns nothing has populated before — has no user-visible shape until EP-4 reads what it wrote; each enabler names the story it unblocks, and none exceeds L.

### EP-0: Vision gate

#### US-001: `[enabler]` Confirm multimodal chat completions from .NET

- **Story**: `[enabler]` Call a real chat deployment from a throwaway .NET harness carrying a `ChatMessage` built from `IList<AIContent>` — a `TextContent` and a `DataContent` together — for the primary target provider, and pin the exact package versions, the accepted image formats, and the per-image size ceiling actually enforced. Unblocks US-002, US-101, US-201, and everything in EP-3 and EP-4 that assumes multimodal content works at all.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Status**: Not started
- **Acceptance criteria**:
  - Given the target provider, when a `ChatMessage` carrying a `TextContent` and a `DataContent` over a `.png` is sent, then the response is answered correctly and the outcome records the exact package ids and versions used, so `US-201`'s registration is written once rather than by trial and error.
  - Given Microsoft's own conflicting documentation on accepted formats, when the target deployment is tested directly with `.png`, `.jpg`/`.jpeg`, and one excluded format, then the outcome records which formats **that deployment** actually accepts, settling the format list this PRD proposes for veto rather than leaving it a guess.
  - Given the per-image size limits Microsoft's how-to and reference pages disagree on, when a large image is sent, then the outcome records the real ceiling the deployment enforces, which `US-103` validates against rather than picking the friendlier documented number.
  - Given a deployment configured with `Model.IsToolEnabled = false` or no vision support at all, when the same call is attempted, then the outcome confirms the request is **rejected outright** rather than silently answered as if the image were absent — the same failure mode `IsReasoningEnabled`'s doc comment already records for an unsupported option, now confirmed for images specifically.
  - Given the harness completes, when it is reviewed, then it is deleted or left under `tests/` as a skipped integration test; no spike code is merged into `Enterprise.Gpt.Api` or `Enterprise.Gpt.Service`.

#### US-002: `[enabler]` Confirm the ingest-time vision call

- **Story**: `[enabler]` Prove that a single chat completion, given one image and a fixed instruction, reliably returns a natural-language description and any legible text it contains, and measure its latency against a realistic image size. Unblocks US-301 and US-302.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-001
- **Status**: Not started
- **Acceptance criteria**:
  - Given a fixed prompt asking for a description and any legible text, when it is sent against ten varied sample images (photographs, screenshots, a scanned form, a diagram), then the outcome records a response shape reliable enough to parse into `Description` and `OcrText` without a fragile ad hoc heuristic.
  - Given the same ten calls, when their latency is measured, then the observed p50 and p95 are recorded, so `US-305`'s wall-clock bound is set from real numbers rather than guessed.
  - Given an image with no legible text at all, when it is described, then the outcome confirms the response can state that plainly, so `OcrText` can be persisted as `null` rather than as an invented empty string doing the same job less clearly.
  - Given the harness completes, when it is reviewed, then it is deleted or left under `tests/` as a skipped integration test.

### EP-1: Image storage foundation

#### US-101: `[enabler]` Add the document-type discriminator and migrate existing rows

- **Story**: `[enabler]` Add a `ConversationDocumentTypes` enum (`Uploaded = 1`, `Attachment = 2`, reserving `3`) to `Enterprise.Gpt.Dto/Enums/`, put the column on `ConversationDocument`, and ship one migration that creates it and backfills every existing row to `Uploaded`. Unblocks US-103, US-106, US-201 (EP-2 needs no new schema, but the retrieval-exclusion test in US-106 depends on this column existing), and the whole of EP-3 and EP-4.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Status**: Not started
- **Acceptance criteria**:
  - Given the new enum, when it is placed, then it sits in `Enterprise.Gpt.Dto/Enums/` beside `JobStatus` and `FileExtensions`, is numbered from 1, and carries the same append-only rule in its XML docs that `JobStatus` already states.
  - Given the value `3`, when it is documented, then the doc comment states plainly that it is **reserved for `docs/prd/image-tool/image-tool.md`'s `Generated` type** and must not be assigned to anything this PRD adds — the whole point of the reservation is that the two PRDs never have to renumber around each other.
  - Given the entity, when the column is added, then it is on `ConversationDocument` and **not** on `BaseDocument`, so `ProjectDocument` is unchanged.
  - Given `ConversationDocumentConfiguration`, when the model is built, then the property is configured there with `HasConversion<int>()` and the column name equals the property name — no `HasColumnName` is introduced, matching the invariant `Tool/DocumentRetrievalSql.cs` already depends on.
  - Given the migration, when it is generated, then it follows `20260821224810_AddConversationUsageReportingIndex` in schema `"Core"`, is applied by the existing startup `Database.Migrate()`, and every pre-existing row reads back as `Uploaded`.

#### US-102: `[enabler]` Provision the `images` container and its configuration

- **Story**: `[enabler]` Add `AzureStorage:ImagesContainer` and the write path into it, keeping `IBlobStorageService` and its test double in step. Unblocks US-103 and US-107.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Status**: Not started
- **Acceptance criteria**:
  - Given `AzureStorage:ImagesContainer`, when it is read, then it is read on each use rather than cached at construction, matching how `DocumentService.DocumentsContainer` consumes `AzureStorage:DocumentsContainer` today.
  - Given the setting is unset, when an image would be written, then `StorageNotConfiguredException` is raised and maps to the existing 503 `/problems/storage-not-configured` — no new problem type is introduced.
  - Given the blob key, when one is written, then it follows the existing convention shape (`{userId}/{conversationId}/{documentId}{ext}`), so a reader of one container needs no second rule to read the other.
  - Given any change to `IBlobStorageService`, when the integration suite runs, then `FakeBlobStorageService` implements the same surface and the suite compiles.

#### US-103: `[enabler]` Accept an image upload without ingesting it

- **Story**: `[enabler]` Extend `UploadedFileValidator` with PNG (`89 50 4E 47 0D 0A 1A 0A`) and JPEG (`FF D8 FF`) magic-byte checks and an acceptance rule that admits `.png`/`.jpg`/`.jpeg` **without** consulting `IDocumentTextExtractorFactory.IsSupported`, and add the write path that stores the bytes in the `images` container and creates a `ConversationDocument` row with `Type = Attachment` — with **no** extraction, chunking, embedding, or the document pipeline's usual job stages. Unblocks US-104, US-106, US-201 (EP-3), and US-401 (EP-4).
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-101, US-102, US-001
- **Status**: Not started
- **Acceptance criteria**:
  - Given `FileExtensions`, when `.Png`, `.Jpg` and `.Jpeg` are added, then they are appended to the enum, following the precedent that several existing values (`.Cs`, `.Py`, `.Xlsm`) already have no registered extractor — the enum has never been extractor-only in practice, only in the validator's rule.
  - Given an uploaded `.png`, when `HasContentMatchingExtension` runs, then it checks the leading bytes against the PNG signature exactly as it already does for `%PDF`, `PK`, and the OLE2 signature.
  - Given the same file, when the acceptance rule at `UploadedFileValidator.cs:72` runs, then it accepts the file when `extractorFactory.IsSupported(extension)` **or** the extension is one of the three image formats — the first genuine widening of "accepted" beyond "extractable" in this validator.
  - Given an accepted image, when it is persisted, then a blob is written to the `images` container and a `ConversationDocument` row is created with `Type = Attachment`, in one operation whose failure leaves neither a row without a blob nor a blob without a row.
  - Given the same write, when the pipeline is inspected, then **no** `IDocumentTextExtractor` is resolved, no chunker runs, and no embedding is generated — an attachment is not a document with a different label.
  - Given the row after persistence, when the chunk table is queried for it, then it returns **zero** rows.
  - Given an image larger than the ceiling US-001 records, when it is uploaded, then it is refused with a reason naming the limit, before any bytes reach the `images` container.

#### US-104: `[enabler]` Widen the advertised upload surface

- **Story**: `[enabler]` Extend `GET api/documents/file-extensions` to advertise `.png`/`.jpg`/`.jpeg` alongside the extractor-derived list, and update the route's own doc comment — which today asserts advertised and extractable formats must never drift apart — to state explicitly that an image is the one deliberate exception. Unblocks US-501.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-103
- **Status**: Not started
- **Acceptance criteria**:
  - Given the route, when it responds, then the returned array includes `.png`, `.jpg` and `.jpeg` in addition to the six extractor-derived extensions.
  - Given the doc comment on `GetFileExtensions`, when it is read, then it states the image exception explicitly, so a future reader does not "fix" the drift by removing the images or by wiring a phantom extractor for them.
  - Given `core/documents/supported-extensions-store.ts`, when it reads the route, then it requires no code change — the store already renders whatever the route returns.

#### US-105: Download a stored image in its own format

- **Story**: As a chat user, I want an image I attached to download with the right name and the right type, so that it opens in an image viewer rather than arriving as an anonymous byte stream.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-103
- **Status**: Not started
- **Acceptance criteria**:
  - Given an `Attachment` document, when `GET /api/documents/conversations/{conversationId}/{documentId}` is called by the conversation's owner, then a `DocumentDownloadDto` is returned unchanged in shape, with `Cache-Control: no-store`.
  - Given `DocumentService.ResolveContentType`, when the map is read, then it also covers `.png`, `.jpg` and `.jpeg` in addition to the six extensions it covers today.
  - Given the signed link, when it is built, then it carries `Content-Disposition: attachment` and the extension-derived content type, exactly as an uploaded document's link already does.
  - Given the caller does not own the parent conversation, when they request the link, then the response is 404 `/problems/resource-not-found` and nothing is signed.

#### US-106: `[enabler]` Serve an inline preview without a SAS in the DOM

- **Story**: `[enabler]` Add an authenticated byte-streaming route that serves an image's bytes directly through the API for inline thumbnail rendering, leaving the existing SAS-issuing download route for explicit downloads only. Unblocks US-503.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-103
- **Status**: Not started
- **Acceptance criteria**:
  - Given the same ownership rule the download route enforces, when a caller who owns the parent conversation requests the preview route for an `Attachment` document, then the response streams the image bytes with the correct `Content-Type` and `Cache-Control: private, max-age=…` — short enough that a stale thumbnail is not served long after a deactivation, and never `no-store`, since this route is meant to be cheaply re-renderable rather than a one-shot credential.
  - Given a caller who does not own the parent conversation, when they request the same route, then the response is 404 `/problems/resource-not-found`, matching the download route's own posture.
  - Given the client, when a thumbnail is rendered, then it requests this route directly as an `<img src>` and **never** requests or holds a SAS for it — the standing rule that a signed link is never prefetched, persisted, or written into a URL (`docs/documents/download-workflow.md` §5.4) is unaffected by this feature, because this route issues no SAS at all.
  - Given repeated renders of the same thumbnail across a session, when the route is called, then each call is a plain authenticated `GET` with no credential minted per call, unlike the SAS route it deliberately does not reuse.

#### US-107: An image is never retrieved or cited

- **Story**: As a chat user, I want an image kept out of the assistant's retrieval corpus, so that a picture I attached — which the assistant reads by description, not by search — is never quoted back to me as if it were a source document.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-103
- **Status**: Not started
- **Acceptance criteria**:
  - Given a conversation holding one uploaded document and one attached image, when `document_search` runs, then only the document's chunks appear in the candidate set and only it can be cited.
  - Given `DocumentRetrievalSql`, when it is compared before and after this feature, then it is **unchanged** — the exclusion holds because the image has no chunk rows, not because a type predicate was added.
  - Given an `Attachment` document, when `DocumentRetrievalService.GetScopeAsync` builds the scope, then it does not contribute to `HasDocuments` nor to the document-name list injected into the retrieval prompt.
  - Given an integration test that inserts one row of each type and asserts **zero** chunk rows exist for the `Attachment` one, when the suite runs, then it passes — this is the regression guard for the whole invariant.

#### US-108: Reclaim images under a retention policy

- **Story**: As an operator, I want stored images to stop accumulating forever, so that a container nothing ever deletes does not become an unbounded cost line.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-102
- **Status**: Not started
- **Acceptance criteria**:
  - Given the retention window the product owner sets (open question 4, §9), when an image blob passes it, then it is removed and its `ConversationDocument` row is marked with `DateDeactivated` in the same pass, so a reachable row never points at a missing blob.
  - Given a soft-deleted image, when the download or preview route is called for it, then it already answers 404 — this story changes nothing about that behaviour, only about the bytes.
  - Given no policy is yet configured, when the feature ships, then images simply accumulate and the growth rate is visible in the counters US-604 adds.

### EP-2: Model catalog vision capability

#### US-201: `[enabler]` Add `Model.IsVisionEnabled`

- **Story**: `[enabler]` Add a third capability flag, `IsVisionEnabled`, to `Model.cs`, off by default, with a migration backfilling every existing row to `false`, and a doc comment mirroring `IsReasoningEnabled`'s own rationale for why an unsupported option is not something a deployment can be asked to just ignore. Unblocks US-202, US-406, US-407.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Status**: Not started
- **Acceptance criteria**:
  - Given `Model.cs`, when the property is added, then it sits beside `IsToolEnabled` and `IsReasoningEnabled`, defaults to `false`, and its doc comment states the same failure mode `IsReasoningEnabled`'s already does — a deployment that does not support image content rejects the whole request rather than ignoring the flag.
  - Given the migration, when it runs, then every existing `Model` row reads back `IsVisionEnabled = false`, so no deployment is silently assumed vision-capable.
  - Given the flag, when a model's row is edited through the admin catalog after this ships, then setting it true is a deliberate, auditable act — matching how `IsReasoningEnabled` is set today.

#### US-202: `[enabler]` Surface `IsVisionEnabled` on the DTO and mapper

- **Story**: `[enabler]` Add `IsVisionEnabled` to `ModelDto` and wire both directions of `ModelMapper`, following the exact shape `IsToolEnabled`/`IsReasoningEnabled` already use. Unblocks US-203, US-406.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-201
- **Status**: Not started
- **Acceptance criteria**:
  - Given `ModelDto`, when the property is added, then it sits beside `IsToolEnabled` and `IsReasoningEnabled`.
  - Given `ModelMapper`, when entity-to-DTO, DTO-to-entity, and update mappings are inspected, then all three carry `IsVisionEnabled` exactly as they already carry the other two flags, with no path silently dropping it.

#### US-203: Mark a model as vision-capable

- **Story**: As an administrator, I want to mark a catalog model as vision-capable, so that the platform knows which deployments a picture can safely be sent to.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-202
- **Status**: Not started
- **Acceptance criteria**:
  - Given `features/admin/models/model-form-dialog.ts`, when the form is opened for an existing or new model, then a "Vision enabled" control renders beside the existing tool and reasoning toggles.
  - Given the toggle is set and saved, when `admin-models-store.ts` submits the change, then the persisted `Model.IsVisionEnabled` reflects it, and `features/admin/models/admin-models.ts`'s list reflects the new value without a page reload.
  - Given a model with `IsToolEnabled = false`, when vision is toggled on anyway, then the save is allowed — the two flags are independent, since a future provider could in principle accept image content without accepting function calls.

### EP-3: Image understanding pipeline

#### US-301: `[enabler]` Configure the ingest-time vision deployment

- **Story**: `[enabler]` Add configuration for a separately configured vision deployment — provider id and deployment name, independent of the model catalog and of any conversation's selection — resolved through the same keyed `IChatClient` resolution the four chat providers already use, following the `DocumentIntelligence:Endpoint` precedent of a directly-read configuration section rather than a new options class. Unblocks US-302.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-002, US-201
- **Status**: Not started
- **Acceptance criteria**:
  - Given the configuration, when it is read, then it names a provider id already registered in `Program.cs` (so `_chatClientResolver.Resolve` can find it) and a deployment name distinct from any catalog `Model` row — the ingest deployment is never required to be user-selectable.
  - Given the flag governing this feature is off, when the app starts, then no vision call is attempted and nothing fails for its absence — an unconfigured optional capability is not a broken deployment, matching the four chat providers' own `Enabled`-gated registration shape.
  - Given the configuration is present but names a provider id `Program.cs` has not registered, when the app starts, then it fails fast with a message naming the unknown id, rather than failing on the first upload.

#### US-302: `[enabler]` Add the `Describing` job stage and call the vision deployment

- **Story**: `[enabler]` Append `Describing = 9` to `JobStatus`, and extend the image upload's background job so that after the blob write, it calls the configured vision deployment with the stored bytes and a fixed instruction asking for a description and any legible text. Unblocks US-303, US-501, US-502.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-103, US-301
- **Status**: Not started
- **Acceptance criteria**:
  - Given `JobStatus`, when `Describing` is added, then it is `= 9`, appended after `Persisting = 8` rather than inserted in pipeline order, following the same precedent `Chunking`/`Persisting` already set.
  - Given the coarse `state` projection (`DocumentEndpoints.MapState`), when a job reports `Describing`, then it projects to `Processing` through the existing `_ => "Processing"` default — **no code change to the projection is required**, and this criterion exists to assert that fact rather than assume it.
  - Given an image job, when it runs, then its stages are `Queued` → `Uploading` → `Describing` → `Processed`, skipping `Extracting`, `Chunking` and `Embedding` entirely — an image never enters the document extraction pipeline.
  - Given the vision call, when it is made, then it resolves the keyed chat client for the configured provider (`US-301`) and sends the stored image bytes as `DataContent`, never re-reading the file from the caller's original upload buffer.
  - Given `domain/documents/upload-schedule.ts`'s existing polling schedule, when an image job runs through `Describing`, then the client requires no code change to keep polling correctly, because the coarse state and the message field both already exist.

#### US-303: `[enabler]` Persist the extracted description and OCR text

- **Story**: `[enabler]` Parse the vision call's response into a description and any OCR'd text, and persist both on the `ConversationDocument` row's new `Description` and `OcrText` columns. Unblocks US-401, US-404.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-302
- **Status**: Not started
- **Acceptance criteria**:
  - Given a successful vision call, when the job completes, then `Description` is populated with the model's account of the image and `OcrText` is populated when the image contains legible text, or left `null` when it does not.
  - Given the same write, when the chunk table is queried for the document, then it still returns **zero** rows — persisting a description is not persisting a chunk.
  - Given the job status, when it reaches `Processed`, then `JobStatusDto.documentId` is populated exactly as a document's own completion already populates it, so the client can resolve the finished attachment with no extra lookup.

#### US-304: Image understanding fails without losing the attachment

- **Story**: As a chat user, I want to know when an image couldn't be understood, so that I'm not left wondering why the assistant can't discuss it.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-302
- **Status**: Not started
- **Acceptance criteria**:
  - Given the vision call fails (content filter, transient fault, malformed response), when the job fails, then it reaches `Failed` with a sanitized `errorMessage` — "The image could not be understood. Try a clearer photo or a different file." — following `BackgroundJobProcessor.DescribeFailure`'s existing rule that raw exception text never reaches the client.
  - Given the same failure, when the `ConversationDocument` row is inspected, then it **still exists** with its blob intact — the attachment is not rolled back because its description failed; only `Description` and `OcrText` stay `null`.
  - Given a conversation holding an image whose understanding failed, when a later turn assembles its prompt, then the missing description is simply absent rather than represented as an empty or fabricated string.

#### US-305: `[enabler]` Bound the vision call's wall-clock time

- **Story**: `[enabler]` Cap the ingest-time vision call at a configurable timeout, following the `DocumentIntelligence:AnalysisTimeout` precedent, so a stalled call cannot hold a processing slot indefinitely. Unblocks nothing further but guards US-302 in production.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-302
- **Status**: Not started
- **Acceptance criteria**:
  - Given the configured timeout, when a vision call exceeds it, then the job fails with a timeout-specific sanitized message rather than holding the semaphore slot until the host restarts.
  - Given the default value, when it is set, then it is derived from `US-002`'s measured p95 latency with headroom, not an arbitrary round number.

### EP-4: Image in the turn

#### US-401: `[enabler]` Widen the stream request to carry attachment ids

- **Story**: `[enabler]` Add an `attachmentIds` field to `CreateConversationStreamActionDto` and its validator, and thread it through `conversation-stream-client.ts`'s request body. Unblocks US-403, US-406.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-103
- **Status**: Not started
- **Acceptance criteria**:
  - Given `CreateConversationStreamActionDto`, when the field is added, then it is `List<Guid>`, defaulting to empty, alongside `Prompt`, `ModelId` and `McpServers`.
  - Given the validator, when an id in the list does not resolve to an `Attachment` document owned by the caller's conversation, then the turn fails with a 400 naming the offending id, rather than silently ignoring it.
  - Given `conversation-stream-client.ts`, when the request body is built, then it includes `attachmentIds` beside `prompt`, `modelId` and `mcpServers`, mapped from whichever attachments are `ready` in the composer at send time.

#### US-402: `[enabler]` Carry the attachment reference on the transcript message

- **Story**: `[enabler]` Add an `attachments[]` array to `TranscriptMessageDocument` carrying id, name, extension, MIME type, size and type — no URL — and bump `TranscriptHeaderDocument.CurrentSchemaVersion` from `1` to `2`. Unblocks US-403, US-503.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-103, US-401
- **Status**: Not started
- **Acceptance criteria**:
  - Given `TranscriptMessageDocument`, when the array is added, then it joins `/content/*` and `/htmlContent/*` on the Cosmos indexing-policy exclusion list, since it carries no field anything needs to query on.
  - Given the array, when an entry is written, then it contains no `downloadUrl`, no SAS token and no storage path.
  - Given a transcript persisted before this story, when it is read, then the missing array deserializes as empty and the message renders exactly as it does today.
  - Given a user message that attached two images, when it is persisted, then both appear in the order the request named them.

#### US-403: `[enabler]` Widen `ChatMessage` assembly to multimodal content

- **Story**: `[enabler]` Widen `ConversationService.cs:855-858`'s message assembly from the `(ChatRole, string)` overload to `(ChatRole, IList<AIContent>)`, so a message can carry a `TextContent` alongside zero or more image parts. Unblocks US-404, US-406, US-407.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-001, US-402
- **Status**: Not started
- **Acceptance criteria**:
  - Given a replayed message with no attachments, when it is assembled, then its `ChatMessage` carries a single `TextContent` equal to what the `(ChatRole, string)` overload already produced — a turn with no images is byte-for-byte unaffected.
  - Given a message whose `attachments[]` names one or more images, when it is assembled, then its `ChatMessage`'s content list carries the message's own text as a `TextContent`, plus each attached image's extracted description as further text content — the always-present half of the hybrid, built before any re-attachment decision runs.
  - Given every non-test project in the solution, when searched for `DataContent` and `UriContent` after this story, then this is the **first** production call site for either — the assembly seam genuinely widens rather than growing a second path beside the old one.

#### US-404: Every attached image's description is present on every future turn

- **Story**: As a chat user, I want the assistant to still know what an image showed however long ago I attached it, so that I never have to re-explain a picture I already shared.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-403, US-303
- **Status**: Not started
- **Acceptance criteria**:
  - Given a conversation whose first turn attached an image with a persisted `Description`, when its tenth turn is assembled, then that description is present in the assembled `ChatMessage` list, whether or not the image's raw bytes are re-attached that turn.
  - Given an image whose description is `null` because understanding failed (`US-304`), when a later turn is assembled, then nothing fabricated stands in for it — the message's own text is unaffected and no placeholder text is invented.
  - Given `OcrText` is non-null for an image, when its description is assembled into the prompt, then the OCR'd text rides alongside the description, not in place of it.

#### US-405: `[enabler]` Add the image-token term to the context budget

- **Story**: `[enabler]` Add a per-image token estimate to `TokenEstimationOptions` (beside `PerMessageFramingTokens` and `PerToolSchemaTokens`) and fold it into `ApplyContextBudget` so a turn carrying image content is never authorised against a size the assembled prompt does not actually fit. Unblocks US-406.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-403
- **Status**: Not started
- **Acceptance criteria**:
  - Given a turn whose assembled content includes N re-attached images, when `ApplyContextBudget` runs, then its token estimate for that turn includes N times the configured per-image estimate, in addition to every term it already counts.
  - Given a turn whose image-inclusive estimate exceeds the model's context window on its own (the system message and current prompt alone, images included, do not fit), when the budget is applied, then the turn fails with the same `ValidationException` shape `ApplyContextBudget` already throws for an oversized text-only prompt — never a silent send that the provider then rejects.
  - Given `ChatMetrics`, when a turn including images completes, then the estimator's relative error against the provider's reported input tokens is recorded exactly as it already is for text-only turns, so the flat per-image estimate can be corrected by `ProviderCalibration` once real data exists.

#### US-406: Re-attach the most recent images, within a bound

- **Story**: As a chat user, I want the assistant to be able to see the actual picture for my most recent images, so that a question needing real pixel detail can still be answered.
- **Priority**: P1 · **Estimate**: L · **Depends on**: US-403, US-405, US-201
- **Status**: Not started
- **Acceptance criteria**:
  - Given a conversation holding more images than the configured re-attachment ceiling N (default 2, open question 2, §9), when a turn is assembled, then only the N most recently attached images have their raw bytes included as `DataContent`; every other image contributes only its extracted description (`US-404`).
  - Given the selected model's `IsVisionEnabled` is `false`, when a turn is assembled, then **no** image bytes are re-attached regardless of recency — every image still contributes its description.
  - Given the per-turn image-token budget (`US-405`), when the N most recent images alone would exceed it, then bytes are dropped from the **oldest** of that recent set first until the turn fits, and the dropped images still contribute their descriptions.
  - Given a turn that re-attaches at least one image, when `ChatMetrics` records it, then the `image_input.reattached_tokens` histogram (`US-604`) captures the estimated cost, so the re-attachment-boundedness success criterion (§1) is measurable in production.

#### US-407: A non-vision model still knows what an image showed

- **Story**: As a chat user, I want a model that cannot see pictures to still answer from what an attached image says, so that switching models mid-conversation does not make the assistant forget an image entirely.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-403
- **Status**: Not started
- **Acceptance criteria**:
  - Given a model whose `IsVisionEnabled` is `false` and a conversation holding an attached image, when a turn runs, then the assembled prompt still carries the image's extracted description as text, and the turn completes normally — no 400, no stand-down warning visible to the user, matching the stand-down precedent at `ConversationService.cs:2173-2183` in spirit (log, continue) rather than the throw precedent at `:2186-2193` (which is for an explicit, named mismatch, not an implicit degradation).
  - Given the same turn, when `IsToolEnabled` is also `false` on that model, then nothing about image handling additionally fails — the two capability flags are independent, and neither turning off the other changes whether a description rides the prompt.
  - Given a model switch mid-conversation from a vision-enabled model to a non-vision one, when the next turn runs, then the transition is silent to the assembled content's correctness — the user is told through the composer (`US-505`), not through a failed turn.

#### US-408: Export renders an image attachment sensibly

- **Story**: As a chat user, I want an exported conversation to say something reasonable about an image I attached, so that an exported transcript is not silently missing a message's content.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-402, US-303
- **Status**: Not started
- **Acceptance criteria**:
  - Given a message carrying an image attachment, when the conversation is exported to `html` or `pdf`, then the export includes the image's file name and its persisted description as visible text — not the raw bytes, and not a blank gap where the attachment was.
  - Given the same message, when exported to `md`, `txt` or `json`, then the attachment's name, type and description are represented in whatever shape that format already uses for structured metadata.
  - Given a message whose image has no persisted description (`US-304`), when exported in any format, then the export states the file name only, without fabricating a description.

### EP-5: Frontend surface

#### US-501: Attach an image in the composer

- **Story**: As a chat user, I want to attach an image the same way I attach a document, so that I don't have to learn a second upload flow.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-104
- **Status**: Not started
- **Acceptance criteria**:
  - Given the composer's existing `file-drop-target.ts` and file picker, when `.png`, `.jpg` or `.jpeg` is dropped or selected, then it uploads through the existing `DocumentUploadClient` and `upload-store.ts` path, subject to the same `MAX_CONCURRENT_UPLOADS = 3` limit as any document.
  - Given `SupportedExtensionsStore`, when the composer renders its accepted-formats hint, then it reflects the widened `file-extensions` response (`US-104`) with no client code change beyond what already reads that store.
  - Given a `.webp` file, when it is dropped, then it is refused with the `unsupported` chip state, the same treatment an unsupported document extension already receives.

#### US-502: See an image being processed

- **Story**: As a chat user, I want to see that an attached image is being understood, so that I know why I should wait before asking about it.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-501, US-302
- **Status**: Not started
- **Acceptance criteria**:
  - Given an uploading image, when its job reports `Uploading` then `Describing` then `Processed`, then the chip's sub-status line renders the server's own message at each stage, through the same `UploadStatusClient` polling path a document upload already uses.
  - Given the job reaches `Processed`, when the chip updates, then its state moves to `ready` and the thumbnail (`US-503`) replaces any interim placeholder.
  - Given the job reaches `Failed`, when the chip updates, then it moves to `failed` and shows the sanitized `errorMessage` (`US-304`), with the image still visible as a stored attachment rather than disappearing.

#### US-503: See a thumbnail, not a file glyph

- **Story**: As a chat user, I want an attached image to look like the picture it is, so that I can recognise which photo I attached without reading a file name.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-106, US-402
- **Status**: Not started
- **Acceptance criteria**:
  - Given `attachment.model.ts`'s `fileGlyph()` table, when `.png`, `.jpg` and `.jpeg` are added, then the chip renders an actual thumbnail sourced from `US-106`'s preview route instead of falling through to a generic file glyph.
  - Given `npm run lint`'s `check:icons` gate, when it runs, then it still passes — the thumbnail is an `<img>` bound to the preview route, not a new sprite icon, so no new manifest entry is required for the thumbnail itself.
  - Given a message that carries an image attachment, when the transcript renders it, then the same thumbnail treatment appears beside the message, sourced from the persisted `attachments[]` reference (`US-402`), and survives a reload.
  - Given the thumbnail's `<img src>`, when inspected, then it points at the authenticated preview route (`US-106`) and never at a SAS URL.

#### US-504: Loading and failure states are distinguishable

- **Story**: As a chat user, I want an image's uploading, processing, ready, failed and unsupported states to look different from each other, so that I am never left guessing whether something is still working.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-502
- **Status**: Not started
- **Acceptance criteria**:
  - Given `AttachmentState`'s existing `uploading | processing | ready | failed | unsupported | unknown` ladder, when an image chip renders each state, then each has a visually distinct treatment already defined for documents — no new state value is introduced, and none is skipped for images.
  - Given a `processing` image (the `Describing` stage), when it is compared to a `processing` document (the `Extracting`/`Chunking`/`Embedding` stages), then both render through the same visual treatment, since the coarse state — not the fine-grained stage — drives the chip's appearance.

#### US-505: The composer explains a non-vision model's limitation

- **Story**: As a chat user, I want to know when the selected model can't actually see my attached picture, so that I understand the assistant is answering from a description, not the image itself.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-407
- **Status**: Not started
- **Acceptance criteria**:
  - Given a conversation holding at least one image attachment and a selected model whose `IsVisionEnabled` is `false`, when the composer renders, then its existing `note()` ladder adds a rung distinct from the "can't read attachments" one, stating that the assistant will answer from the image's description rather than the picture itself.
  - Given the model is switched to a vision-enabled one, when the composer re-renders, then the note is removed without a page reload.
  - Given a conversation with no image attachments at all, when a non-vision model is selected, then this note does not render — it is conditioned on an actual attachment existing, not merely on the model's capability.

#### US-506: Use the image surface without a mouse

- **Story**: As a chat user relying on a keyboard or assistive technology, I want to attach, review and remove an image without a pointer, so that the feature is usable the same way every other attachment already is.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-503
- **Status**: Not started
- **Acceptance criteria**:
  - Given the attachment chip, when an image renders inside it, then the chip remains a single focusable element in tab order, exactly as a document chip already is — the thumbnail adds no new interactive surface.
  - Given a screen reader, when an image chip is focused, then its accessible name states the file name and its current state (uploading, processing, ready, failed), matching the pattern a document chip already announces.
  - Given `prefers-reduced-motion`, when a processing image's chip renders, then no new animation is introduced beyond what the existing chip states already use.

#### US-507: An unusable image is refused clearly

- **Story**: As a chat user, I want to be told exactly why an image was refused, so that I know whether to try a different file or a different format.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-103
- **Status**: Not started
- **Acceptance criteria**:
  - Given a file whose extension is not `.png`/`.jpg`/`.jpeg`, when it is refused, then the message names the accepted formats, sourced from the same `file-extensions` response every unsupported-document message already reads from.
  - Given a file whose content does not match its claimed extension, when it is refused, then the message states the mismatch plainly, matching the wording a renamed document already receives.
  - Given a file larger than the ceiling `US-001` records, when it is refused, then the message states the limit in human terms, matching the existing `upload-too-large` problem shape.

### EP-6: Cost, governance and rollout

#### US-601: Gate image understanding on a permission

- **Story**: As an administrator, I want to decide who can have the platform understand images, so that a capability that calls a vision deployment on every upload is granted deliberately.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-203
- **Status**: Not started
- **Acceptance criteria**:
  - Given the permission decision from open question 3 (§9), when it is implemented, then either the existing `Upload File` grant gates image uploads, or a new built-in permission is seeded in a migration, added to `PermissionIds` **and** to `PermissionIds.Names` — a new id absent from `Names` fails the endpoint filter's map-time validation at app start.
  - Given a user without the grant, when they open the composer, then image formats are not advertised by `GET api/documents/file-extensions` for that request, and nothing renders an unavailable state — the same shape as an upload affordance that is simply absent.
  - Given the grant is resolved, when it is read, then it comes from the singleton `IUserPermissionCache`, not a per-request database query, and it is invalidated per user by `PermissionService` when changed.
  - Given an administrator holding no image grant, when they attempt to attach an image, then they do not get the capability implicitly — administrators are deliberately not granted every permission.
  - Given a user whose grant is revoked mid-conversation, when their next upload attempt runs, then it is refused; an image already attached and processed before the revoke is unaffected.

#### US-602: Feature-flag image input with a documented rollback

- **Story**: As an operator, I want to switch image input off without a deployment, so that a misbehaving vision deployment or an unexpected cost is a configuration change rather than an incident.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-301, US-403
- **Status**: Not started
- **Acceptance criteria**:
  - Given the flag governing this feature, when it is turned off, then image formats are no longer advertised, no vision call is attempted for any new upload, and no image content is assembled into any turn's prompt — the whole capability disappears end to end.
  - Given the flag off, when a conversation that previously held an image runs a turn, then it behaves exactly as it would if the image had never been attached — a regression test asserts this.
  - Given a fresh environment with no explicit setting, when it starts, then the flag defaults to **off**, including in development.
  - Given the flag off, when an image already stored before the flag was flipped is inspected, then its blob and its `ConversationDocument` row are untouched — a rollback does not destroy files users already sent.
  - Given a flag change, when the app restarts, then no code change or redeploy is needed, and the rollback path is exercised at least once before production enablement.

#### US-603: Bound what one conversation and one user can upload

- **Story**: As an operator, I want a ceiling on how many images a conversation and a user can attach, so that one long conversation's re-attachment cost cannot become a surprise.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-406
- **Status**: Not started
- **Acceptance criteria**:
  - Given the ceilings the product owner sets, when they are configured, then an upload that would exceed a per-conversation image ceiling is refused with a reason naming the limit, and the assistant is not asked to re-attach more than the re-attachment ceiling (`US-406`) regardless of how many images a conversation holds.
  - Given a ceiling is unset, when the feature runs, then it behaves exactly as it does without this story — a ceiling is opt-in, not a surprise default.
  - Given a user at a per-user ceiling, when they attempt another upload, then the refusal is a plain explanation naming the limit, not a 403 or a failed turn.

#### US-604: Emit image spans and metrics

- **Story**: As an operator, I want image ingestion and re-attachment activity in the traces and metrics I already collect, so that a slow vision call or a runaway re-attachment cost is diagnosable without reading application logs.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-302, US-406
- **Status**: Not started
- **Acceptance criteria**:
  - Given a new set of instruments on `Enterprise.Gpt.Service/Observability/ChatMetrics.cs`'s existing `Meter`, when they are added, then `image_input.describe.duration` records the ingest-time vision call's elapsed time, `image_input.describe.count` increments per attempt tagged by outcome, and `image_input.reattached_tokens` histograms the estimated re-attachment cost per turn.
  - Given `TelemetryRegistration.AddEnterpriseTelemetry`, when it is inspected, then no new meter is registered — these instruments ride the existing `ChatTelemetrySourceName` meter, following the one-meter pattern already established.
  - Given any instrument, when its tags are inspected, then none carries prompt content, a file name that is user content, image bytes, or a signed URL.
  - Given no telemetry backend is configured, when the app runs locally, then the instruments still exist and can be asserted in tests.

#### US-605: `[enabler]` Attribute the ingest-time vision call's cost

- **Story**: `[enabler]` Record what the ingest-time vision call spent, even though it runs on a background job outside any turn's usage report, so its cost is not invisible simply because it has no natural `ConversationUsage` row to join. Unblocks nothing further; closes a gap `US-604`'s metrics alone do not.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-302
- **Status**: Not started
- **Acceptance criteria**:
  - Given a completed vision call, when its token usage is available from the provider's response, then it is recorded through `US-604`'s telemetry instruments, tagged by outcome, rather than silently discarded because no turn is in progress to attribute it to.
  - Given the codebase has no HTTP surface for usage today (`ConversationUsage`), when this story ships, then it adds none — cost visibility for the ingest call is telemetry-only for v1, matching this PRD's own non-goal in §2.

## 8. Milestones & rollout

**Phases**, derived from the epic dependency graph.

| Phase | Contents | Relative estimate |
| --- | --- | --- |
| **Wave 0 — the gate** | EP-0 in full (US-001, US-002). Nothing downstream is sized in detail until both close | ~0.5 week |
| **Wave 1 — storage and catalog, in parallel** | EP-1 in full (US-101 … US-108) and EP-2 in full (US-201 … US-203); neither depends on the other | ~2 weeks |
| **Wave 2 — understanding** | EP-3 in full (US-301 … US-305); needs EP-1's write path and EP-0's confirmed vision call | ~2 weeks |
| **Wave 3 — the turn** | EP-4 in full (US-401 … US-408); needs EP-2's flag, EP-3's persisted descriptions, and EP-1's transcript reference | ~2.5 weeks |
| **Wave 4 — frontend surface** | EP-5 in full (US-501 … US-507); starts as soon as EP-1 and EP-3's shapes are stable, does not wait for EP-4's re-attachment policy | ~1.5 weeks, overlapping wave 3 |
| **Wave 5 — hardening before switch-on** | EP-6 in full (US-601 … US-605) | ~1 week |

**Critical path.** `US-001 → US-002 → US-301 → US-302 → US-303 → US-404`, with `US-101 → US-103 → US-402 → US-403 → US-405 → US-406` joining ahead of it and `US-201 → US-202` running clear beside both. EP-3 is the longest stretch sitting on unproven ground — the ingest-time vision call — so schedule pressure belongs there; EP-1 and EP-2 carry no such risk and are where anyone idle during the gate should work.

**Risks & mitigations.**

| Risk | Mitigation |
| --- | --- |
| The ingest-time vision call's response shape is inconsistent across sample images, making description/OCR parsing brittle | `US-002` tests against ten varied images before any production code depends on the shape; `US-304` ensures a parsing failure is a failed job, never a corrupted description |
| A flat per-image token estimate is badly wrong for the actual provider | `US-405` reuses the exact `ProviderCalibration` mechanism already correcting text estimates, so a wrong flat number is a data problem, not a redesign |
| The re-attachment bound (N, per-turn budget) ships with a guessed default that is wrong for real usage | Both are explicit open questions (§9) with a proposed default; `US-604`'s telemetry makes the real distribution observable before the defaults are locked in |
| `image-tool` ships before this PRD's reservation of enum value `3` is respected | `US-101`'s doc comment states the reservation explicitly; a reviewer of `image-tool`'s eventual migration checks this PRD first |
| The context-budget image term (`US-405`) is skipped because it has no user-visible surface | Marked P0 in §4 and called out by name in §1's overview specifically because it is the kind of gap nothing renders a symptom for until a provider starts rejecting turns |

**Rollout & rollback.** One flag governs the whole feature end to end — attach affordance, ingest pipeline, and turn injection — defaulting **off** everywhere, including development. With it off, a conversation behaves exactly as it does today, and `US-602` asserts that with a regression test. Rollback is a configuration change with no redeploy. The image-input permission gate is the last gate before general availability: until `US-601` lands, the capability is reachable by anyone in an environment where the flag is on, which is why the flag defaults off independently of the permission.

## 9. Assumptions & open questions

**Assumptions.** Each is a guess a reviewer can veto.

- The document-type discriminator is numbered `Uploaded = 1`, `Attachment = 2`, with `3` **reserved** rather than assigned, for `image-tool`'s future `Generated` value. This is this PRD's own numbering decision, made because this PRD ships first; a reviewer who prefers a different reservation scheme should say so before `US-101` starts, since the value is frozen the moment it deploys.
- Uploaded images live in a new `AzureStorage:ImagesContainer`, distinct from both `documents` and `image-tool`'s future `generated` container. This **overrides** `image-tool` §9's assumption that uploaded images would live in `documents`.
- The extracted description and OCR text are persisted as two nullable columns directly on `ConversationDocument` (`Description`, `OcrText`), not in a child table, because an image has exactly one row of extracted content and never a chunk.
- The ingest-time vision deployment is configured independently of the model catalog, following the `DocumentIntelligence:Endpoint` precedent — a raw configuration section, not an `Options` class with `ValidateOnStart`, matching that precedent exactly rather than the four chat providers' pattern.
- The re-attachment policy re-attaches the **N most recent** images by attachment order, not by relevance to the current prompt. A relevance-ranked policy would need a retrieval-like mechanism over images, which is explicitly out of scope (§2) — recency is assumed to be a reasonable proxy for "still under discussion."
- A per-image token estimate is a flat number (mirroring `PerToolSchemaTokens`'s own flat-cost precedent for tool schemas), not a resolution-aware calculation, for v1. Vision APIs price image tokens by resolution and tiling, and getting this exactly right is deferred to real usage data via `ProviderCalibration`.
- Numeric targets not supplied in the request, proposed here for veto: the 15-second p95 processing-visibility target in §1, the default re-attachment ceiling `N = 2`, and the phase durations in §8, which assume no team size since none was specified.
- `image-tool`'s superseded stories are `US-101` (discriminator — this PRD ships it first), `US-105` (message-level attachment reference — this PRD's `US-402` ships the array `image-tool` would later add `Generated`-typed entries to), `US-106` (retrieval exclusion — this PRD's `US-107` ships the invariant and its test generically), `US-301` (accept an image upload without ingesting it — this PRD's `US-103` already builds general uploaded-image handling), and the composer-widening half of `US-404` (attaching an image in the composer — this PRD's `US-501` already ships it). `image-tool`'s `US-102` (its own `generated` container) and `US-104` (downloading a *generated* file) are **not** superseded — they remain necessary for its own output — though `US-104`'s specific requirement that the content-type map cover `.png`/`.jpg`/`.jpeg` is already satisfied by this PRD's `US-105` before `image-tool` ships.
- **Amending `image-tool.md` to formally record this reversal is out of scope for this PRD** and is carried here as a follow-up: whoever picks up `image-tool` next should update its §9 and its superseded story ids before estimating that PRD's build order, rather than discovering the drift mid-implementation.

**Open questions.**

- **Which deployment performs the ingest-time extraction?** The conversation's selected model is the wrong answer — ingestion runs on a background job with no user model selection in scope, and a user can change models mid-conversation after the image was already described. Proposed for veto: a separately configured vision deployment, exactly as `DocumentIntelligence:Endpoint` already is, resolved through the existing keyed `IChatClient` mechanism with a distinct deployment name rather than a new SDK client type — *product owner with the backend engineer, before `US-301` starts*.
- **The per-image and per-turn token budget for re-attachment, and the value of N.** Proposed defaults: `N = 2` most recent images, and a per-turn image-token ceiling derived from `Model.ContextWindowSize` (a fixed fraction, magnitude TBD). Both are vetoable and both gate `US-406` — *product owner, before `US-406` starts*.
- **Reuse the `Upload File` permission, or add a new `Understand Image` grant?** Inherited verbatim from `image-tool`'s own open question 2, which lands here because this PRD ships first. `US-601` gates on whichever the product owner picks and states both consequences — *product owner, before `US-601` starts*.
- **Retention for the `images` container.** No window is proposed here beyond "shorter than forever." `image-tool`'s own US-107 flags that blobs are never reclaimed on soft delete today, which applies identically to this container — *product owner with the operator, before `US-108` starts*.
- **How a transcript thumbnail gets its bytes.** Inherited verbatim from `image-tool`'s own open question 1: the download route mints a five-minute SAS, and the standing posture (`docs/documents/download-workflow.md` §5.4) is that a SAS is never prefetched, persisted, or written into a URL, so `<img src="{sas}">` on every render is forbidden. This PRD's `US-106` proposes and builds the authenticated byte-streaming route as the resolution, but the choice itself is a decision a reviewer should confirm before `US-106`'s route shape is finalised, since `image-tool` will inherit whichever answer lands here — *product owner, before `US-106` starts*.
- **Does an uploaded image appear in the conversation's document list and the cross-conversation documents library screen (`GetUserDocumentsAsync`)?** Inherited verbatim from `image-tool`'s own open question 5, unanswered there too. Today, with no filter added, an image **would** appear in that listing, mixed in with genuinely readable documents, because nothing filters `GetUserDocumentsAsync` by `Type`. If the answer is "hide it," `US-107`'s retrieval-scope work is joined by a query-side filter; if "show it," nothing further is needed — *product owner, before `US-107` starts*.
