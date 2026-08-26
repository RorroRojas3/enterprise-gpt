# PRD: Document Summarization

> **Revision note (2026-08-25).** This PRD previously specified summarization as a direct-request feature: HTTP routes, a background job, job-status polling, and a seven-story frontend epic on the attachment chip. That design is reversed. Summarization is now an `AIFunction` the model calls mid-turn, synchronously, off the conversation's own `IChatClient` — the same shape as `document_search`. There is no HTTP route, no background job, no frontend surface. Every section below reflects the new design; §9 records what changed and why.
>
> **A second, unusual note, now updated.** While this revision was first drafted, the codebase itself moved: EP-1 and EP-2 were already shipped, and an implementation of most of EP-3, EP-4, and the new tool-integration epic appeared mid-revision (`Enterprise.Gpt.Service/Summarization/DocumentSummaryService.cs`, `Enterprise.Gpt.Service/Tool/DocumentSummaryTool.cs`, `Enterprise.Gpt.Service/Caching/UserGrantReader.cs`, the two summary entities and their configurations, migration `20260825224934_AddDocumentSummaries`, and the `ConversationService.CreateChatOptionsAsync` wiring), with no tests and no review. **That work is now finished.** 41 new unit tests and 4 new integration tests were added; the full suite — 1,631 unit tests, 377 integration tests — passes with zero failures (verified: a targeted 280-test run covering the summarization, tool, permission-filter, and conversation-service areas passed cleanly against the working tree). The one genuinely open risk this revision originally flagged (§6, invariant 4 — a tracked chat client called from inside a tracked tool invocation corrupting the outer turn's own usage report) turned out to be **real, not theoretical**, and is now fixed and regression-tested; see §6 for the mechanism. The `PermissionEndpointFilter`/`UserGrantReader` duplication this revision also flagged is resolved: the filter's own grant loader is deleted and it now calls `IUserGrantReader` like everything else. **All of this remains uncommitted.** Every EP-3/EP-4/EP-5 story below is marked **Done** on that basis — implemented and tested, per this codebase's own bar for "done" — except where a specific acceptance criterion is still genuinely unmet, which is called out by name where it applies.

## 1. Overview

**Problem.** Enterprise GPT ingests an uploaded document into a full retrieval corpus — extract, chunk, embed, index in SQL Server vector search — but a user who just wants to know what a document *says* has no first-class way to ask for that. The closest existing capability, `document_search` (`Enterprise.Gpt.Service/Tool/DocumentRetrievalService.cs`), only ever returns short matched passages for a specific query; it was never built to condense a whole document, and routing "summarize this" through it would spend the *conversation's own selected chat model* — which could be an expensive reasoning deployment — on a task that has nothing to do with reasoning quality and everything to do with cost predictability.

**Solution.** The model itself decides when a user wants a summary and calls a second tool, `document_summarize`, mid-turn — no separate request, no button, no job to poll. The tool reduces a named document, or (when no name is given) every document the conversation can see, to text using the same pinned, cheap, provider-agnostic summarization engine EP-2 already built: single-pass when the document fits the summarizer's own budget, hierarchical map-reduce with a collapse loop when it does not. The summarizer is resolved through `IChatClientResolver`, and every fact that describes it — context window, provider, deployment name, price — is read from its `[Core.Ref].[Model]` catalog row in SQL at the moment of use, never restated in configuration. The result is one canonical summary per document, shared across every conversation that can read it, and free on every request after the first because the summary row itself is the cache. A digest — every document in scope reduced to one further pass — costs one map call per not-yet-summarized document plus one reduce call over all of them, not one fresh pass per document. Every model call this feature makes is billed to the conversation that asked for it — even a call over a project document — through the same `ConversationUsage` machinery that already accounts for chat turns and conversation naming, as **one row per run**, matching the grain a chat turn with several tool iterations is already billed at, and — critically — billed to that row **alone**, never to the outer turn's own (§6).

**Why hierarchical map-reduce, not the alternatives.** Two designs were considered and rejected for the fallback path, and the reasoning is recorded rather than just the conclusion (§9 restates both). This part of the design is unaffected by the tool pivot and unchanged from the original PRD.

- **Refine** (a sequential running summary, re-compressed against each new chunk in turn) is strictly serial — latency scales linearly with document size with no parallelism available — and early content drifts as it is recompressed at every step, which is the opposite of what a summary should preserve.
- **Cluster-then-summarize** (embedding-based extractive selection of "representative" chunks) is lossy by construction: it never reads the whole document, and a user who asked for a summary of the whole thing does not want one built from a sample of it.
- **Hierarchical map-reduce with a collapse loop** reads every chunk exactly once at the map stage and adds the one piece plain map-reduce is missing: a loop that keeps re-summarizing the map phase's own output when *that* output is still too large to reduce in one call — the case a document long enough to overflow even its own partial summaries would otherwise have no answer for at all.

**Success criteria.**

- **Cost containment**: 100% of `Core.ConversationUsage` rows with `Kind = Summarization` carry the pinned summarizer's `ModelId`/`DeploymentName` — never the requesting conversation's own selected chat model — measured by a scheduled query grouping those rows by `ModelId` and asserting exactly one distinct value across the whole table, and by `US-402`'s own test. **A second, equally important half of this criterion, discovered only once it was tested**: a summarization run's tokens must also never land on the *outer turn's* `Kind = Chat` row — see §6, invariant 4, and `US-402`'s regression guard.
- **Completeness**: 0 documents that fit inside the summarizer's single-pass budget are ever routed through map-reduce, and 0 documents that do not fit ever fail *for that reason* — measured by `US-203`'s unit test matrix spanning documents just under, at, and just over the budget boundary, and by `document_summary.run.duration`'s outcome tag showing zero occurrences of a context-window failure reason in production.
- **Accounting integrity**: every summarization *run* — not every model call inside it — has exactly one `ConversationUsage` row, and that row's token totals equal the summary row's own persisted totals exactly, and the *turn's own* usage row is untouched by the run — measured by comparing `COUNT(*)` from `Core.ConversationUsage WHERE Kind = Summarization` for a document's most recent run against `1`, the row's `InputTokens`/`OutputTokens` against the summary row's own `US-404`-persisted totals, and the turn's own `Kind = Chat` row's tokens against what the turn's own model call alone produced — all three now proven by `US-402`'s tests.
- **Cache effectiveness**: a repeat request for an already-summarized document, with no prompt-template change since it was generated, writes 0 `ConversationUsage` rows and leaves every conversation token counter unmoved — measured by `US-406`'s test and, in production, by zero `document_summary.run.duration` records tagged `summary.path = cache-hit` alongside a nonzero `document_summary.run.duration` count tagged with any other path.
- **In-turn responsiveness**: 0% of tool invocations are allowed to run the conversation's own SSE stream past the configured `ToolTimeoutSeconds` deadline (default 300s) — a breach always surfaces to the model as a sanitized, relayable sentence rather than hanging the turn — measured by `US-505`'s test and, in production, by the `summary.outcome = cancelled` share of `document_summary.run.duration`. This replaces the original PRD's job-status-polling criterion, which measured an asynchronous mechanism this design no longer has.

A user, end to end: Priya is in a project conversation holding three documents — one she uploaded herself, two the project already had. She asks, in plain chat, "what's the gist of the big policy document?" The model recognizes this as a summarization request rather than a specific question, and calls `document_summarize` with that file's name. The activity card in her stream shows "Summarizing policy.pdf…" while the engine splits the 40-page document into map units, summarizes each, collapses the partial summaries once because they still overflow the budget, and reduces the result; a paragraph appears in the assistant's answer a minute or two later. She then asks, "can you summarize everything in here?" — no file name this time. The model calls the same tool with no argument. Two of the three documents already have summaries — hers from a moment ago, one project document a teammate summarized last week — so the digest costs one map call for the one remaining document and one reduce call over all three summaries, not three fresh passes. Every one of those calls, including the ones over the project's own documents, shows up on *her* conversation's usage, at the summarizer's flat, predictable rate — never at whatever the conversation's selected chat model would have charged, and never doubled onto the turn's own bill either. She never saw a button, a job id, or a loading panel; she asked, and the answer came back inside the conversation she was already having.

## 2. Goals & non-goals

**Goals.**

- **The summarization tool is the surface.** A user asks in chat; the model decides whether the request calls for a summary and, if so, calls `document_summarize` mid-turn — the same way it already decides whether to call `document_search`. There is no separate request path.
- One constant, cheap, pinned summarization model, resolved provider-agnostically through the existing `IChatClientResolver.Resolve(providerId)`, so summarization cost never scales with whatever model — reasoning-enabled or otherwise — the conversation's user happens to have selected for chat.
- Read every fact about the summarizer — context window, provider, deployment name, both price columns — from its `[Core.Ref].[Model]` catalog row in SQL, never restated in configuration beyond the row's own id.
- Summarize in a single model call when a document fits the summarizer's budget; fall back to hierarchical map-reduce with a collapse loop when it does not, so no document ever fails to summarize purely because it is large.
- One canonical, shared summary per document — a project document summarized once by any conversation is reused, at zero further cost, by every conversation that can read it, and is superseded automatically (never on an explicit "regenerate" request) only when the prompt templates that produced it change version.
- A digest that reduces over the per-document summaries already available for the conversation's retrieval scope, recomputed on every request and never persisted.
- Bill every summarization model call to the conversation that requested it, through the existing `ConversationUsage` machinery, as one row per run, **and to that row alone** — no new reporting surface, no new usage table, and no bleed into whatever turn happened to trigger the call.
- Hide the summarizer's catalog row from the chat model picker behind a new, purpose-built flag, without touching `DateDeactivated` (which means something else entirely: retired, not merely unpickable).
- Gate the capability on the existing `Upload File` permission grant; add no new permission id.
- **Zero client-side footprint.** Everything this feature does is visible through the assistant's existing SSE activity feed — the same `AssistantUiEvent` frames a tool call already produces. No route, panel, store, or component is added to `enterprise-gpt-ui/`.

**Non-goals.**

- **Automatic summarization at ingest.** A document is summarized only when a conversation's own request implies it — never as a side effect of upload.
- **Any HTTP route of its own.** There is no `POST`/`GET` surface for a summary or a digest; the only caller of `IDocumentSummaryService` is the tool.
- **A background job or job-status polling.** Summarization runs synchronously, inline, on the turn that asked for it, bounded by its own deadline (`ToolTimeoutSeconds`) rather than handed to `Enterprise.Gpt.Service/BackgroundJobs/`.
- **Any frontend surface.** No chip menu entry, no summary panel, no "Summarize this conversation" button, no store. `enterprise-gpt-ui/` is untouched by this feature end to end.
- **A second retrieval corpus, embeddings, or citations over summary text.** A summary is read text, not a searchable index; nothing this PRD adds is reachable from `document_search` or `Tool/DocumentRetrievalSql.cs`.
- **Refine or cluster-then-summarize as the fallback algorithm.** Both were considered and rejected for v1; the reasoning is recorded in §1 and §9.
- **A new RFC 9457 problem type.** This feature has no HTTP surface of its own to need one. A failure is relayed to the model as a plain, sanitized sentence inside the tool's thrown exception — exactly the mechanism `document_search`'s own failures already use — never as a problem response.
- **A new permission id.** Summarization is gated on the existing `Upload File` grant (`b1c2d3e4-f5a6-4b7c-8d9e-0f1a2b3c4d5e`).
- **A user-facing usage or cost API.** `ConversationUsage` has no HTTP surface today and this PRD does not invent one; summarization cost is visible through SQL and through the run-level telemetry `US-405` adds.
- **Summarizing anything other than the platform's own ingested document text.** Images, audio, and video are out of scope; so is summarizing raw, un-ingested bytes.
- **Cross-conversation synthesis beyond one conversation's own retrieval scope.** The digest reduces over exactly what `DocumentRetrievalService.GetScopeAsync` already resolves for that conversation — its own documents plus, when it belongs to one, its project's — never documents from an unrelated conversation.
- **A regeneration flag or a manual "summarize again" action.** A document's text never changes after ingestion, so the only thing that can stale a cached summary is a change to the prompts that produced it; the stored `PromptVersion` self-invalidates the cache automatically (§6), and a flag the model could set on its own tool call would be a flag the model could spend money with.
- **Documentation stories.** Docs under `docs/` and the `CHANGELOG.md` entry are the SE Technical Writer flow's job, after implementation and review are both complete.

## 3. Users & access

**Personas.**

- **Chat user**: a signed-in employee holding a conversation that can see at least one document. Asks a question in plain language; never sees a "Summarize" button, a job id, a deployment name, or which path (single-pass or map-reduce) served the request — the model decides when to call the tool and reports back inline, exactly as it already does for `document_search`.
- **Administrator**: holds `PermissionIds.Administrator` (`a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d`). Toggles a catalog model's picker visibility (`IsUserSelectable`) from the existing admin models screen; does not separately administer "which model is the summarizer" — that is an operator-owned configuration value pointing at a `Model.Id`, not a database flag an admin screen writes.
- **Operator**: runs the deployment. Sets `Summarization:Enabled`, `:ModelId`, the safety fraction, the map-unit ceiling, the digest cap, the tool and per-call deadlines; reads the run-level telemetry this PRD adds.
- **Backend engineer**: owns every epic in this document, in `enterprise-gpt-api/` under `.claude/rules/csharp.md` and `aspnet-rest-apis.md`. **There is no frontend engineer role for this feature** — the pivot removed the only epic that touched `enterprise-gpt-ui/`.

**Role-based access.**

- **Anonymous**: no access. Every conversation route sits inside groups already `.RequireAuthorization()`'d, and the tool only ever exists inside an authenticated turn.
- **Chat user without the `Upload File` grant**: the tool is never attached to `ChatOptions.Tools` for that turn. The model has no way to discover or call it — there is no disabled affordance to see, because there is no UI at all; the absence is silent in exactly the way `document_search`'s own absence on a documentless conversation is silent.
- **Chat user with the grant**: the tool is attached whenever the flag is on, the conversation's scope has at least one document, and the selected model supports tools. It can summarize any document the scope resolves — the conversation's own documents and, when it belongs to one, its project's — under the exact same ownership rule `document_search` already enforces, because both tools close over the identical `DocumentRetrievalScope`. A document outside that scope simply cannot be named; there is no separate 404-vs-403 distinction to get right, because there is no route to get it wrong on.
- **Administrator**: not implicitly granted summarization — an administrator without the `Upload File` grant sees the tool go unattached exactly like any other ungranted user, matching the codebase's standing rule that an administrator holds only what they were given.

The grant is resolved through the singleton `IUserPermissionCache`, via `IUserGrantReader` (`Service/Caching/UserGrantReader.cs`). **This is now the single grant-loading query in the codebase.** `PermissionEndpointFilter` originally kept its own private loader in parallel with the new reader; that duplication is resolved — the filter's `LoadGrantsAsync` is deleted and it now resolves `IUserGrantReader` and calls it, exactly as the tool-attachment path does. This was more than tidying: the filter's old loader captured the request's own `IServiceProvider`, and `IUserPermissionCache.GetOrLoadAsync`'s own doc comment warns that a loader may be shared with concurrent callers and outlive the request that started it — `UserGrantReader` creates its own scope via `IServiceScopeFactory`, which the filter's inline loader never did, so consolidating onto it also closed a latent (if narrow) correctness gap, not merely a style one.

## 4. Functional requirements

| ID | Requirement | Priority | Epic(s) |
| --- | --- | --- | --- |
| FR-1 | The catalog carries a `Model.IsUserSelectable` flag, defaulting to `true`, that hides a row from the chat model picker without touching `DateDeactivated` | P0 | EP-1 |
| FR-2 | A migration corrects the seeded summarizer row's `DeploymentName`, `ContextWindowSize`, and `MaxOutputTokens`, and sets its `IsUserSelectable` to `false` | P0 | EP-1 |
| FR-3 | `GET api/models` (the chat picker's source) excludes non-user-selectable rows; `GET api/models/all` (the admin listing) is unaffected | P0 | EP-1 |
| FR-4 | An administrator can toggle a model's picker visibility from the existing admin models screen | P2 | EP-1 |
| FR-5 | A `Summarization` configuration section names the summarizer by an existing `Model.Id`, validated at startup against the database and against registered chat-client providers | P0 | EP-1 |
| FR-6 | A summarization request against a summarizer catalog row whose `ContextWindowSize` is `<= 0` fails clearly rather than being treated as unbounded | P0 | EP-2 |
| FR-7 | A document's text is reassembled from its existing chunk rows — never re-extracted from the blob — for both conversation and project document families, with the chunker's own overlap stripped at each seam | P0 | EP-2 |
| FR-8 | Token counting for the fit decision uses the summarizer's own provider and deployment name, never the requesting conversation's selected chat model | P0 | EP-2 |
| FR-9 | A document that fits the summarizer's budget is summarized in exactly one model call | P0 | EP-2 |
| FR-10 | A document that does not fit is split into map units sized to the summarizer's own computed budget, not the 512-token retrieval chunk size, and refused outright — before any map call — once the split would exceed a configured ceiling (`MaxMapUnits`, default 40) | P0 | EP-2 |
| FR-11 | Map-phase calls run under a bounded concurrency gate, a per-call timeout, and cooperative cancellation | P0 | EP-2 |
| FR-12 | A collapse loop re-summarizes partial summaries that still exceed the budget until they fit, bounded by a maximum pass count | P0 | EP-2 |
| FR-13 | Exactly one final reduce call produces a document's canonical summary text | P0 | EP-2 |
| FR-14 | Every summarization prompt (single-pass, map, reduce) delimits the document or partial-summary text against instructions embedded inside it | P0 | EP-2 |
| FR-15 | One canonical summary row is persisted per document, shared across every conversation that can read the document; it is superseded in place only when the running `SummarizationPrompts.PromptVersion` advances past the value stored on the row — never by an explicit regenerate request | P0 | EP-3 |
| FR-16 | A summary row is soft-deleted when its document, its parent conversation, or its parent project is soft-deleted | P0 | EP-3 |
| FR-23 | Every summarization **run** — the single-pass call, or the whole map/collapse/reduce sequence, or a digest's own reduce over per-document summaries — writes exactly **one** `ConversationUsage` row of kind `Summarization`, billed to the conversation that requested it — including a run made over a project document — **and never to the turn's own `Kind = Chat` row when the run was triggered from inside one** (§6, invariant 4) | P0 | EP-4 |
| FR-24 | Conversation `InputTokens`/`OutputTokens` increment in the same transaction as the run's usage row; `ConversationUsage.ContextTokens` is always `null` for a summarization row | P0 | EP-4 |
| FR-25 | A summary row persists the run's token totals and model-call count | P1 | EP-4 |
| FR-26 | Summarization run duration, path (`cache-hit`/`single-pass`/`map-reduce`/`digest`), outcome, and map-unit count emit through the existing `ChatMetrics` meter, at run grain, not call grain | P1 | EP-4 |
| FR-27 | A cache-hit request writes zero usage rows and leaves every conversation counter unmoved | P0 | EP-4 |
| FR-35 | The summarization tool is attached to a turn's `ChatOptions.Tools` only when `Summarization:Enabled` is true, the conversation's document scope is non-empty, the selected model supports tools, and the caller holds the `Upload File` grant (checked through `IUserGrantReader`) | P0 | EP-5 |
| FR-36 | The tool accepts one optional `documentName` parameter; when it names exactly one document in the turn's scope, that document alone is summarized | P0 | EP-5 |
| FR-37 | When `documentName` is omitted, the tool reduces over every document in scope (a digest, capped at a configured maximum document count, default 25); when a supplied name matches zero or more than one document, the tool refuses outright — naming the available documents — rather than guessing or falling back to a full digest | P0 | EP-5 |
| FR-38 | A scope holding exactly one document, with no name supplied, is summarized directly rather than routed through a one-document "digest" reduce call | P1 | EP-5 |
| FR-39 | The whole tool invocation — every call the run makes, plus its persistence and billing — is bounded by a single configurable deadline (`ToolTimeoutSeconds`) distinct from any one model call's own timeout; a breach surfaces to the model as a sanitized, relayable message, never as a failed turn | P0 | EP-5 |
| FR-40 | The tool reports progress through the ambient `ChatProgress` reporter at least at the start and the end of a run, reaching the client through the same SSE activity feed (`AssistantUiEvent` `ActivityStarted`/`ActivityProgress`/`ActivityCompleted`) every other tool already uses | P1 | EP-5 |
| FR-41 | A summarization tool named identically to an already-selected MCP tool stands down for that turn rather than colliding with it, mirroring `document_search`'s own precedent | P0 | EP-5 |
| FR-42 | Every terminal failure the tool can produce is relayed to the model as a sanitized, actionable sentence; the underlying exception is logged and never relayed verbatim | P0 | EP-5 |
| FR-33 | The whole feature sits behind `Summarization:Enabled`, defaulting to `false` everywhere including development, with a no-redeploy rollback that leaves already-generated summaries untouched | P0 | EP-6 |
| FR-34 | A per-user and a per-conversation summarization request-rate bound exist and are enforced when configured; the map-unit ceiling (FR-10) and the digest document-count cap (FR-37) are enforced unconditionally, since both guard against one pathological request rather than a tunable usage pattern | P1 | EP-6 |

**Retired by this revision**: FR-17 (background job stage), FR-18–FR-22 (summary/digest HTTP routes and job-status polling), FR-28–FR-31 (frontend chip surface), and FR-32 (a standalone route-level permission gate — folded into FR-35, since gating is now entirely inside tool attachment and there is no separate route layer to re-gate). Every retired requirement's underlying *concern* is still met — permission gating, cost containment, cache behavior — by a requirement above; only the mechanism changed.

## 5. User experience

**There is no new screen, panel, menu entry, or button.** This feature adds nothing to `enterprise-gpt-ui/`. The entire user experience is: a user asks a question in the conversation they are already having, and the model — not the user — decides whether that question calls for a summary.

**Core experience.**

1. A user with the `Upload File` grant, in a conversation whose scope has at least one document, asks something the model recognizes as wanting a document's gist rather than a specific fact — "what does this say", "summarize the attached policy", "give me an overview of my files". The model calls `document_summarize`, optionally naming a file.
2. The turn's activity feed — the same `AssistantUiEvent` stream that already shows `document_search`'s "Searching…" card — shows a "Summarizing `<name>`…" (or, for a digest, "Summarizing `N` documents…") sub-status for as long as the run takes. This is the only visual indication anything is happening; there is no separate loading surface.
3. If a canonical summary already exists for the named document (or, for a digest, for every document in scope) and no prompt-template version bump has invalidated it, the tool returns instantly — no model call, no billing, no perceptible delay beyond the round trip itself.
4. Once the run completes, the model receives the summary text as the tool's result and folds it into its own answer, in its own words or largely verbatim, exactly as it already does with `document_search`'s passages.
5. Asking again for the same document later — in the same turn or a different one, from the same conversation or a different conversation over the same project document — is a cache hit unless the prompt templates changed in between, in which case the tool regenerates transparently; the user never asks for or sees a "regenerate."

**Edge cases.**

- **Ambiguous or unknown file name**: the tool refuses to guess. It returns a result naming the documents that *are* available and asks the model to try again with one of those names or with none at all — spending no model call on a near-miss.
- **A single document in scope, no name given**: summarized directly; no wasted second reduce call over a "digest" of one summary.
- **A run that would exceed `ToolTimeoutSeconds`**: the tool's own deadline fires (independent of, and shorter-fused than, the turn's overall cancellation), and the model receives a sentence explaining that summarizing took too long and offering to answer specific questions about the document instead.
- **A model without tool support, or a conversation with no documents at all**: the tool is never attached; the model cannot discover it, and no error, message, or degraded affordance is shown, because nothing ever promised the capability.
- **Feature off, or caller ungranted**: identical from the user's perspective to "the model didn't think a summary was needed" — there is no way to distinguish "the tool doesn't exist for me" from "the model chose not to call it," and that ambiguity is intentional: a capability with no client surface has no button to grey out.
- **A generation failure** (provider timeout, empty completion, an unsummarizable zero-chunk document): relayed to the model as a sanitized sentence it can pass on to the user; the underlying document and its chunks are completely unaffected.

**What was removed.** The prior design's attachment-chip "Summarize" action, its summarizing indicator, its regenerate affordance, its digest button, and its `summarizing | summarized | failed | absent` store state are all gone. No component in `enterprise-gpt-ui/` needs to exist, be reviewed for accessibility, or count against the bundle budget — this feature's entire client-side cost is zero.

## 6. Technical considerations

**Four things shape everything below.** The first three are unchanged from the original design (EP-2/EP-3's engine and schema are unaffected by the tool pivot); the fourth was introduced by running the engine synchronously inside an already-tracked turn, turned out to be a real bug rather than a theoretical one, and is now fixed and tested.

**1. A `ContextWindowSize` of `<= 0` must mean *unconfigured, refuse* for this feature — the opposite of what the shared budget calculator already means by it.** `ContextBudgetCalculator.Apply` (`Enterprise.Gpt.Service/Tokenization/ContextBudgetCalculator.cs`) treats `contextWindowSize <= 0` as **unbounded**, which is the right reading for a chat turn (the alternative is destroying a user's context over an unset catalog field) and the wrong one here, where it would mean attempting to send a multi-million-token document in a single call. `SummarizationBudgetCalculator.Compute` (`Enterprise.Gpt.Service/Summarization/SummarizationBudget.cs`) inverts the convention explicitly and throws `SummarizerNotConfiguredException` instead.

**2. `Model.MaxOutputTokens` reaches the wire here, and nowhere else in the codebase.** `ConversationService.CreateChatOptionsAsync` never sets `ChatOptions.MaxOutputTokens` for a chat turn, for any provider — only `DocumentSummarizer`'s own calls do, from the pinned catalog row. An unbounded map call would be unbounded output billed at the summarizer's rate on every one of potentially hundreds of calls a large document produces (§9 assumption: this is not yet verified against a live deployment).

**3. A summary must join the four existing soft-delete cascades.** `ConversationService.DeactivateConversationAsync`, `DeactivateConversationsBulkAsync`, `ProjectService.DeactivateProjectAsync`, and `DeactivateProjectDocumentAsync` each ran a sequence of `ExecuteUpdateAsync` statements deactivating a document's chunk rows and then the document itself; a summary table that does not join those statements leaves a fully readable summary of a document — and everything it said — after the document is deleted. All four now deactivate the matching `Core.ConversationDocumentSummary`/`Core.ProjectDocumentSummary` row first, ahead of the chunk cascade, in the same transaction, and each of the four is now exercised by its own integration test against real SQL Server (`Persistence/DocumentSummaryCascadeIntegrationTests`: `DeactivateConversation_DocumentHasASummary_DeactivatesItAlongside`, `DeactivateConversationsBulk_DocumentsHaveSummaries_DeactivatesEveryOne`, `DeactivateProject_DocumentHasASummary_DeactivatesItAlongside`, `DeactivateProjectDocument_DocumentHasASummary_DeactivatesItAlongside`).

**4. Resolved — a tracked chat client called from inside an active tracked tool invocation was corrupting the outer turn's own usage report, and this was real, not theoretical.** `IChatClientResolver.Resolve(providerId)` returns the *fully decorated* pipeline for every provider — `.UseToolTracking(ConfigureToolTracking).UseFunctionInvocation(...)` is applied at DI registration for all four providers, with no separate, untracked client available — and `DocumentSummarizer` resolves the summarizer's client through this same resolver. A test that drove a summarization call through the decorated pipeline from inside a tracked tool invocation confirmed the failure mode directly: the nested call's 900,000 input tokens landed on the turn's own `ConversationUsage.ToolInputTokens`, a double-charge — once on the `Chat` row at the *conversation's own selected model's rate*, once on the dedicated `Summarization` row at the summarizer's — that defeated this feature's whole cost-containment goal. **There is no public opt-out in Andes.** The tracking middleware's ambient scope (`AmbientScope`) is `internal`, and its own doc comment states its purpose is to connect a nested tracked pipeline to the invoking tracker — the roll-up is the package's *intended* behavior, not a bug in it, so there was nothing to configure or disable.
>
> **The fix** is `DocumentSummaryService.RunDetachedAsync`: it runs the engine's work inside `ExecutionContext.SuppressFlow()` + `Task.Run`, so the work executes on an execution context that never inherited the tracker's `AsyncLocal`. `Activity.Current` is carried across by hand into the detached task, so the summarizer's own spans still hang off the turn's trace — the aim is to detach from the *usage tracker* specifically, not from telemetry generally. Only the engine's own model calls are detached this way; whatever the tool itself reports through `ChatProgress.Report` stays attributed to the turn, because that is what draws the activity card the user is watching.
>
> **The regression guard** is `ConversationServiceTests.StreamConversationAsync_SummaryToolMakesItsOwnModelCall_DoesNotBillItToTheTurn`, which runs the *real* `DocumentSummaryService` over a substituted `IDocumentSummarizer` that makes a genuine nested call on the real middleware pipeline, and asserts both halves of the fix: the turn's own usage row does not carry the summarizer's tokens, and exactly one `Summarization` row does, at the summarizer's own `ModelId`/`DeploymentName`. It fails without `RunDetachedAsync`.

**Integration points.** All verified against the working tree.

| Concern | Where |
| --- | --- |
| The tool to copy the shape of | `Enterprise.Gpt.Service/Tool/DocumentTool.cs` — a static `Create(scope, ...)` factory returning an `AIFunction`, a private per-turn invoker class, `ChatProgress.Report` for activity-feed sub-statuses, a sanitized `InvalidOperationException` on failure |
| The tool itself | `Enterprise.Gpt.Service/Tool/DocumentSummaryTool.cs` — `ToolName = "document_summarize"`, `Create(scope, conversationId, IDocumentSummaryService, toolTimeoutSeconds, logger)`, ambiguous-name refusal via `DocumentRetrievalService.MatchByName`; tested by `tests/Enterprise.Gpt.Unit.Test/Tool/DocumentSummaryToolTests.cs` (14 tests) |
| Name-matching shared between the two tools | `Enterprise.Gpt.Service/Tool/DocumentRetrievalService.cs` — `internal static IReadOnlyList<RetrievableDocument> MatchByName(DocumentRetrievalScope, string wanted)`, extracted from `document_search`'s own `ResolveDocumentFilter` specifically so the two tools that take a document name agree on what a name means |
| The persistence/caching/billing seam, and the double-billing fix | `Enterprise.Gpt.Service/Summarization/DocumentSummaryService.cs` — `IDocumentSummaryService.GetOrCreateAsync`/`CreateDigestAsync`, `RunDetachedAsync` (§6 invariant 4); tested by `tests/Enterprise.Gpt.Unit.Test/Summarization/DocumentSummaryServiceTests.cs` (12 tests) |
| The permission-check seam, now the only one | `Enterprise.Gpt.Service/Caching/UserGrantReader.cs` — `IUserGrantReader.GetGrantsAsync(userId, ct)`, backed by `IUserPermissionCache.GetOrLoadAsync` with its own loader; `PermissionEndpointFilter` calls it too, its own former private loader deleted |
| Where the tool is attached | `ConversationService.CreateChatOptionsAsync` — `attachSummaryTool = _summarizationOptions.Enabled && documentScope?.HasDocuments is true`, degraded on a non-tool model, gated on `IUserGrantReader` returning `PermissionIds.UploadFile`, guarded against an MCP tool already named `document_summarize`; tested by the `ConversationServiceTests` summarization-attachment block |
| Provider-agnostic client resolution | `Enterprise.Gpt.Service/Chat/ChatClientResolver.cs` — `IChatClient Resolve(Guid providerId)`, throws `ProviderNotConfiguredException` → 503 `/problems/provider-not-configured` on a chat turn's own path (not reachable from the tool, which has no HTTP response of its own) |
| The tracked-client double-completion mechanism and its fix (§6 invariant 4) | `Enterprise.Gpt.Service/Chat/ChatUsageScope.cs`, `ChatUsageObserver.cs` (the ambient and its documented "last write wins" contract); `DocumentSummaryService.RunDetachedAsync` (the fix) |
| Catalog row to reference | `Enterprise.Gpt.Entity/Model.cs`; seed in `Enterprise.Gpt.Repository/Configurations/ModelConfiguration.cs` (`Id = c36e22ed-262a-47a1-b2ba-06a38355ae0f`) |
| Non-turn model call that already records usage — the shape `US-402` copies | `Enterprise.Gpt.Service/ConversationService.cs` naming-call write (`Kind = ConversationUsageKinds.Naming`, one `BeginTransactionAsync`, the usage row and the conversation counter update together) |
| Usage grain and enum | `Enterprise.Gpt.Common/Enums/ConversationUsageKinds.cs` — `Summarization = 3`, whose own doc comment states the run grain: *"One row per run rather than per model call, matching how a chat turn is recorded... the call count itself is persisted on the summary row."* |
| What a conversation can see — the tool's and the digest's shared input | `Enterprise.Gpt.Service/Tool/DocumentRetrievalService.GetScopeAsync` → `DocumentRetrievalScope(ConversationId, ProjectId, IReadOnlyList<RetrievableDocument> Documents)` |
| Token counting | `Tokenization/TokenEstimatorResolver.cs`, `Tokenization/TokenizerProvider.cs`, `Tokenization/PromptOverhead.cs`, `Tokenization/ContextBudgetCalculator.cs`, `Settings/TokenEstimationOptions.cs` |
| Source text and seam stripping | `Core.ConversationDocumentChunk`/`Core.ProjectDocumentChunk`, ordered by `Index`; `DocumentRetrievalService.AppendWithoutOverlap` |
| Chunker precedent for "not the retrieval chunk size" | `Enterprise.Gpt.Service/Chunking/TokenTextChunker.cs` — 512 tokens, 128 overlap |
| Schema and configuration | `Enterprise.Gpt.Entity/BaseDocumentSummary.cs`, `ConversationDocumentSummary.cs`, `ProjectDocumentSummary.cs`; `Enterprise.Gpt.Repository/Configurations/ConversationDocumentSummaryConfiguration.cs`, `ProjectDocumentSummaryConfiguration.cs` |
| Document entities the summary FKs into | `Enterprise.Gpt.Entity/BaseDocument.cs`, `ConversationDocument.cs`, `ProjectDocument.cs` — the last's own doc comment ("readable by every conversation in that project") is why one canonical, shared summary per document is correct |
| Soft-delete cascades a summary table must join | `ConversationService.DeactivateConversationAsync`/`DeactivateConversationsBulkAsync`, `ProjectService.DeactivateProjectAsync`/`DeactivateProjectDocumentAsync` (§6 invariant 3) |
| Options-class and validation precedent | `Enterprise.Gpt.Service/Settings/SummarizationOptions.cs` (carries every setting this feature needs — see below); `Program.cs`'s `AddOptions<T>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` pattern; tested by `tests/Enterprise.Gpt.Unit.Test/Settings/SummarizationOptionsTests.cs`, which now covers the four new keys and asserts the shipped configuration has `Enabled: false` |
| Prompts, and the delimiter pattern to copy | `Enterprise.Gpt.Service/Prompts/SummarizationPrompts.cs` + its three `document-summary-*.md` templates (map/reduce/single-pass, EP-2); `Enterprise.Gpt.Service/Prompts/ConversationPrompts.cs`'s `BuildDocumentSummaryToolPrompt(summaryToolName, retrievalToolName)` + `document-summary-tool-prompt.md`, which tells the model when to prefer `document_summarize` over `document_search` |
| Latest migration to follow | `Enterprise.Gpt.Repository/Migrations/20260825050920_AddMcpServerHeaders` — the **thirteenth**. The summary tables' own migration, `20260825224934_AddDocumentSummaries`, is the **fourteenth**, not the thirteenth as the original PRD stated. |
| Telemetry | `Enterprise.Gpt.Service/Observability/ChatMetrics.cs` — `enterprise_gpt.document_summary.run.duration` (seconds, tagged `deployment.name`/`summary.path`/`summary.outcome`) and `enterprise_gpt.document_summary.run.map_units`, both on the existing `Meter`; recorded from `DocumentSummaryService.MeasureAsync` |
| Integration test infrastructure fix | `tests/Enterprise.Gpt.Integration.Test/TestInfrastructure/IntegrationTestFixture.cs` — a new `ClearDocumentSummariesAsync` helper, called from `ResetModelsAsync` (§9) |
| No new RFC 9457 problem types | This feature has no HTTP surface of its own; `Api/Problems/ProblemTypes.cs` is untouched |

**Data storage & privacy.**

- **`BaseDocumentSummary` mirrors `BaseDocumentChunk`'s split**, exactly as designed: an abstract, unmapped base (`: BaseModifiedEntity`, no `[Table]` of its own) carrying the summary text, the summarizer's `ModelId` (FK) and `DeploymentName` snapshotted at generation time, a `PromptVersion` string, run-total `InputTokens`/`OutputTokens`, `ModelCallCount`, `MapUnitCount`, `CollapsePasses`, and a unique FK to its owning document. `Core.ConversationDocumentSummary` (`ConversationDocumentId`) and `Core.ProjectDocumentSummary` (`ProjectDocumentId`) are the two derived, mapped tables.
- **The unique index on each table's document FK is filtered `[DateDeactivated] IS NULL`**, matching `ConversationDocumentChunkConfiguration`'s own ordinal index — so a soft-deleted summary (its document, conversation, or project was deleted) does not block a later document from being summarized again. Without the filter, a summary deactivated alongside its document would make the next attempt at that document a unique-constraint violation rather than a fresh insert.
- **Generation time is `BaseModifiedEntity`'s own inherited `DateModified`, not a duplicate column.** A regeneration — triggered only by a prompt-version mismatch, never by a user action — is a full-row rewrite, never a new row and never a version history.
- **No regeneration flag exists anywhere in this design, and that is deliberate (§2, §9).** `DocumentSummaryService.ReadCachedTextAsync` filters on `PromptVersion == SummarizationPrompts.PromptVersion`; a row whose version no longer matches the running one is treated as a cache miss and rewritten in place. A document's text never changes after ingestion, so this is the *entire* invalidation mechanism — there is nothing else that could stale a summary, and giving the model a way to force regeneration would be giving it a way to spend money on demand.
- **The reassembled document text is never persisted a second time.** Only the resulting summary text is written to disk.
- **A summary's soft delete follows its document's, transitively**, in the same transaction as the document's own deactivation (§6 invariant 3).
- **The map-unit ceiling is enforced before any spend, not after.** `DocumentSummarizer.MapReduceAsync` checks `units.Count > _options.MaxMapUnits` immediately after `MapUnitSplitter.Split` produces the candidate units and *before* the map phase dispatches a single call — an oversized document is refused at zero cost rather than run to the ceiling and stopped with a truncated, already-billed result. Covered by two new `DocumentSummarizerTests` cases.
- **Migration placement.** This feature adds three migrations in total, all additive: `US-101`'s `Model.IsUserSelectable` column and `US-102`'s seed-row correction (wave 1, already shipped), and the summary tables' own migration, which follows both of those *and* every migration shipped since — it is the **fourteenth** migration overall (`20260825224934_AddDocumentSummaries`), not the thirteenth as originally stated, because `20260822191806_AddMcpServerIconKey` and `20260825050920_AddMcpServerHeaders` both landed first.
- **Prompt content never rides telemetry.** The document text, the partial summaries, and the final summary are model input/output persisted to SQL and returned to the caller (as the tool's own result, not a metric); the run-level instruments carry only a deployment name, a path, and an outcome — no document name, no conversation id, no summary text.

**Testing infrastructure note.** Any table with a *required* FK to `Core.Ref.Model` must be cleared by the integration fixture before that fixture resets the catalog, or a class-ordering-dependent failure results — the same failure mode `IntegrationTestFixture`'s own existing comment already warns about for conversations. The two new summary tables were not initially added to that reset path, and it broke five unrelated `RequestLoggingIntegrationTests` purely through test-class execution order. Fixed via a shared `ClearDocumentSummariesAsync` helper, called from `ResetModelsAsync` ahead of the model reset itself. Anyone adding a further table with a required `Model` FK should extend the same helper rather than rediscovering this failure mode.

**Security.**

- **Summarization is gated on `Upload File`, not merely on scope membership — a deliberate departure from the download route's own posture**, because summarizing spends money on a cache miss and reading a document back does not.
- **Ownership is enforced by scope, not by a route-level check.** The tool closes over the same `DocumentRetrievalScope` `document_search` does, resolved once per turn against the caller's own conversation and project membership. A document outside that scope simply cannot be named — there is no 404-vs-403 distinction to get right, because there is no route to get wrong.
- **The document text is untrusted input reaching a model.** Every prompt this feature sends — single-pass, map, reduce — delimits the document or partial-summary text with a fresh, unguessable per-call token (`US-208`, unaffected by the pivot).
- **The tool's own result can leak document names on a miss.** `AvailableDocuments` and `Note` name every document in scope when a requested name does not resolve — the same posture `document_search`'s own `DocumentNames` already has, and no wider, since both tools share one scope.
- **No new secret, no new credential surface.** The summarizer is resolved through the same keyed `IChatClient` registrations every other provider call already uses.
- **Concurrent identical requests are not deduplicated in v1.** Two conversations racing on the same not-yet-summarized project document may each run the engine; the unique filtered index still guarantees exactly one final row, but the loser's work — and its billed tokens — is wasted rather than joined to the winner's. `UserPermissionCache`'s `Lazy<Task<T>>` single-flight pattern is the precedent a follow-up could copy if telemetry shows this happening often enough to matter (§9).
- **A tracked chat client called from inside a tracked tool invocation no longer corrupts the invoking turn's own billing (§6, invariant 4, resolved).** This was a real, proven security-adjacent correctness issue — a cost-containment feature that, unfixed, silently doubled a turn's cost and misattributed it — not merely a theoretical one.
- **`IUserGrantReader` is now the only grant-loading query in the codebase (§3, resolved).** `PermissionEndpointFilter` and the tool-attachment path can no longer drift into disagreeing about what "an active grant" means.

**Scalability & performance.**

- **The fit decision, the map split, and the collapse loop all measure against the summarizer's own computed budget — never the 512-token retrieval chunk size** (unaffected by the pivot).
- **Map-phase calls run under a bounded concurrency gate and a per-call timeout** (`SummarizationOptions.MaxConcurrency`, default 4; `CallTimeoutSeconds`, default 180), so one document's summarization cannot monopolize the process, and a single stalled call cannot hold things up indefinitely.
- **The whole tool invocation is itself now latency-bounded, not merely each model call inside it.** `SummarizationOptions.ToolTimeoutSeconds` (default 300, `[Range(30, 1800)]`) bounds map-reduce fan-out plus persistence plus billing as one deadline, linked to the turn's own cancellation token — because unlike the original job-based design, this work now runs inline on an open SSE stream holding the conversation's own lock, and a run with no ceiling of its own would be a turn that never ends.
- **A cache hit is the cheap path by construction**: `DocumentSummaryService.GetOrCreateAsync` returns from `ReadCachedTextAsync` with no model call and no database write beyond the read itself — the property the cache-effectiveness success criterion measures directly.
- **A document's own map-unit ceiling (`MaxMapUnits`, default 40) and the digest's document-count ceiling (`MaxDigestDocuments`, default 25) are unconditional, not opt-in**, unlike the original PRD's proposal — both are ordinary bindable ints with defaults, always enforced, because they guard against a single pathological request rather than a tunable usage pattern an operator might legitimately want off.
- **Detaching the engine's calls from the tracking ambient (§6 invariant 4) adds a `Task.Run`, not a thread pool concern of its own scale.** One summarization run's engine work moves to a pool thread for its duration; this is the same cost profile as any other CPU/IO-bound background dispatch already in the codebase and is not expected to be a scaling concern at this feature's volume.
- **No client bundle impact of any kind.** This section previously tracked the frontend's bundle budget; there is nothing left here to track.

**AI system requirements.**

- **The algorithm, precisely** (unchanged from the original PRD — EP-2's engine is unaffected by the pivot):
  1. Reassemble the document's text from its chunk rows, seams stripped by `AppendWithoutOverlap`. Count its tokens with the estimator for the **summarizer's own `ProviderId`**, using the tokenizer for its own `DeploymentName`.
  2. Compute the per-call budget from `ContextWindowSize`, `MaxOutputTokens`, prompt overhead, and the safety fraction. A `ContextWindowSize <= 0` refuses outright. If the document fits, summarize it in **one call** and stop.
  3. Otherwise, split into **window-sized map units** and summarize each under bounded concurrency, a per-call timeout, and cooperative cancellation — refusing outright if the split would exceed `MaxMapUnits`.
  4. **Collapse loop**: while the concatenated partial summaries still exceed the budget, group and re-summarize, repeating until they fit or a configured maximum pass count is reached.
  5. Run one final reduce call over whatever now fits, producing the document's single canonical summary.
- **Evaluation strategy** (unchanged): a fixed benchmark spanning single-pass, one-round map-reduce, and collapse-requiring documents, each with a human-reviewed reference summary, with the same 100%/≥90% pass thresholds as before EP-2 was accepted.
- **The digest is not a fourth algorithm.** It is the same reduce, run over per-document summaries instead of map units — confirmed exactly by `DocumentSummaryService.CreateDigestAsync`, which concatenates each document's `GetOrCreateAsync` result and calls `IDocumentSummarizer.SummarizeTextAsync` over the concatenation.
- **There is now a new `AIFunction`, and `UsageReportTranslator.ResolveMcpServerId`'s server-prefix rule does have something to consider.** `document_summarize` deliberately shares `document_search`'s `document`-prefixed naming convention for the same reason `document_search` was named the way it was: usage is attributed to an MCP server by matching the longest `{server}_` prefix, and a server literally named `document` would collide with both. No such server exists today; the risk is recorded, not eliminated, exactly as it already was for `document_search`.

## 7. Epics & user stories

**What "Done" means in this table, now.** Every EP-3, EP-4, and EP-5 story below moved from **In progress** (a working, uncommitted implementation with zero tests) to **Done** in this update: 41 new unit tests and 4 new integration tests were added, and the full suite — 1,631 unit + 377 integration — passes with zero failures. **"Done" here still means "uncommitted."** Nothing in this feature has been committed yet; a reviewer should read that as "ready to commit and code-review," not "already shipped." Where a specific acceptance criterion is still genuinely unmet, it is called out by name in that story rather than the story being held at a lesser status overall.

| ID | Epic | Goal | Priority | Estimate | Depends on |
| --- | --- | --- | --- | --- | --- |
| EP-1 | Catalog & configuration | The catalog can name a pinned, cheap summarizer model that is invisible to the chat picker | P0 | M | — |
| EP-2 | Summarization engine | Any document, however large, reduces to one canonical summary within the summarizer's own budget | P0 | L | EP-1 |
| EP-3 | Persistence | A summary has a durable, cacheable, shared identity | P0 | M | EP-1, EP-2 |
| EP-4 | Token accounting & telemetry | Every summarization run is billed to the conversation that asked for it, exactly once, and never to the turn that triggered it | P0 | M | EP-2, EP-3 |
| EP-5 | Tool integration | The model can call summarization mid-turn, gated, bounded, and visible in the activity feed — with zero client change | P0 | M | EP-2, EP-3, EP-4 |
| EP-6 | Governance & rollout | The capability is bounded, flagged, and reversible without a redeploy | P0 | S | EP-2, EP-5 |

EP-1 and EP-2 are unchanged from the original PRD and are marked **Done** below on every story. EP-3 and EP-4 keep only the stories that still make sense once there is no job and no route; the ones that do not (background job stage, HTTP routes, job-status polling) are removed rather than reworded, since rewording a route into a tool call would not be the same story. **EP-5 (Tool integration) is new** and replaces the deleted frontend epic in the numbering; it is the seam between EP-2's engine and a real conversation. EP-6 keeps its flag and its bounds, drops the one story (durable resume) that presumed a background job.

### EP-1: Catalog & configuration — Done

Unchanged from the original PRD. All five stories (`US-101`–`US-105`) are implemented, committed, and tested: `Model.IsUserSelectable`, the corrected seeded summarizer row, `GetModelsAsync`'s picker filter, the admin toggle, and the validated-at-startup `Summarization` configuration section. See migrations `20260825031549_AddModelIsUserSelectable` and `20260825031604_CorrectSummarizerModelSeed`, `Enterprise.Gpt.Service/Settings/SummarizationOptions.cs`, and `Enterprise.Gpt.Api/Startup/SummarizerBootstrapper.cs`.

| Story | Status |
| --- | --- |
| US-101 `[enabler]` Add `Model.IsUserSelectable` and migrate existing rows to `true` | Done |
| US-102 `[enabler]` Correct the seeded summarizer catalog row | Done |
| US-103 `[enabler]` Surface `IsUserSelectable` and exclude the summarizer from the chat picker | Done |
| US-104 Administrator can toggle whether a model is user-selectable | Done |
| US-105 `[enabler]` Configure the summarizer model, validated at startup | Done |

### EP-2: Summarization engine — Done

Unchanged from the original PRD. All eight stories (`US-201`–`US-208`) are implemented, committed, and tested — the context-window-refusal invariant, chunk reassembly, the fit decision, single-pass and map-reduce summarization, the collapse loop, the final reduce, and the delimiter-based prompts. See `Enterprise.Gpt.Service/Summarization/` (`DocumentSummarizer.cs`, `SummarizerModelResolver.cs`, `SummarizationBudget.cs`, `MapUnitSplitter.cs`, `DocumentTextAssembler.cs`) and `Enterprise.Gpt.Service/Prompts/SummarizationPrompts.cs`. `DocumentSummarizer` additionally enforces the map-unit ceiling (`_options.MaxMapUnits`, EP-6's original `US-602` scope) as part of its own map split, ahead of dispatching any call (§6) — now covered by two further `DocumentSummarizerTests` cases added in this update.

| Story | Status |
| --- | --- |
| US-201 `[enabler]` Refuse to summarize when the summarizer's context window is unconfigured | Done |
| US-202 `[enabler]` Reassemble a document's text from its chunk rows | Done |
| US-203 `[enabler]` Decide whether a document fits a single pass | Done |
| US-204 Summarize a document that fits in a single call | Done |
| US-205 `[enabler]` Split an oversized document into window-sized map units and summarize them under bounded concurrency | Done |
| US-206 Collapse loop reduces partial summaries that still overflow the budget | Done |
| US-207 `[enabler]` Final reduce produces one canonical summary | Done |
| US-208 `[enabler]` Prompts that delimit untrusted document text | Done |

### EP-3: Persistence — Done

#### US-301: `[enabler]` Add the document-summary tables and their migration

- **Story**: `[enabler]` Add `BaseDocumentSummary` (mirroring `BaseDocumentChunk`'s shape) and its two derived, mapped types `Core.ConversationDocumentSummary` and `Core.ProjectDocumentSummary`, each carrying the summary text, the summarizer's `ModelId`/`DeploymentName` snapshotted at generation time, a prompt version, run-total input/output tokens, the model-call count, map-unit count, and collapse-pass count, with a unique filtered FK index to its owning document. Unblocks US-302, US-404.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-207
- **Status**: Done. `Enterprise.Gpt.Entity/BaseDocumentSummary.cs`, `ConversationDocumentSummary.cs`, `ProjectDocumentSummary.cs`; `Enterprise.Gpt.Repository/Configurations/ConversationDocumentSummaryConfiguration.cs`, `ProjectDocumentSummaryConfiguration.cs`; migration `20260825224934_AddDocumentSummaries` (the fourteenth). Exercised end-to-end against real SQL Server by `Persistence/DocumentSummaryCascadeIntegrationTests` and against SQLite in-memory by `Summarization/DocumentSummaryServiceTests`. **Still uncommitted.**
- **Acceptance criteria**:
  - Given the two new tables, when they are configured, then each carries a unique index on its document FK (`ConversationDocumentId`, `ProjectDocumentId`), filtered `[DateDeactivated] IS NULL` — matching `ConversationDocumentChunkConfiguration`'s own ordinal index, so a summary deactivated alongside its document does not block a later document's own first summary. Confirmed by `GetOrCreateAsync_StoredSummaryDeactivated_IsAMissAndInsertsBesideIt`.
  - Given the migration, when it is generated, then it follows every migration that precedes it — the fourteenth overall — and adds only the two new tables; no existing table is touched.
  - Given a document with no summary yet, when its summary table is queried, then it returns zero rows. Confirmed by `GetOrCreateAsync_NoStoredSummary_SummarizesAndPersistsTheRun`.
  - Given the snapshotted `ModelId`/`DeploymentName` on a summary row, when the catalog's summarizer model is later repointed, then an already-generated summary keeps naming what actually produced it.

#### US-302: `[enabler]` Join summaries to the four soft-delete cascades

- **Story**: `[enabler]` Extend `ConversationService.DeactivateConversationAsync`, `DeactivateConversationsBulkAsync`, and `ProjectService.DeactivateProjectAsync`, `DeactivateProjectDocumentAsync` to also set `DateDeactivated`/`DateModified` on the corresponding summary row inside the same `ExecuteUpdateAsync` sequence — the invariant §6 calls out by name.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-301
- **Status**: Done. Implemented in all four methods, each deactivating the summary row ahead of the chunk cascade; each of the four is exercised by its own integration test in `Persistence/DocumentSummaryCascadeIntegrationTests` against real SQL Server 2025. **Still uncommitted.**
- **Acceptance criteria**:
  - Given a conversation with a document that has a generated summary, when `DeactivateConversationAsync` runs, then the summary row's `DateDeactivated` is set in the same transaction as the document's and the chunks'. Confirmed by `DeactivateConversation_DocumentHasASummary_DeactivatesItAlongside`.
  - Given the bulk variant, when `DeactivateConversationsBulkAsync` deactivates several conversations at once, then every summary row belonging to any of their documents is deactivated in the same batched shape. Confirmed by `DeactivateConversationsBulk_DocumentsHaveSummaries_DeactivatesEveryOne`.
  - Given a project document with a generated summary, when `DeactivateProjectAsync` or `DeactivateProjectDocumentAsync` runs, then its summary row is deactivated alongside its chunk rows. Confirmed by `DeactivateProject_DocumentHasASummary_DeactivatesItAlongside` and `DeactivateProjectDocument_DocumentHasASummary_DeactivatesItAlongside`.

### EP-4: Token accounting & telemetry — Done

#### US-401: `[enabler]` Append `ConversationUsageKinds.Summarization`

- **Story**: `[enabler]` Add `Summarization = 3` to `ConversationUsageKinds`, documented at the **run** grain from the start. Unblocks US-402.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Status**: Done. Doc comment states the grain: *"One row per run rather than per model call, matching how a chat turn is recorded... the call count itself is persisted on the summary row."* **Still uncommitted.**
- **Acceptance criteria**:
  - Given `ConversationUsageKinds`, when the value is added, then it is `= 3`, appended after `Naming = 2`.
  - Given every existing report or query that switches on `Kind`, then none of them silently mis-bucket a summarization row.

#### US-402: `[enabler]` Write one usage row per summarization *run*, billed to the requesting conversation — and only that row

- **Story**: `[enabler]` For a whole summarization run — the single-pass call, or the entire map/collapse/reduce sequence, or a digest's own reduce — write **one** `ConversationUsage` row with `Kind = Summarization`, `ConversationId` set to the requesting conversation, and `ModelId`/`ProviderId`/`DeploymentName`/prices snapshotted from the summarizer's catalog row — **and ensure the run's own model calls, when made from inside an already-tracked turn, never bleed into that turn's own usage row (§6, invariant 4).** This is a grain change from the original PRD, which specified one row per model call; the new grain matches how `ConversationUsage` already works — a chat turn with several tool iterations is one row plus `ConversationUsageTurn` children, not one row per iteration — and the double-billing clause was added only after testing proved the risk real. Unblocks US-403, US-404, US-406.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-401, US-207
- **Status**: Done. Implemented as `DocumentSummaryService.BillCoreAsync` (called from `PersistAsync` for a document summary and `BillAsync` for a digest) and `RunDetachedAsync` (the isolation fix). Tested by `DocumentSummaryServiceTests` and, for the isolation fix specifically, by `ConversationServiceTests.StreamConversationAsync_SummaryToolMakesItsOwnModelCall_DoesNotBillItToTheTurn`. **Still uncommitted.**
- **Acceptance criteria**:
  - Given a document summarized from a project, when the run completes, then the resulting `ConversationUsage` row's `ConversationId` is the **conversation that requested it** — not any id derived from the project. Confirmed by `GetOrCreateAsync_NoStoredSummary_BillsOneRowAtTheSummarizersOwnRate`.
  - Given a document already summarized by conversation A and now read as a cache hit by conversation B, when B requests it, then **zero** `ConversationUsage` rows are written for B's request. Confirmed by `GetOrCreateAsync_ProjectDocumentAlreadySummarized_IsAHitForASecondConversation` and `GetOrCreateAsync_SummaryAlreadyStored_CostsNothingAtAll`.
  - Given a document split into map units, when the run completes, then exactly **one** `Summarization`-kind row exists for it, whose `InputTokens`/`OutputTokens` equal the sum across every map, collapse, and reduce call the run made — not one row per call.
  - Given the tool's own model calls run from inside an active tracked turn, when they execute, then they run via `RunDetachedAsync` (`ExecutionContext.SuppressFlow()` + `Task.Run`, `Activity.Current` carried across by hand) so their tokens never reach `ConversationUsage.ToolInputTokens` on the turn's own row. Confirmed by `StreamConversationAsync_SummaryToolMakesItsOwnModelCall_DoesNotBillItToTheTurn`, which fails without the fix.
  - Given a digest over several documents, when it completes, then it writes exactly one further usage row for the digest's own reduce call. Confirmed by `CreateDigestAsync_Completes_BillsTheDigestButStoresNothing`.

#### US-403: `[enabler]` Increment conversation token counters in the same transaction

- **Story**: `[enabler]` Increment `Conversation.InputTokens`/`OutputTokens` by a run's tokens inside the same transaction as its usage row, and leave `ConversationUsage.ContextTokens` `null` — a summary contributes nothing to the transcript. Unblocks US-406.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-402
- **Status**: Done. Implemented inside `BillCoreAsync`'s `ExecuteUpdateAsync`. **Still uncommitted.**
- **Acceptance criteria**:
  - Given a conversation's `InputTokens`/`OutputTokens` before and after a summarization run, then the delta equals the run's own total tokens. Confirmed by `GetOrCreateAsync_NoStoredSummary_MovesTheConversationsOwnCounters`.
  - Given `ContextTokens` on a summarization usage row, then it is `null`.
  - Given a cache-hit request, then neither counter moves. Confirmed by `GetOrCreateAsync_SummaryAlreadyStored_CostsNothingAtAll`.

#### US-404: Persist run totals on the summary row

- **Story**: As an operator, I want each summary row to say what it cost to produce, so that a report can answer "how expensive was this document to summarize" without joining every `ConversationUsage` row that ever touched it.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-301, US-402
- **Status**: Done. Implemented as `DocumentSummaryService.Apply`, called from `PersistAsync`. **Still uncommitted.**
- **Acceptance criteria**:
  - Given a completed run, when the summary row is written, then its `InputTokens`/`OutputTokens`/`ModelCallCount`/`MapUnitCount`/`CollapsePasses` equal the run's own totals exactly.
  - Given a regenerated summary (a prompt-version bump, never a user request), when the row is overwritten, then its totals reflect only the new run. Confirmed by `GetOrCreateAsync_Regenerated_ReplacesTheRunTotalsRatherThanAddingToThem` and `GetOrCreateAsync_StoredSummaryFromAnOlderPromptVersion_RegeneratesInPlace`.

#### US-405: `[enabler]` Emit summarization run-level spans and metrics

- **Story**: `[enabler]` Add instruments to `ChatMetrics`'s existing `Meter` for a summarization **run's** duration, path, outcome, and map-unit count. Retargeted from the original PRD, which measured per-*call* duration; a caller waiting on the tool experiences a run, not a call.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-402
- **Status**: Done. `enterprise_gpt.document_summary.run.duration` (seconds, tagged `deployment.name`/`summary.path` ∈ {`cache-hit`, `single-pass`, `map-reduce`, `digest`}/`summary.outcome` ∈ {`success`, `failure`, `cancelled`}) and `enterprise_gpt.document_summary.run.map_units`, both recorded from `DocumentSummaryService.MeasureAsync` on the existing `ChatTelemetrySourceName` meter. **Still uncommitted.**
- **Acceptance criteria**:
  - Given the instruments, when a run completes, then `document_summary.run.duration` records its elapsed time tagged by path and outcome, and `document_summary.run.map_units` histograms its map-unit count.
  - Given `TelemetryRegistration.AddEnterpriseTelemetry`, then no new meter is registered.
  - Given any instrument's tags, then none carries a document name, a conversation id, or summary text.

#### US-406: A cache hit costs nothing, provably

- **Story**: Add the regression test that ties §1's cache-effectiveness success criterion directly to code: a repeated summary request for an already-cached, prompt-version-unchanged document writes zero `ConversationUsage` rows and leaves every conversation counter unmoved. Closes the accounting loop `US-402`/`US-403` open.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-402, US-403
- **Status**: Done. `GetOrCreateAsync_SummaryAlreadyStored_CostsNothingAtAll` and `GetOrCreateAsync_ProjectDocumentAlreadySummarized_IsAHitForASecondConversation` (`DocumentSummaryServiceTests`) both assert this directly. **Still uncommitted.**
- **Acceptance criteria**:
  - Given a request that generates a summary once, then requests it again (same conversation, same document, no prompt-version change), when row counts are compared, then `Core.ConversationUsage` gained zero rows and the conversation's `InputTokens`/`OutputTokens` are unchanged.
  - Given the same test extended to a project document read from a second conversation, when it runs, then the same zero-row, zero-increment result holds for the second conversation too — the cache is genuinely shared, not per-conversation.

### EP-5: Tool integration — Done

The seam between EP-2's engine and a real conversation. Every story here is new relative to the original PRD, which explicitly ruled this design out (§2's old non-goal, now inverted).

#### US-501: `[enabler]` Extract a shared permission-grant reader

- **Story**: `[enabler]` Extract `PermissionEndpointFilter`'s private grant-loading query into `Service/Caching/UserGrantReader.cs` (`IUserGrantReader.GetGrantsAsync(userId, ct)`, backed by the existing `IUserPermissionCache`), so a service — not just an endpoint filter — can ask "does this user hold this grant" without re-deriving the query. Unblocks US-503.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Status**: Done, and fully as originally intended. `Enterprise.Gpt.Service/Caching/UserGrantReader.cs`, registered in `Program.cs`. `PermissionEndpointFilter`'s own private `LoadGrantsAsync` is now deleted, and the filter resolves and calls `IUserGrantReader` directly — there is exactly one grant-loading query in the codebase. Covered by `tests/Enterprise.Gpt.Unit.Test/Filters/PermissionEndpointFilterTests.cs`. **Still uncommitted.**
- **Acceptance criteria**:
  - Given `IUserGrantReader.GetGrantsAsync`, when called for a user with an already-cached entry, then it costs no database query — a straight `IUserPermissionCache.GetOrLoadAsync` cache hit.
  - Given a cache miss, then the reader creates its own DI scope (via `IServiceScopeFactory`) to resolve `EnterpriseGptDbContext`, rather than closing over a scoped context that could be disposed before a concurrent waiter's continuation runs.
  - Given a user with zero grants, then the reader returns an empty set — a real answer, not a miss.
  - Given `PermissionEndpointFilter`, when it authorizes a request, then it resolves `IUserGrantReader` rather than carrying its own copy of the query.

#### US-502: `[enabler]` Build the `document_summarize` tool

- **Story**: `[enabler]` Build `Enterprise.Gpt.Service/Tool/DocumentSummaryTool.cs` following `DocumentTool.cs`'s exact structure: a static `Create` factory closing over the turn's `DocumentRetrievalScope`, an optional `documentName` parameter, refusal (not widening) on an ambiguous or unknown name via `DocumentRetrievalService.MatchByName`, a single-document shortcut that skips the digest reduce when the scope holds exactly one document, and sanitized `InvalidOperationException`s on every failure path (timeout, validation failure, unhandled exception) so nothing but a relayable sentence and a logged original ever reaches the model. Unblocks US-503.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-208, US-402 (via `IDocumentSummaryService`)
- **Status**: Done. `Enterprise.Gpt.Service/Tool/DocumentSummaryTool.cs`, `DocumentSummaryToolResult` (`Scope`, `DocumentName`, `DocumentCount`, `Summary`, `AvailableDocuments`, `Note`), the tool prompt `Enterprise.Gpt.Service/Prompts/document-summary-tool-prompt.md` plus `ConversationPrompts.BuildDocumentSummaryToolPrompt`. Tested by all 14 cases in `tests/Enterprise.Gpt.Unit.Test/Tool/DocumentSummaryToolTests.cs`, including the schema (`cancellationToken` hidden from the model), project-document sourcing, both name-resolution failure shapes, sanitized provider errors, the tool deadline, and caller cancellation. **Still uncommitted.**
- **Acceptance criteria**:
  - Given a `documentName` that matches exactly one document in scope, when the tool runs, then only that document is summarized and the result's `Scope` is `"document"`. Confirmed by `InvokeAsync_NameMatchesOneDocument_SummarizesThatDocumentOnly` and `InvokeAsync_ProjectDocument_PassesItsOwnSourceThrough`.
  - Given a `documentName` that matches zero or more than one document, when the tool runs, then it makes **no** model call and returns `AvailableDocuments` plus a `Note`. Confirmed by `InvokeAsync_NameMatchesNothing_RefusesWithoutSpendingAnything` and `InvokeAsync_NameMatchesSeveralDocuments_RefusesRatherThanWidening`.
  - Given no `documentName` and more than one document in scope, when the tool runs, then it produces a digest. Confirmed by `InvokeAsync_NameOmitted_DigestsEveryDocumentInScope`.
  - Given no `documentName` and exactly one document in scope, when the tool runs, then that single document is summarized directly. Confirmed by `InvokeAsync_NameOmittedAndOneDocumentInScope_SummarizesItDirectly`.
  - Given any terminal failure, when it is caught, then the model receives a sanitized, actionable sentence and the real exception is logged, never relayed. Confirmed by `InvokeAsync_RunRefused_RelaysTheSanitizedReasonToTheModel` and `InvokeAsync_RunFails_ReplacesTheMessageWithSomethingSafeToShowTheModel`.

#### US-503: Attach the tool to a turn

- **Story**: As a chat user, I want the assistant to be able to summarize my documents without me asking for it through anything but plain conversation, so that summarization feels like part of the assistant rather than a separate feature.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-501, US-502
- **Status**: Done. Implemented in `ConversationService.CreateChatOptionsAsync`: gated on `Summarization:Enabled`, a non-empty document scope, `model.IsToolEnabled`, and the caller's `Upload File` grant (via `IUserGrantReader`); guarded against a same-named MCP tool. Tested by five new `ConversationServiceTests` cases covering every gate combination. **Still uncommitted.**
- **Acceptance criteria**:
  - Given `Summarization:Enabled = false`, when a turn builds its chat options, then the tool is never attached. Confirmed by `StreamConversationAsync_SummarizationDisabled_AttachesOnlyRetrieval`.
  - Given the flag on but the conversation's document scope empty, then the tool is not attached. Confirmed by `StreamConversationAsync_SummarizationEnabledWithNoDocuments_AttachesNothing`.
  - Given the flag on, documents present, a tool-enabled model, but the caller lacks the `Upload File` grant, then the tool is not attached. Confirmed by `StreamConversationAsync_CallerWithoutTheUploadGrant_AttachesOnlyRetrieval`.
  - Given an MCP tool selected for the turn that is already named `document_summarize`, then the summarization tool stands down for that turn. Confirmed by the MCP-collision case added alongside `document_search`'s own equivalent.
  - Given the tool is attached, then `ConversationPrompts.BuildDocumentSummaryToolPrompt` adds instructions telling the model when to prefer it over `document_search`. Confirmed by `StreamConversationAsync_SummarizationEnabled_TellsTheModelWhenToPreferItOverSearch` and, for attachment itself, `StreamConversationAsync_SummarizationEnabled_AttachesTheSummaryToolBesideRetrieval`.

#### US-504: Digest across every document when no name is given

- **Story**: As a chat user, I want to ask for an overview of everything I've attached without naming each file, so that I don't have to summarize documents one at a time.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-502
- **Status**: Done. Implemented as `DocumentSummaryService.CreateDigestAsync`, called from the tool's `SummarizeAllAsync` path. **Still uncommitted.**
- **Acceptance criteria**:
  - Given a conversation with several documents in scope, some of which already have summaries, when the digest runs, then only the missing ones are generated before the reduce. Confirmed by `CreateDigestAsync_SomeDocumentsAlreadySummarized_OnlySummarizesTheRest`.
  - Given a scope whose document count exceeds `MaxDigestDocuments` (default 25), when the digest is requested, then it refuses with a message naming the limit, before making any model call. Confirmed by `CreateDigestAsync_ScopeAboveTheConfiguredMaximum_RefusesNamingTheLimit`.
  - Given the digest, when it completes, then it is **never persisted**. Confirmed by `CreateDigestAsync_Completes_BillsTheDigestButStoresNothing`.
  - Given an empty scope, then the digest refuses with a plain "nothing to summarize" message. Confirmed by `CreateDigestAsync_NoDocumentsInScope_RefusesRatherThanCallingTheSummarizer`.

#### US-505: `[enabler]` Bound the whole tool invocation with its own deadline

- **Story**: `[enabler]` Wrap the tool's whole invocation — not any one model call inside it — in a deadline (`SummarizationOptions.ToolTimeoutSeconds`, default 300, linked to the turn's own cancellation token), distinct from `CallTimeoutSeconds`'s per-call bound inside the engine. Unblocks nothing further; guards every other EP-5 story's worst case.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-502
- **Status**: Done. Implemented via `CancellationTokenSource.CreateLinkedTokenSource` inside `DocumentSummaryTool`'s invoker. **Still uncommitted.**
- **Acceptance criteria**:
  - Given a run that would exceed `ToolTimeoutSeconds`, when the deadline fires, then the tool relays a sanitized message explaining the summary took too long. Confirmed by `InvokeAsync_ExceedsItsOwnDeadline_TellsTheModelRatherThanFailingTheTurn`.
  - Given the turn itself is cancelled before the tool's own deadline fires, then cancellation propagates as cancellation. Confirmed by `InvokeAsync_TurnCancelled_LetsCancellationStayCancellation`.
  - Given a run that completes well within the deadline, then this story changes nothing observable.

### EP-6: Governance & rollout

#### US-601: Feature-flag document summarization with a no-redeploy rollback

- **Story**: As an operator, I want to switch document summarization off without a deployment, so that an unexpected cost or a misbehaving summarizer deployment is a configuration change rather than an incident. Retargeted: the flag now gates tool attachment (§EP-5), not an HTTP route.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-105, US-503
- **Status**: Done. `SummarizationOptions.Enabled` (default `false`) is wired into `CreateChatOptionsAsync`'s attachment gate, and `SummarizationOptionsTests.Bind_TheShippedConfiguration_HasSummarizationDisabled`/`Bind_OnlyTheModelId_LeavesSummarizationDisabled` assert the shipped default; `StreamConversationAsync_SummarizationDisabled_AttachesOnlyRetrieval` asserts the attachment behavior directly. **Still uncommitted.**
- **Acceptance criteria**:
  - Given the flag off, when any turn builds its chat options, then the summarization tool is never attached. Confirmed.
  - Given the flag off, when a document that already has a generated summary is inspected directly (SQL, not the tool), then its summary row is untouched — a rollback does not destroy summaries already generated.
  - Given a fresh environment with no explicit setting, then the flag defaults to **off**, including in development. Confirmed by `SummarizationOptionsTests`.

#### US-602: Bound how often one user or conversation may invoke summarization

- **Story**: As an operator, I want a ceiling on how many summarization requests one user or one conversation can make, so that a request pattern — not a single pathological document, which `MaxMapUnits` already bounds unconditionally (EP-2) — cannot become a surprise cost. Narrowed from the original PRD: the map-unit ceiling and the digest document-count cap are no longer this story's concern — both are already enforced unconditionally, inside the engine and `DocumentSummaryService` respectively (§6). This story is the one piece of the original governance bound genuinely not yet built.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-503
- **Status**: Not started. No rate-limiting mechanism of any kind exists yet for the tool — this remains true after the rest of this feature was tested; nothing in that pass touched it.
- **Acceptance criteria**:
  - Given a configured per-user or per-conversation daily request-rate ceiling, when it is exceeded, then the tool refuses with a plain explanation naming the limit, relayed to the model as a sanitized sentence — not a failed turn.
  - Given no ceiling is configured, then the feature behaves exactly as it would without this story — the bound is opt-in, unlike `MaxMapUnits`/`MaxDigestDocuments`.
  - Given the ceiling is exceeded mid-digest (several documents still need generating), then the whole digest refuses before any further model call, rather than partially completing and then failing.

#### US-603: Cap the digest's maximum document count

- **Story**: As an operator, I want a ceiling on how many documents a single digest reduces over, so that an unusually large scope cannot turn one request into an unbounded number of runs.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-504
- **Status**: Done. `SummarizationOptions.MaxDigestDocuments` (default 25) is enforced, unconditionally, inside `DocumentSummaryService.CreateDigestAsync` (§6), and asserted by `CreateDigestAsync_ScopeAboveTheConfiguredMaximum_RefusesNamingTheLimit`. **Still uncommitted.**
- **Acceptance criteria**:
  - Given a scope exceeding `MaxDigestDocuments`, when the digest is requested, then it refuses with a message naming the limit and suggesting individual documents instead, before any model call. Confirmed.
  - Given a scope at or under the maximum, then this story changes nothing about `US-504`'s existing behavior.

**Dropped from the original PRD**: `US-604` (durable resume for a restarted background-job run). A background job's mid-run state was the only thing that story protected; there is no background job left to protect. Nothing in this document names `US-604` in a `Depends on`, so dropping it changes no other story's scope.

## 8. Milestones & rollout

**Phases**, derived from the epic dependency graph and the actual state of the working tree.

| Phase | Contents | Status |
| --- | --- | --- |
| **Wave 1 — catalog & configuration** | EP-1 in full | Done |
| **Wave 2 — the engine** | EP-2 in full | Done |
| **Wave 3 — persistence & accounting** | EP-3 (`US-301`, `US-302`) and EP-4 (`US-401`–`US-406`) | Done, implemented and tested. Still uncommitted. |
| **Wave 4 — tool integration** | EP-5 in full (`US-501`–`US-505`) | Done, implemented and tested, including the §6 invariant-4 fix and its regression guard. Still uncommitted. |
| **Wave 5 — hardening before switch-on** | EP-6 (`US-601`, `US-603` done; `US-602` not started) | The flag and the digest cap are implemented, wired, and tested; the per-user/per-conversation rate bound is the one piece of this feature with no code at all yet. |

**Critical path to production.** With every epic but `US-602` now implemented and tested, what remains is: (1) code review and commit the (still uncommitted) implementation and its tests; (2) build `US-602`'s rate bound, the one genuinely unbuilt piece of this feature; (3) exercise the `US-601` rollback path once in a real environment before production enablement. The `ChatUsageScope` double-billing risk that used to head this list is resolved and regression-tested (§6, invariant 4) — it is no longer a gate on committing, only a reason this feature was worth writing careful tests for before trusting its cost-containment claims.

**Risks & mitigations.**

| Risk | Mitigation |
| --- | --- |
| A tracked chat client call made from inside an active tool invocation overwrote the outer turn's own usage report (§6 invariant 4) | **Resolved.** Proven real by test (900,000 tokens landing on the wrong row), fixed by `DocumentSummaryService.RunDetachedAsync` (execution-context detachment with `Activity.Current` carried across by hand), and regression-guarded by `ConversationServiceTests.StreamConversationAsync_SummaryToolMakesItsOwnModelCall_DoesNotBillItToTheTurn`, which fails without the fix. |
| `Model.MaxOutputTokens` reaching the wire for the first time, with no live-deployment verification that Azure OpenAI over the Responses API honours it as a hard cap | **Accepted, not eliminated** (carried over from the original PRD, still unverified against a live deployment). An uncapped or mis-capped map phase is unbounded output billed at the summarizer's rate on every call a large document produces. |
| A summary table that does not join the four existing soft-delete cascades leaves a readable summary of a deleted document | **Resolved and tested.** All four cascades are extended, and each has its own passing integration test against real SQL Server. |
| Working code gets committed under time pressure without review, even though it is now tested | The build-order companion tracks "uncommitted" as its own fact, separate from "tested," precisely so this cannot silently be skipped |
| A future table with a required FK to `Core.Ref.Model` repeats the class-ordering test failure the summary tables originally caused | Documented in §6's testing-infrastructure note and fixed via a reusable `ClearDocumentSummariesAsync` helper any future table should extend |

**Rollout & rollback.** `Summarization:Enabled` governs the whole feature end to end — the tool's attachment to any turn — defaulting **off** everywhere, including development. With it off, the model cannot discover or call the tool, and `US-601` asserts that with a regression test. Rollback is a configuration change with no redeploy; a summary already generated before a rollback is untouched in the database and is served immediately, at no cost, the next time the flag is on and the tool is called for that document. `Summarization:ModelId`'s own startup validation runs regardless of the flag.

## 9. Assumptions & open questions

**What changed in this revision.**

- **Resolved by the pivot, and removed from this section**: whether `US-604` (durable resume for a restarted background-job run) ships — there is no job to resume. Whether a summary should render as plain text or through the markdown pipeline — there is no client surface to render it on. Whether the frontend's bundle budget absorbs this feature's marginal cost — there is no frontend cost.
- **Resolved by testing, and removed from this section**: the `ChatUsageScope` double-completion risk (§6, invariant 4) — proven real, fixed, regression-tested. The `PermissionEndpointFilter`/`UserGrantReader` duplication (§3, §6) — the filter now calls the shared reader; there is exactly one grant-loading query.
- **Still live, carried over unchanged**: the `MaxOutputTokens`-honouring assumption and the whitespace-lossy chunk reassembly are both still true of EP-2's engine, which the pivot does not touch.

**Assumptions.** Each is a guess a reviewer can veto.

- **Azure OpenAI over the Responses API is assumed to honour `ChatOptions.MaxOutputTokens` as a hard cap, not merely advisory.** This is the first call site in the codebase to set that option for any provider (§6, invariant 2), and nothing in this cycle verifies it against a live deployment. If the assumption is wrong, a map phase's per-call output is effectively unbounded, billed at the summarizer's rate on every call a large document produces (§8 risks, accepted). A reviewer who wants this proven before production traffic depends on it should say so.
- **Chunk reassembly is whitespace-lossy, and this is accepted.** `TokenTextChunker` joins its units with `"\n"`, so reassembling chunk text at summarization time can collapse blank lines and turn inter-sentence spacing into newlines — all the words survive, the original formatting does not. Re-extracting from the blob was rejected (re-bills Azure Document Intelligence); persisting a second, unchunked copy at ingest time was rejected (a duplicated storage cost nobody asked for).
- **No regeneration flag exists, and a document's text never changing after ingestion is why that is safe.** The only thing that can stale a cached summary is a prompt-template version bump, which self-invalidates automatically (§2, §6). A reviewer who wants an explicit administrator-triggered regeneration path should say so — today, the only way to force one is to bump `SummarizationPrompts.PromptVersion`, which invalidates every cached summary in the system, not just one.
- **Project documents are visible to every member of a project**, so a shared project-document summary is readable by anyone who can already read the document itself.
- **The delimiter-based prompt-injection defense (`US-208`) is treated as sufficient for v1.** No adversarial red-team pass against the summarization prompts specifically is scoped in this document.
- **Concurrent identical requests for the same not-yet-summarized document are not deduplicated.** The unique filtered index still guarantees exactly one final row, but a losing racer's work — and its billed tokens — is wasted.
- **Detaching the engine's calls from the ambient usage tracker (§6, invariant 4) is sufficient, and no further isolation is needed.** The fix addresses the proven failure mode (nested `ChatUsageScope` completion); a reviewer who suspects a related but distinct leak elsewhere in the tracked pipeline should say so, since the regression test covers the specific mechanism proven, not every conceivable one.
- **Every numeric default shipped in `SummarizationOptions` and `appsettings.json` — `SafetyFraction` 0.85, `MapUnitFraction` 0.90, `MaxConcurrency` 4, `CallTimeoutSeconds` 180, `MaxCollapsePasses` 5, `MaxCallRetries` 2, `ToolTimeoutSeconds` 300, `MaxMapUnits` 40, `MaxDigestDocuments` 25 — matches exactly what the original PRD's §9 proposed for veto, and no reviewer vetoed any of them before they were coded.** They remain revisitable once `US-405`'s run-level telemetry shows real call sizes, durations, and outcomes in production; none is derived from a measured pre-production baseline.

**Open questions.**

- **The per-user/per-conversation summarization request-rate bound's exact numbers (`US-602`).** No default is proposed yet, unlike every other numeric knob in this feature — *product owner with the operator, before `US-602` starts*.
- **Whether an administrator should be able to see which model is currently configured as the summarizer from the admin UI**, or whether reading `Summarization:ModelId` from configuration is sufficient. No story in this document adds such a read-only display — *product owner, before `US-104`-adjacent work resumes, since it is the natural place to add one if the answer is yes*.
- **When the nine-plus implementation and test files this feature comprises get committed and code-reviewed.** Everything is tested; nothing is committed. This is now the single biggest gap between "this PRD says Done" and "this feature has shipped" — *backend engineer / reviewer, immediately*.
