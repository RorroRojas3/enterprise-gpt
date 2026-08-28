# PRD: File Agent

## 1. Overview

**Problem.** Enterprise GPT can **read** a document and cannot **produce** one. `Enterprise.Gpt.Service/DocumentService.cs` extracts, chunks, embeds and vector-searches an uploaded file, and `Tool/DocumentTool.cs` hands passages to the model — but there is no path in the other direction. A user who asks for "that as a spreadsheet" gets a Markdown table pasted into a chat bubble, and every downstream step of their real task happens outside this platform. The rails for the fix are already in the tree, unused: `Program.cs`'s `ConfigureToolTracking` calls `options.UseAgentToolClassification()` for all four chat providers (`Program.cs:117-121`), `Andes.Extensions.AI.Agent` 0.8.0 is already referenced by `Enterprise.Gpt.Api` (bringing `Microsoft.Agents.AI` 1.17.0 transitively), `ConversationToolKinds.Agent = 3` exists and `UsageReportTranslator.MapKind` already maps it, and two code comments are addressed to a feature that does not exist yet — `UsageReportTranslator.cs:260-264` leaves `ModelId`/`DeploymentName` null on every tool-call row because "only an agent this application configured could answer, and none exists yet," and `ConversationUsageToolCallConfiguration.cs` omits its `ModelId` index with "add it with the first agent that reports a model." Two empty committed folders scaffold the destination: `Enterprise.Gpt.Api/Agents/` and `Enterprise.Gpt.Service/Agents/Documents/Skills/`.

**Solution.** A specialised **File Agent**, built on **Microsoft Agent Framework** (`Microsoft.Agents.AI`), exposed to the main assistant's `IChatClient` as a single tracked `AIFunction`. It creates, edits, compares and converts `docx`, `pdf`, `xlsx`, `csv`, `pptx`, `md` and `txt` files by writing and running Python in a per-turn **`HostedCodeInterpreterTool`** sandbox, riding the **Azure OpenAI Responses route** already registered under `ChatClientKeys.AzureOpenAI` (`Program.cs:157-191`) — the only one of the four providers structurally capable of carrying a hosted tool. It loads **Agent Skills**, one per format family and one per verb, through progressive disclosure so its own context stays lean regardless of how many formats the platform supports. Every artifact is re-opened in a second, deterministic, model-free sandbox pass and asserted against the requested shape before it is ever handed back — no reviewer LLM, because a model cannot open a binary `.docx` and Code Interpreter is already an iterative write→run→see-traceback→retry loop (product-owner ruling, 2026-08-14, still standing). A finished file lands as a `ConversationDocument` of a new `Generated` type — this PRD's own contract, since no image tool exists to have built it first — in the dedicated `generated-documents` blob container, never enters the retrieval index, and reaches the user as a **document chip** that mints a fresh, short-lived signed link on click. File generation is gated on a new, dedicated permission that administrators do not hold implicitly.

**Success criteria.**

- **Artifact validity**: ≥ 90% of completed File Agent runs return an artifact that passes deterministic verification on the first attempt, measured by a `file_agent.verification` metric tagged `outcome="passed"` over a rolling 7-day window, with 0 runs that report success while producing no file.
- **Retrieval isolation**: 0 rows in `Core.ConversationDocumentChunk` for any `ConversationDocument` whose `Type` is `Generated`, and 0 citations in any answer name one — measured by a scheduled query and pinned by an integration test.
- **Token attribution completeness**: 100% of turns that invoke the File Agent write a `Core.ConversationUsageToolCall` row with `Kind = 3` (`Agent`), a non-null `ModelId`, and a subtree whose totals equal what the turn actually spent — measured by a query over that table and by a regression test asserting the enclosing turn's own row is never inflated or deflated by the agent's nested calls.
- **Conversion fidelity honesty**: 100% of served conversions whose confirmed tier is `◐` (structural) state in the answer what was lost, 100% of pairs confirmed `refused` are refused by name before any sandbox run starts, and 0 conversions silently degrade to a text-only answer — measured by an acceptance test enumerating every cell in `FileAgentOptions`'s published matrix. A pair the sandbox can genuinely perform being refused counts as a failure of this criterion, not a success.
- **Delivery**: ≥ 99% of generated-file chip clicks resolve to a `200` from the existing document-download route within the link's configured lifetime — measured by that route's success rate filtered to `Generated` documents.

Priya asks the assistant to turn her uploaded meeting notes into a one-page summary deck. The assistant calls one tool; an activity card labelled "File Agent" appears with an **Agent** kind badge, and beneath it nested child cards show Python being written and run, then a second pass re-opening the finished `.pptx` and counting its slides. When the card completes, a document chip — distinct from an upload chip by its glyph and its accessible name — sits under the answer. Priya clicks it; the client asks the download route for a link at that moment and the browser saves `meeting-summary.pptx`. Nothing about the file ever re-enters the model's context, nothing about it is retrievable or citable, and the whole turn — the assistant's own tokens plus the agent's nested tokens plus the sandbox's billed seconds — is one row and its children in the usage audit. A week later she asks for the same notes "but as a Word doc, formatted nicely" — the agent reads the deck it made, this time producing a `.docx`, again as a **new** `Generated` document, the original untouched.

## 2. Goals & non-goals

**Goals.**

- Let a user get a real `docx`, `pdf`, `xlsx`, `csv`, `pptx`, `md` or `txt` file out of a conversation — created, edited, compared, or converted from another of those formats — without leaving the platform.
- Make every artifact trustworthy by construction: re-open it in the sandbox and assert it parses and matches the requested shape before it is ever returned.
- Attribute every token the File Agent spends, and its own nested calls, to the turn that caused it — filling the `ModelId`/`DeploymentName` nulls `UsageReportTranslator` currently writes and adding the index its configuration currently defers.
- Build the generated-file contract this feature needs itself, once: an `Uploaded`/`Generated` document-type discriminator, a separate `generated-documents` blob container, a persist-without-ingesting write path, download-route format coverage, a message-level file reference, and a retrieval exclusion — because no image tool exists in this codebase to have built it first (decision 2).
- Use Microsoft Agent Framework's Agent Skills to keep the agent's own context lean: advertise only the skills relevant to a run's source and target formats, and disclose a skill's full recipe only when it is actually loaded.
- Run Python only inside the hosted sandbox — never as a subprocess on the API host — and name real, currently-available Python libraries in every skill rather than aspirational ones.
- Give a generated **document** its own delivery surface: a chip on the assistant's message that downloads on click and survives a reload.
- Reuse the streaming contract exactly as it stands: the File Agent is a `depth: 1` activity and its internal steps are `depth: 2` children, folded by the reducer the client already vendors.

**Non-goals.**

- **Image generation or embedding, in any form.** Out of scope entirely (decision 2) — no `generate_image` call, no dependency on any image PRD, no image epic. A document the agent produces may contain charts it draws with its own plotting libraries inside the sandbox, but nothing here generates or fetches a photographic or illustrative image.
- **The Foundry `AIProjectClient` preview SDK, `Azure.AI.Projects`, or a toolbox-hosted Code Interpreter.** Decision 1 rides the existing Azure OpenAI Responses registration; no new SDK, no new client type, and no toolbox — a toolbox-hosted Code Interpreter shares one container context across every user in a project, which is disqualifying for a multi-tenant deployment.
- **A planner/executor/reviewer topology, or a reviewer LLM.** Settled by the product owner on 2026-08-14 and restated here rather than re-opened: Code Interpreter's own write→run→see-traceback→retry loop already is the executor, and no model can inspect a binary `.docx`. The reliable check is deterministic Python with no model call.
- **Background or asynchronous execution.** The agent runs inside the streaming turn, cancelled with it. The document-ingestion pipeline's queue-and-poll design is not reused.
- **`.xlsm` or any macro-enabled format**, and any format outside the seven named. `Xlsm` already exists in `FileExtensions` for uploads but is not a File Agent input or output.
- **Ingesting an uploaded `.xlsx`/`.csv` — a hard predecessor, not part of this PRD.** Reading a spreadsheet a user brings into a conversation (local `.xlsx`/`.csv` extraction, header-aware row-window chunking, the sheet-aware citation, the `sheet_query` deterministic lookup) is specified in full in `docs/prd/sheet-ingestion/sheet-ingestion.md`, which ships **first** (§9). This PRD's edit/compare/convert verbs (EP-4) consume whatever spreadsheet document that PRD's pipeline has already ingested — the same `DocumentRetrievalScope`/`MatchByName` resolution every other source format already goes through — and change nothing about how it got there.
- **Adding `Andes.Extensions.AI.Agent` or `Microsoft.Agents.AI` to `Enterprise.Gpt.Service`.** `Enterprise.Gpt.Service.csproj` carries an explicit comment recording the exclusion; it stands. The agent is composed in `Enterprise.Gpt.Api`.
- **New `AssistantUiEvent` kinds, or any change to the vendored `Andes.Extensions.AI.UI` contract.** `ActivityStarted`/`ActivityProgress`/`ActivityCompleted`/`ActivityFailed` with `scopeId`/`parentScopeId`/`displayName`/`toolKind`/`depth` already model arbitrary nesting; adding a kind would be a breaking third-party-package change and a `npm run check:contract` drift failure.
- **New RFC 9457 problem type URIs.** Every failure this feature introduces happens mid-stream and surfaces as a failed activity card; problem types are opaque identifiers clients match verbatim, and this PRD mints none.
- **Generated documents on projects.** The type discriminator goes on `ConversationDocument`, not `BaseDocument`, so `ProjectDocument` is untouched; a project-scoped generated file is deliberately deferred.
- **Templates, corporate branding, or document themes.** The sandbox has no outbound network access and cannot fetch a font, a logo, or a template from anywhere.
- **Opening or editing a generated document in the browser.** The deliverable is a downloaded file, not an in-app editor.
- **A second retrieval corpus, embeddings, or citations over generated content.** `document_search` and `Tool/DocumentRetrievalSql.cs` see nothing this PRD adds.
- **Documentation stories.** Docs under `docs/` and the `CHANGELOG.md` entry are the SE Technical Writer flow's job, after implementation.

## 3. Users & access

**Personas.**

- **Chat user**: a signed-in employee holding a conversation. Asks for a file in natural language; never selects the File Agent explicitly, because it is an implicit tool the assistant chooses the way it already chooses document retrieval.
- **Administrator**: holds `PermissionIds.Administrator`. Grants the new file-generation permission and reads what the feature costs; is **not** implicitly granted the capability itself.
- **Operator**: runs the deployment. Holds the feature flag and the per-user/conversation ceiling, and owns the decision to switch the feature off without a redeploy.
- **Backend engineer**: owns EP-0 through EP-5 and EP-7, working in `enterprise-gpt-api/` under `.claude/rules/csharp.md` and `aspnet-rest-apis.md`.
- **Frontend engineer**: owns EP-6, working in `enterprise-gpt-ui/` under the `ngrx-signal-store` and `angular-developer` skills.

**Role-based access.**

- **Anonymous**: no access. Every route this feature touches is inside a group already carrying `.RequireAuthorization()`.
- **Chat user without the grant**: conversations behave exactly as they do today. The File Agent tool is **not attached** to their turns and the assistant is told nothing about it — the same shape as the `Upload File` affordance being absent, not a 403 on a capability the caller never named.
- **Chat user with the grant**: the tool is attached whenever the feature flag is on and the selected model supports tools. They can download any generated file in a conversation they own, through the same route and the same ownership rule that already serves uploaded documents — ownership is read off the **parent conversation**, and a miss is a 404, never a 403 (`Enterprise.Gpt.Service/DocumentService.cs:427-443`).
- **Administrator**: grants and revokes the permission through the existing `api/permissions` surface. Administrators are **not** implicitly granted it, matching the codebase's standing rule (`PermissionEndpointFilter.Require`, admin routes gate on `PermissionIds.Administrator` alone).

The grant is resolved through the singleton `IUserPermissionCache`, the same path `PermissionEndpointFilter` and `ConversationService`'s summarization gate already use via `IUserGrantReader`, and is invalidated per user by `PermissionService` exactly as every other grant already is.

## 4. Functional requirements

| ID | Requirement | Priority | Epic(s) |
| --- | --- | --- | --- |
| FR-1 | Code Interpreter is proven reachable through the Responses-route Azure OpenAI client, in this tenant/region, before any code depends on it | P0 | EP-0 |
| FR-2 | The sandbox image's actually-installed Python packages are inventoried and recorded, not assumed from documentation | P0 | EP-0 |
| FR-3 | The supported and refused format-conversion pairs are established against real sandbox runs and published as a matrix | P0 | EP-0 |
| FR-4 | `ConversationDocument` carries an `Uploaded`/`Generated` discriminator; every existing row backfills to `Uploaded` | P0 | EP-1 |
| FR-5 | A generated file is written to the dedicated `generated-documents` blob container with zero chunk rows, and rejected before being persisted if it exceeds `Documents:MaxFileSizeBytes` | P0 | EP-1 |
| FR-6 | A generated document is never extracted, chunked, embedded, retrieved, ranked or cited | P0 | EP-1 |
| FR-7 | A generated document downloads through the existing download route in its own format | P1 | EP-1 |
| FR-8 | A transcript message carries a structured, URL-free reference to every generated file it introduced | P0 | EP-1 |
| FR-9 | `HostedCodeInterpreterTool` is attached only to the Azure OpenAI (Responses-route) keyed client, never Azure AI Foundry, Bedrock or Anthropic | P0 | EP-2 |
| FR-10 | Every sandbox input is uploaded by the API to the Files API and supplied as `HostedFileContent` on `HostedCodeInterpreterTool.Inputs`; the sandbox itself makes no outbound network call | P0 | EP-2 |
| FR-11 | Generated artifacts are read back off every channel a response can carry one on, and downloaded container-scoped; raw `DataContent` inputs are asserted by a test to be silently dropped rather than assumed away | P1 | EP-2 |
| FR-12 | A run that returns text but no artifact is classified as a distinct "no file produced" failure, never a completed turn | P1 | EP-2 |
| FR-13 | A run is bounded in wall-clock time and total artifact count, independent of the outer turn's own function-invocation bounds | P0 | EP-2 |
| FR-14 | A run is cancelled with the turn; cancellation never leaves an orphaned document row or an orphaned blob | P0 | EP-2 |
| FR-15 | The agent is composed in `Enterprise.Gpt.Api` and reached from `Enterprise.Gpt.Service` through an abstraction; `Enterprise.Gpt.Service` gains no `Microsoft.Agents.AI` reference | P0 | EP-3 |
| FR-16 | The agent is attached via `AIAgent.WithTracking(...)` with `trackUsage` chosen explicitly; a bare `AsAIFunction()` never appears outside that wrapper | P0 | EP-3 |
| FR-17 | The agent carries a non-empty `Name`, and its tool name does not sanitize to the leading `{token}_` prefix of any catalog MCP server name today | P0 | EP-3 |
| FR-18 | Format-targeted Agent Skills are attached via `AgentSkillsProvider` and filtered, before advertisement, to the run's source and target formats | P0 | EP-3 |
| FR-19 | All three skill tools are auto-approved for read-only use so a headless turn never stalls on an unanswered approval request | P0 | EP-3 |
| FR-20 | The only `AgentFileSkillScriptRunner` registered anywhere in the solution refuses to execute; a skill's own scripts, if referenced, run only inside the sandbox | P0 | EP-3 |
| FR-21 | Skill content ships as `SKILL.md`/`references/*.md` files under `Enterprise.Gpt.Service/Agents/Documents/Skills/`, copied to the output directory | P1 | EP-3 |
| FR-22 | The agent's model is a dedicated, pinned `Core.Ref.Model` row on the Azure OpenAI provider, validated at startup regardless of the feature flag | P0 | EP-3 |
| FR-23 | The agent creates a document in `docx`, `xlsx`, `pptx`, `csv`, `md` or `txt` from a natural-language request | P0 | EP-4 |
| FR-24 | The agent creates a `pdf`, and the sandbox's own font/fidelity limitation is stated in the answer | P1 | EP-4 |
| FR-25 | Every artifact is re-opened in a second, deterministic, model-free sandbox pass and asserted against the requested shape before it is returned | P0 | EP-4 |
| FR-26 | The agent edits an existing conversation document, always producing a **new** `Generated` document | P1 | EP-4 |
| FR-27 | The agent compares two documents and reports differences; a comparison document is produced only when asked | P1 | EP-4 |
| FR-28 | A conversion is served at the fidelity tier the confirmed matrix (FR-3) records, with a `◐` structural result declaring what was lost; only a pair confirmed `refused` — and an ambiguous or unauthorized source — is refused by name before a sandbox run starts | P0 | EP-4 |
| FR-29 | A model without tool support stands the File Agent down with a warning; a mid-run failure surfaces as a failed activity, never a corrupted stream | P0 | EP-4 |
| FR-30 | Stopping a turn abandons the sandbox call, releases the conversation lock, and leaves no partial document | P1 | EP-4 |
| FR-31 | A File Agent tool-call row records a non-null `ModelId`/`DeploymentName`; every other tool kind continues to record null | P0 | EP-5 |
| FR-32 | The deferred `(ModelId, DateCreated)` index on `ConversationUsageToolCall` is added | P1 | EP-5 |
| FR-33 | The turn's own usage totals are unaffected by the agent's nested calls — no double count, no under-count | P0 | EP-5 |
| FR-34 | Sandbox session time is measured and attributed as its own metric, distinct from token counts | P1 | EP-5 |
| FR-35 | File Agent and sandbox activity emit spans and metrics through the existing OpenTelemetry registration, with no prompt content, file content or file name in any tag | P1 | EP-5 |
| FR-36 | File generation is bounded by a configurable per-user and/or per-conversation ceiling covering both runs and sandbox seconds | P1 | EP-5 |
| FR-37 | A generated document renders as a distinct chip variant, visually and semantically distinguishable from an uploaded attachment | P0 | EP-6 |
| FR-38 | Clicking the chip downloads the file via the existing mint-on-click path, with no prefetch | P0 | EP-6 |
| FR-39 | A generated document's chip survives a reload, replayed from the persisted message reference; the activity tree is not replayed | P1 | EP-6 |
| FR-40 | The File Agent's nested activity renders through the existing activity-card nesting, with no new `AssistantUiEvent` kind | P1 | EP-6 |
| FR-41 | Every generated-file surface has distinct loading, failure and unavailable states | P1 | EP-6 |
| FR-42 | Every generated-file surface is operable by keyboard and announced to assistive technology | P0 | EP-6 |
| FR-43 | The feature's added components ride the lazy `chat` chunk, and the initial bundle is re-baselined | P1 | EP-6 |
| FR-44 | File generation is gated by a new, dedicated permission, resolved through `IUserPermissionCache`; administrators are not implicitly granted it | P0 | EP-7 |
| FR-45 | The feature sits behind a configuration flag with a documented, exercised rollback requiring no redeploy | P0 | EP-7 |

**Retired by this revision**: FR-46 and FR-47 (accepting `.xlsx`/`.csv` uploads and chunking them header-aware). Both moved to `docs/prd/sheet-ingestion/sheet-ingestion.md` (its FR-1/FR-2 and FR-6/FR-7 respectively) when that PRD was carved out as this feature's predecessor — see §2 and §9. The numbers are retired, not reused, so a later revision does not silently collide with a requirement this document no longer owns.

## 5. User experience

**Entry points & first-time flow.** There is no new screen, menu item, or picker. A user with the grant simply asks — "make me a spreadsheet of these numbers," "turn this into a deck," "give me this as a PDF" — and the assistant decides to call the File Agent, exactly as it already decides to call document retrieval or summarization. A user without the grant sees no difference from today, because the tool is not attached and nothing renders an unavailable panel.

**Core experience.**

1. The user sends a prompt in the existing composer. Nothing about the composer changes.
2. The assistant calls the File Agent. An activity card appears in the timeline at `depth: 1`, labelled with the agent's `displayName` and carrying an **Agent** kind badge — rendered as two separate elements, per the streaming contract's rule that a pre-composed label like "Calling File Agent" is a defect.
3. Child cards appear nested at `depth: 2` as the agent works: a skill being loaded, Python being written and run, and — after the first artifact exists — a second pass re-opening it and checking its shape. Sub-status lines come from `ChatProgress.Report(...)`, the only channel that reaches the feed, and never carry generated source code or file content.
4. The agent card completes with a duration. The answer text continues streaming around it.
5. A **document chip** renders under the assistant's message: file name, extension glyph, size. It is a variant of the existing `shared/chip/attachment-chip` component, distinguished from an upload chip by a leading glyph and its accessible name ("Generated file, {name}") so a reader can tell a file the assistant made from one they gave it. It renders only after verification has passed, so a failed run leaves the answer text and no chip at all.
6. Clicking the chip calls the existing document-download route at that moment, and `DocumentDownloadStore` hands the returned URL to a detached anchor — the shipped mint-on-click path, unchanged.
7. Reopening the conversation later replays the transcript; the chip renders again from the reference persisted on the message, and the click mints a **fresh** link. The activity tree is not replayed — only the answer text and the file reference are transcribed, matching the existing rule that reopening a conversation replays the answer and never the work that produced it.

**Edge cases & UI states.**

- **Working**: the agent card shows a running state and its nested children as they arrive. Between the tool call and the first child there may be several seconds of silence, which the card's own running state fills — no fabricated sub-status covers the gap.
- **Verification failed**: the agent retried within its bound and still could not produce a matching artifact. The card renders failed with the reason, the assistant explains it in prose, and **no chip renders** — a failed run must never leave a chip pointing at a file that does not parse.
- **No file produced**: the run returned text only. Rendered as a failed activity naming that outcome specifically, not as a completed turn.
- **Unsupported conversion**: the agent refuses before running any code, naming the pair and, when the matrix supports a related pair, suggesting it.
- **Cancelled**: the user pressed Stop mid-run. The agent card renders failed/cancelled, the partial answer becomes the existing detached "Stopped — not saved" treatment, and no document row survives.
- **Download expired or gone**: a `404` reads as "no longer available," never "deleted," because the same status also covers a document that was never the caller's. Inherited unchanged from `DocumentDownloadStore`.
- **Busy**: a second turn while one is streaming still returns 409 `conversation-busy`; a long File Agent run widens this window, which is why the run has its own time bound rather than only the outer turn's.
- **Feature off**: turns behave exactly as they do today; there is no "file generation unavailable" panel, because no surface ever promised one.

**UI/UX highlights.**

- The chip reuses `shared/chip/attachment-chip` and its existing variant set rather than a second component — the one presentational addition this PRD makes.
- The nested activity rendering is the same mechanism `features/chat/transcript/activity-card.ts` already performs for MCP children; this feature is the first to nest a nontrivial subtree under an `Agent`-kind parent.
- Keyboard: the chip is a real `<button>`, reachable in tab order, activated by Enter and Space, with focus returning to it after the download starts.
- Announcements: the chip's appearance is announced once through the transcript's existing `aria-live` region; the streaming sub-status lines are not announced individually.
- Motion honours `prefers-reduced-motion` using the existing keyframes; no new keyframe is introduced.

## 6. Technical considerations

**Three findings shape the design below, beyond what the invocation already established.**

**1. `AIAgent.AsAIFunction()` — and therefore `WithTracking(...)`, which wraps it — exposes exactly one string parameter in and one string out.** Confirmed from `Microsoft.Agents.AI.xml` (1.17.0): *"The resulting function accepts a query string as input and returns the agent's response as a string."* This settles a question the invocation left open: the File Agent's outward tool schema is a single natural-language instruction, not structured `sourceDocumentNames`/`targetFormat` parameters. The assistant composes that instruction from context it already has — the document names `document_search`/`document_summarize` already surface — and the File Agent's own instructions are primed with the same names via a new `ConversationPrompts.BuildFileAgentPrompt(...)`-style helper. Because the model has no separate tool for "fetch this document's bytes," source-document resolution has to happen **inside the agent's own chat pipeline**, ahead of the model seeing the request: a delegating `IChatClient` inserted into the agent's own client chain matches document names appearing verbatim (case-insensitively) in the incoming instruction against the turn's `DocumentRetrievalScope` (`DocumentRetrievalService.MatchByName`, the same matcher `document_summarize` already uses), downloads any match's blob, uploads it to the Files API, and adds the resulting `HostedFileContent` to the `HostedCodeInterpreterTool` instance's `Inputs` before the request reaches the model. **EP-0 corrected this bullet's original claim**: raw `DataContent` on `Inputs` is silently discarded by the Responses bridge, so only a hosted file reference reaches the sandbox. This is safe to do by mutating one shared tool instance per turn rather than allocating one per call, because `ConfigureFunctionInvocation` (`Program.cs:103-109`) sets `AllowConcurrentInvocation = false` on every provider — at most one tool call executes at a time on a turn, so nothing races the mutation.

**2. `WithTracking`'s string-in/string-out bridge does not itself expose the underlying `AgentRunResponse`, which is what an artifact-persistence step needs to read `CodeInterpreterToolResultContent` off.** This is a genuine open engineering question this PRD surfaces rather than resolves: the composing code (US-302) must choose a mechanism — a second, agent-scoped function tool the model itself calls to hand off a produced artifact's identity, or an inspection point on the `AgentSession`/thread `WithTracking` is given — and record the choice in a comment beside `FileAgentToolProvider`, since neither `Andes.Extensions.AI.Agent` 0.8.0's public surface nor Microsoft's own docs pin one mechanism as canonical. What EP-0 *did* settle is where an artifact's identity appears once a response is in hand: on this deployment it arrived as a `CitationAnnotation` carrying a file id and a container id, not on `CodeInterpreterToolResultContent.Outputs` — so whatever mechanism reaches the response must walk every channel rather than the documented one alone. Every acceptance criterion below is written against the **observable outcome** (a produced artifact ends up as a persisted `Generated` document) rather than against unconfirmed internals.

**3. The task's own name for the skill script-execution guard does not exist in the installed package.** `SubprocessScriptRunner` appears nowhere in `Microsoft.Agents.AI` 1.17.0 (confirmed by scanning every DLL in the package for that string). The real type is `AgentFileSkillScriptRunner` — a **delegate**, not a class — whose own doc comment states plainly that *"implementations determine the execution strategy (e.g., local subprocess, hosted code execution environment)."* Nothing in the package ships a default implementation; one is only ever wired in by calling `AgentFileSkillsProviderBuilder.UseFileScriptRunner(...)`. The correct, testable guard is therefore about the runner rather than a nonexistent class — **but not its absence**: implementation found that `AgentSkillsProviderBuilder.Build()` *rejects* a file-based skill source with no runner ("File-based skill sources require a script runner"), so the guard is that the solution registers exactly one, and that the one it registers throws.

**Integration points.** All verified against the working tree.

| Concern | Where |
| --- | --- |
| The single tool-assembly funnel | `Enterprise.Gpt.Service/ConversationService.cs:2127-2357` (`CreateChatOptionsAsync`) — builds one `List<AITool>` and assigns `chatOptions.Tools` once; `document_summarize`'s gate ladder at lines 2210-2317 is the pattern to copy |
| The tool shape to copy | `Enterprise.Gpt.Service/Tool/DocumentSummaryTool.cs` — `static class` + `public const string ToolName` + `public static AIFunction Create(...)` over a private per-turn invoker, `[Description]` on parameters, `ChatProgress.Report(...)`, a linked `CancellationTokenSource` deadline, every terminal failure a sanitized `InvalidOperationException` |
| Name-matching shared with the other document tools | `Enterprise.Gpt.Service/Tool/DocumentRetrievalService.cs` — `internal static IReadOnlyList<RetrievableDocument> MatchByName(DocumentRetrievalScope, string)` |
| Agent classification, already installed and unused | `Enterprise.Gpt.Api/Program.cs:117-121` — `ConfigureToolTracking` calls `UseAgentToolClassification()` for all four providers |
| The Responses-route registration this rides | `Enterprise.Gpt.Api/Program.cs:157-191` — `ChatClientKeys.AzureOpenAI`, `new OpenAIClient(...).GetResponsesClient().AsIChatClient(...)`, already decorated with `.UseToolTracking(ConfigureToolTracking).UseFunctionInvocation(...)` |
| The route this cannot use, and why | `Program.cs:212-236` — `ChatClientKeys.AzureAIFoundry` is `.GetChatClient(...).AsIChatClient()` (Chat Completions); structurally cannot carry a hosted tool |
| Provider-to-service-key map | `Enterprise.Gpt.Dto/Enums/Providers.cs` — `ServiceKeys[Providers.AzureOpenAI] = ChatClientKeys.AzureOpenAI`; the pinned catalog model's `ProviderId` must resolve to this key |
| Agent-as-tool wrapper | `Andes.Extensions.AI.AgentToolTrackingExtensions.WithTracking(AIAgent, AIFunctionFactoryOptions, AgentSession?, bool trackUsage, bool reportFunctionCalls)`, `Andes.Extensions.AI.Agent` 0.8.0 |
| Function-invocation bounds shared by every provider | `Program.cs:103-109` — `AllowConcurrentInvocation = false`, `IncludeDetailedErrors = true`, `MaximumIterationsPerRequest = 5`, `MaximumConsecutiveErrorsPerRequest = 5` |
| Usage translation and the two nulls to fill | `Enterprise.Gpt.Service/Chat/UsageReportTranslator.cs:260-264` (the nulls), `:341-347` (`MapKind` already maps `ToolKind.Agent` → `ConversationToolKinds.Agent`) |
| The deferred index | `Enterprise.Gpt.Repository/Configurations/ConversationUsageToolCallConfiguration.cs` — "Add it with the first agent that reports a model" |
| MCP attribution rule the tool name must not trip | `UsageReportTranslator.cs:313-339` — `ResolveMcpServerId`, longest `{sanitizedServerName}_` prefix match |
| Billing-isolation precedent and its fix | `Enterprise.Gpt.Service/Summarization/DocumentSummaryService.cs:269-290` — `RunDetachedAsync` (`ExecutionContext.SuppressFlow()` + manually carried `Activity.Current`); regression guard `ConversationServiceTests.StreamConversationAsync_SummaryToolMakesItsOwnModelCall_DoesNotBillItToTheTurn`. Applies to any File Agent code path that calls a tracked client **outside** the `WithTracking`-wrapped agent run |
| Layering exclusion to preserve | `Enterprise.Gpt.Service/Enterprise.Gpt.Service.csproj` — references `Andes.Extensions.AI`, `.Mcp`, `.UI` and pointedly not `.Agent`, with a comment recording the exclusion |
| The abstraction shape to mirror | `Enterprise.Gpt.Service/McpToolProvider.cs:34-64` — `IMcpToolLeaseSet : IAsyncDisposable` / `IMcpToolProvider.AcquireToolsAsync(...)` |
| Where the agent is composed | `Enterprise.Gpt.Api/Agents/` (empty, committed) |
| Where skill content is deployed from | `Enterprise.Gpt.Service/Agents/Documents/Skills/` (empty, committed); deployed the way `Enterprise.Gpt.Service.csproj` already ships `Prompts/*.md` — `<None Update="Agents\Documents\Skills\**\*.md"><CopyToOutputDirectory>Always</CopyToOutputDirectory></None>` |
| Skills SDK surface, confirmed present in `Microsoft.Agents.AI` 1.17.0 | `AgentSkillsProvider`, `AgentSkillsProviderBuilder` (`.UseFileSkill`, `.UseSkill`/`InlineSkill`, `.UseFilter`, `.Build()`), `AgentFileSkillsSourceOptions`, `AgentFileSkillScriptRunner` (delegate), `ToolApprovalAgentOptions`, `AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule`, `.UseToolApproval(...)`, `ToolApprovalRequestContent` — every name confirmed by scanning the installed package's DLLs and XML docs directly, not from memory |
| Hosted code interpreter SDK surface, confirmed present in `Microsoft.Extensions.AI.Abstractions` 10.9.0 | `HostedCodeInterpreterTool` (with `.Inputs`, added in 10.9.0), `CodeInterpreterToolCallContent`, `CodeInterpreterToolResultContent`, `HostedFileContent` (with `.Scope`, which carries the container id). The download itself needs `IHostedFileClient`, built from `OpenAIClient.AsIHostedFileClient()` in `Microsoft.Extensions.AI.OpenAI` 10.9.0 — the `OpenAIFileClient` overload cannot see a container file. |
| Content-type map for downloads | `Enterprise.Gpt.Service/DocumentService.cs:591-599` — `ResolveContentType` over a `FrozenDictionary` covering `.doc .docx .md .pdf .pptx .txt`; needs `.xlsx` and `.csv` |
| Signed link generation and its constraint | `Enterprise.Gpt.Service/BlobStorageService.cs` — `IBlobStorageService` is `byte[]`-based with no list operation; `GenerateSasUri` throws `StorageNotConfiguredException` unless `BlobClient.CanGenerateSasUri` is true, which requires a `StorageSharedKeyCredential` — `DefaultAzureCredential` would need a user-delegation SAS instead |
| Download route and ownership rule | `Enterprise.Gpt.Api/Endpoints/DocumentEndpoints.cs:80-82`, `Enterprise.Gpt.Service/DocumentService.cs:427-443` — ownership read off the parent conversation in the query itself, so a miss is a `FirstOrDefaultAsync` returning `null` → 404, never a 403 |
| Retrieval candidate set, which a generated document must never enter | `Enterprise.Gpt.Service/Tool/DocumentRetrievalSql.cs` — a `UNION ALL` of the two chunk tables; a document with zero chunk rows is structurally absent, not filtered out |
| Entity shape | `Enterprise.Gpt.Entity/BaseDocument.cs` (`UserId`, `Name`, `Extension` `[StringLength(8)]`, `MimeType`, `Size`, `Path`), `ConversationDocument.cs` (no discriminator today) — the discriminator goes on the latter, never the former, so `ProjectDocument.cs` is untouched |
| Own-DI-scope precedent for the persistence write | `DocumentSummaryService.cs` — summary and usage rows are written on a fresh scope via `IServiceScopeFactory`, never the request's shared `EnterpriseGptDbContext`, so a failure there cannot leave the turn's own save re-attempting an insert that already partially committed |
| Server-produced-file precedent worth reading | `Enterprise.Gpt.Service/Export/` (`ConversationExportService`, `WordExportRenderer`, `PdfExportRenderer` over `DocumentFormat.OpenXml` + `PDFsharp-MigraDoc`) — the only place this codebase writes an Office-shaped or PDF file today, and it does so without a sandbox |
| Options-class and startup-validator precedent | `Enterprise.Gpt.Service/Settings/SummarizationOptions.cs`, `Enterprise.Gpt.Api/Startup/SummarizerBootstrapper.cs` — `AddOptions<T>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` plus a startup validator that runs regardless of the feature flag |
| Permission ids and their names | `Enterprise.Gpt.Dto/Enums/PermissionIds.cs` — `Administrator`, `UploadFile`, and the `Names` `FrozenDictionary` validated at endpoint-map time |
| Latest migration to follow | `Enterprise.Gpt.Repository/Migrations/20260825224934_AddDocumentSummaries` — the fourteenth; `docs/prd/sheet-ingestion/sheet-ingestion.md` ships the fifteenth first (§9), so this PRD's discriminator migration is the sixteenth |
| Client chip, download store, activity nesting | `enterprise-gpt-ui/src/app/shared/chip/attachment-chip/`; `core/documents/document-download-store.ts` (the shipped mint-on-click path, including its 404/503 copy); `features/chat/transcript/activity-card.ts` (existing nesting, `toolKind`/`displayName` rendered as separate elements) |
| Stream contract and drift guard | `enterprise-gpt-ui/src/app/domain/stream/andes/assistant-ui.contract.ts` (`ToolKind = "Unknown" \| "Function" \| "McpTool" \| "Agent"`, confirmed present); `npm run check:contract` |

**Data storage & privacy.**

- **The document-type discriminator lives on `ConversationDocument`, not `BaseDocument`.** `ConversationDocumentTypes { Uploaded = 1, Generated = 2 }` in `Enterprise.Gpt.Dto/Enums/`, numbered from 1 so an unset column is distinguishable from a legitimate value, matching `JobStatus`'s own convention. Configured with `HasConversion<int>()` and no `HasColumnName`, matching the invariant `Tool/DocumentRetrievalSql.cs` already depends on for its hand-written SQL. Every existing row backfills to `Uploaded`.
- **A generated file lives in its own Azure Blob Storage container named `generated-documents`, never in `documents`.** The name is supplied by a new `AzureStorage:GeneratedContainer` setting whose value is `generated-documents`, read on each use rather than cached at construction, matching how `DocumentService` consumes `AzureStorage:DocumentsContainer`; unset throws `StorageNotConfiguredException` → the existing 503 `/problems/storage-not-configured`, no new problem type. Blob key convention `{userId}/{conversationId}/{documentId}{extension}`, matching the existing pattern.
- **Neither type has chunks, and this is structural, not a filter.** `DocumentRetrievalSql`'s `UNION ALL` needs no change for a generated document to be unretrievable — it contributes nothing because it has no chunk rows. Pinned by an integration test that inserts one row of each type and asserts zero chunk rows for the generated one, exactly the shape `document_summarize`'s own retrieval-isolation guard already takes.
- **The transcript message carries an identity, never a credential.** `TranscriptMessageDocument` (`Entity/Transcripts/TranscriptDocuments.cs`) has no field for this today — `Content` is a bare string. It gains an `attachments[]` array of `{ id, name, extension, mimeType, size }`, joining `/content/*` on the indexing-policy exclusion list; `TranscriptHeaderDocument.CurrentSchemaVersion` bumps from `1` to `2`, and a transcript persisted before this change deserializes the missing array as empty, so no transcript migration runs.
- **The persistence write runs on its own DI scope**, mirroring `DocumentSummaryService`'s pattern: the blob write and the `ConversationDocument` insert happen together, via `IServiceScopeFactory`, never the request's shared `EnterpriseGptDbContext` — so a failure here cannot leave the turn's own save re-attempting a half-committed insert after the answer has already streamed.
- **Migration placement.** One migration in this PRD's own scope for the discriminator (the sixteenth, following Sheet Ingestion's own fifteenth migration, itself following `AddDocumentSummaries`); the `(ModelId, DateCreated)` index (EP-5) is a second, independent migration.
- **Prompt content never rides telemetry or a progress event.** Sandbox source, tool arguments and generated content stay out of `ChatProgress` lines and out of metric tags; `ToolTrackingOptions.IncludeToolArguments` stays off.

**Security.**

- **The sandbox has no outbound network access.** Every input is uploaded by the API; every output is pulled back by the API. It cannot read Blob Storage, fetch a font, or call any API — which is also why PDF fidelity is bounded and stated as a criterion rather than implied away (FR-24).
- **A generated document is model output and is treated as untrusted.** Served with the existing `attachment` `Content-Disposition` and a content type derived from the extension, never a declared MIME type — an `inline` disposition would let generated HTML or SVG execute on the storage origin.
- **SAS signing still requires a shared-key connection string.** `BlobClient.CanGenerateSasUri` is true only for a `StorageSharedKeyCredential`; the second container changes nothing about that constraint.
- **A generated artifact is capped at the same ceiling as an upload** (`Documents:MaxFileSizeBytes`, 50 MB), enforced when the artifact is pulled out of the sandbox, so a runaway script cannot write an unbounded blob.
- **No skill tool ever executes a subprocess on the API host.** Three things together: `AgentFileSkillsSourceOptions.AllowedScriptExtensions = []`, so nothing is discovered as a script; `AllowedResourceExtensions = [".md"]`, so a `.py` beside a skill is not even readable; and the one script runner the package insists on is a delegate that throws.
- **All three skill tools are auto-approved for read-only use**, via `AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule` on a `ToolApprovalAgentOptions` passed to `.UseToolApproval(...)`. Left unconfigured, every skill call returns `ToolApprovalRequestContent` instead of executing — a headless server turn with no human to answer, which would stall every File Agent run silently.
- **Per-turn container isolation is inherent, not configured.** The Responses API's Code Interpreter tool provisions its own execution context per request on this route; no toolbox or shared-project construct is used anywhere in this design.

**Scalability & performance.**

- **The run is synchronous inside a turn that already holds a conversation lock**, so a slow run widens the window in which a second turn gets 409 `conversation-busy`. Its own `ToolTimeoutSeconds` deadline and Stop-cancellation propagation are the mitigation; neither alone is sufficient.
- **`FunctionInvokingChatClient.MaximumIterationsPerRequest = 5` bounds only the outer assistant's own tool loop** and is not inherited by the File Agent's internal pipeline — a test asserts the two are configured independently.
- **A Code Interpreter session is billed on top of token fees.** Sandbox seconds are measured and attributed as a distinct metric (EP-5), not inferred from token counts.
- **`AllowConcurrentInvocation = false` (Program.cs:105) is what makes mutating one shared `HostedCodeInterpreterTool.Inputs` instance per turn safe** rather than requiring a fresh instance per call — see the technical finding above.

**AI system requirements.**

- **Tools the system needs**: `HostedCodeInterpreterTool` over the existing Azure OpenAI Responses registration, and a dedicated pinned catalog model on that same provider, both gated by EP-0 before any code depends on them.
- **The primary evaluation is deterministic and free**: after generating, the agent runs a second sandbox pass that re-opens the artifact and asserts it parses, is non-zero bytes, and matches the requested shape — sheet count for `xlsx`, slide count for `pptx`, page count for `pdf`, a parseable header row for `csv`. It makes **no model call**. This is what the artifact-validity success criterion counts, and it is an acceptance criterion, not a metric.
- **Offline benchmark and pass threshold**: a fixed benchmark of 30 prompts spanning all seven create formats plus the edit, compare and convert verbs. Pass threshold before EP-4 is accepted: **≥ 90%** produce an artifact that passes verification, and **100%** of the remainder surface as a failed activity card — a silent text-only answer counts as a failure regardless of the prose quality.
- **Skills satisfy targeted loading at two levels, and both matter.** `AgentSkillsProviderBuilder.UseFilter(...)` trims the *advertise* stage by the run's source/target formats before a single token is spent; progressive disclosure (`load_skill` → `read_skill_resource` → `run_skill_script`) trims the *load* stage after. Filtering alone or disclosure alone is half the mechanism. Skills are per-turn state — the provider is built fresh each turn, never cached across conversations.
- **The proposed skill set**, one per format family plus one per verb: `docx-authoring`, `xlsx-authoring`, `pptx-authoring`, `pdf-authoring`, `csv-tabular`, `markdown-text`, `document-comparison`, `document-conversion`, `artifact-verification`. Each `SKILL.md` carries the production Python recipe naming real, currently-shippable libraries; deep API detail moves to `references/*.md`, read on demand.
- **Named Python tooling**, as recorded by EP-0's inventory (`FileAgentSpike/Evidence/sandbox-inventory.json`, Python 3.11.15): `python-docx` 1.2.0 (docx), `openpyxl` 3.1.5 (xlsx), `python-pptx` 1.0.2 (pptx), `pandas` 1.5.3 and `tabulate` 0.9.0 (csv/tabular), `reportlab` 4.4.5 and **`fpdf` 2.8.3 — the fpdf2 API, not the 1.x this PRD originally assumed** (pdf authoring), `pypdf` 6.3.0 / `PyPDF2` 3.0.1 / `pdfplumber` 0.6.2 (pdf reading and table extraction), **`PyMuPDF` 1.26.6** (pdf text-and-layout extraction — the engine behind `pdf` → `docx`), **`weasyprint` 53.3** (HTML → pdf rendering — the engine behind Office → `pdf`, since no `soffice` binary was found), `Pillow` 9.1.0 (raster work a chart export needs), plus `beautifulsoup4` 4.14.3, `lxml` 6.1.1 and `matplotlib` 3.6.3. **`markdown` is absent** — the one library on the original list that is not installed, so no skill may import it. `pandoc`, `docx2pdf`, `mammoth` and `pdf2docx` are absent too; none is installable, because the sandbox has no outbound network on any of the three paths EP-0 probed. Every Office → `pdf` and `pdf` → Office claim carries a fidelity tier rather than a yes/no — see the conversion matrix below.
- **Proposed conversion matrix** (source rows, target columns; confirmed and published authoritatively by US-003). Conversion is graded on a **fidelity tier**, not a binary — refusing a pair the sandbox can genuinely perform is as much a defect as promising a fidelity it cannot reach:

  - **`✓` faithful** — the target carries everything the source expressed that the format can hold. No caveat needed in the answer.
  - **`◐` structural** — content, heading hierarchy, tables, lists and images survive; exact pagination, typography, and vendor-specific layout do not. The answer states what was lost. This is a real conversion, not a refusal.
  - **`refused`** — no path in this sandbox produces a result worth handing a user. Refused by name before any run starts.

  | From \ To | docx | xlsx | pptx | pdf | csv | md | txt |
  | --- | --- | --- | --- | --- | --- | --- | --- |
  | docx | — | n/a | n/a | **◐**¹ | n/a | ✓ | ✓ |
  | xlsx | n/a | — | n/a | **◐**¹ | ✓ | ✓ | ✓ |
  | pptx | n/a | n/a | — | **refused**⁶ | n/a | ✓ | ✓ |
  | pdf | **◐**² | **◐**³ | **refused**⁴ | — | **◐**³ | ◐⁵ | ◐⁵ |
  | csv | ✓ | ✓ | n/a | **◐**¹ | — | ✓ | ✓ |
  | md | ✓ | n/a | n/a | ✓ | n/a | — | ✓ |
  | txt | ✓ | n/a | n/a | ✓ | n/a | ✓ | — |

  ¹ **Office → `pdf` is supported, not refused.** A *pixel-faithful* render needs LibreOffice or Word, and neither is guaranteed in a hosted Code Interpreter image — but faithfulness is not the only useful outcome. The structural path is fully achievable with libraries the image is known to carry: read with `python-docx`/`openpyxl`/`python-pptx`, emit HTML, render with **`weasyprint`** (present, 53.3 in the published inventory), or compose directly with `reportlab`. US-003 probes for a `soffice` binary first and uses it when present, giving `✓`; absent it, the structural path gives `◐` and the answer says the typography is the sandbox's, not the source's. Refusing a conversion this common because the best-case renderer is missing would fail the user over a fidelity distinction they did not ask about. ² `pdf` → `docx` recovers text, heading structure, tables and embedded images via **`PyMuPDF`** (present, 1.19.6) written out with `python-docx`. It is an editable document with the source's content — **not** a faithful reconstruction of its layout, and the answer says so. ³ Tables only, via `pdfplumber`'s table extraction; dependable for ruled tables, unreliable for whitespace-aligned ones, and the answer reports how many tables it found. ⁴ Reconstructing a slide deck from rendered PDF pages produces one image-per-slide with no editable content — worse than useless, so refused outright. ⁵ Text extraction; legible for text-based PDFs, not guaranteed for a scanned one, stated as such in the answer. `n/a` cells are not proposed for v1 (for example spreadsheet-to-slide-deck) because no natural request maps to them; they are not "refused," they are simply not offered. ⁶ **Demoted by US-003's run**: no `soffice` was found, and the structural path through `python-pptx` → HTML → `weasyprint` did not produce a pdf worth handing over for a slide deck specifically. **US-003 has since confirmed every cell against the real sandbox; the authoritative table now lives in `Enterprise.Gpt.Service/Agents/Documents/conversion-matrix.json`, rendered into `docs/file-agent/sandbox-capabilities.md` §5 and guarded by a drift test. Where this table and that file disagree, that file is right.**
- **Tool names are chosen deliberately.** `file_agent` is the outward tool name; a code-search-backed test asserts no MCP server name in the catalog today sanitizes to a `file_agent_`-colliding prefix, matching the same residual, acknowledged (not eliminated) risk `document_search`/`document_summarize` already carry for their own prefix.

## 7. Epics & user stories

| ID | Epic | Goal | Priority | Estimate | Depends on |
| --- | --- | --- | --- | --- | --- |
| EP-0 | Provisioning & capability spike | Prove the Responses route carries `HostedCodeInterpreterTool` in this tenant/region; inventory the sandbox's Python libraries; establish which conversion pairs are actually achievable | P0 | M | — |
| EP-1 | Generated-file contract | A file this platform produces is stored, delivered and structurally unretrievable — one implementation, one migration | P0 | L | — |
| EP-2 | Code interpreter execution | Files in as hosted file references on `Inputs`, artifacts out by their container-scoped identity, bounded and cancelled with the turn, "no file produced" a named failure | P0 | L | EP-0 |
| EP-3 | The File Agent & its skills | Composed in `Api` behind a `Service` abstraction, attached with `WithTracking`, format-targeted skills, read-only auto-approval, no host-side script execution | P0 | L | EP-0, EP-2 |
| EP-4 | Create · edit · compare · convert | The four verbs across the seven formats, each artifact re-opened in the sandbox and asserted before it is returned | P0 | L | EP-1, EP-3 |
| EP-5 | Usage, cost & observability | Fill the `ModelId`/`DeploymentName` nulls, add the deferred index, attribute sandbox seconds, bound per-user spend | P0 | M | EP-3 |
| EP-6 | Frontend surface | The chip, its download, its reload, the nested activity tree, states, a11y, bundle re-baseline | P0 | L | EP-1 |
| EP-7 | Governance & rollout | Flag, permission, rollback with no redeploy | P0 | S | EP-3 |

EP-1 depends on no Azure capability and can start immediately, alongside the EP-0 gate — schema, container and download-route work that a hand-inserted `Generated` row can exercise with no agent in existence yet, mirroring how EP-6's chip lane (`US-601`-`US-603`) is likewise testable against that same fixture before EP-3 or EP-4 land. EP-3 carries the highest proportion of `[enabler]` stories of any epic here, because composing an agent, attaching skills, and wiring approval and script-runner exclusion all have no user-visible shape until EP-4 actually calls the agent to do something — each enabler names the story it unblocks, and none exceeds L. EP-4 is the epic that turns the machinery into the four promised verbs; its ordering (create → verify → the remaining verbs) exists because verification is the gate every other verb's own acceptance criteria assume is already in place, and starting an edit or convert path against an unverified create path would mean building on a foundation nobody has proven solid. EP-5 and EP-7 are both narrow, single-purpose epics — filling two long-standing nulls and adding one governance surface — sized S/M rather than L because neither introduces new user-facing behaviour.

### EP-0: Provisioning & capability spike

#### US-001: `[enabler]` Prove Code Interpreter runs over the Responses-route Azure OpenAI client

- **Story**: `[enabler]` Stand up a throwaway console or skipped-integration-test harness that resolves the existing `ChatClientKeys.AzureOpenAI` client, attaches a bare `HostedCodeInterpreterTool`, sends a request that writes a small file, and reads it back via `CodeInterpreterToolResultContent` — so EP-2 and EP-3 are estimated against a working path in this tenant rather than a documentation page. Unblocks US-002 and, through it, the whole critical path.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Status**: ✅ Done (2026-08-27)
- **Acceptance criteria**:
  - Given the existing `ChatClientKeys.AzureOpenAI` registration, when `HostedCodeInterpreterTool` is added to `ChatOptions.Tools` and a request is sent, then the response completes and carries a `CodeInterpreterToolResultContent` naming a produced artifact.
  - Given the same request, when the sandbox is asked to make an outbound HTTP call from within its own generated code, then the call fails — confirming the no-network constraint holds in this deployment rather than assuming it.
  - Given `HostedCodeInterpreterTool.Inputs`, when a small file is supplied as `DataContent`, then it is **silently dropped** and never reaches the sandbox, and when the same file is uploaded to the Files API and supplied as `HostedFileContent`, then the generated Python code can read it — recorded as evidence for FR-10 and FR-11 rather than assumed.
  - Given the harness completes, when it is reviewed, then it is deleted or left under `tests/` as a skipped integration test; no spike code is merged into `Enterprise.Gpt.Api` or `Enterprise.Gpt.Service`.

#### US-002: `[enabler]` Inventory the sandbox's actual Python libraries

- **Story**: `[enabler]` Using the harness from US-001, run a script that imports and reports the version of every library the skills would name — `python-docx`, `openpyxl`, `python-pptx`, `pandas`, `reportlab`, `fpdf`/`fpdf2`, `pypdf`, `pdfplumber`, `PyMuPDF`/`fitz`, `weasyprint`, `Pillow`, `markdown` — **and probe the shell for a `soffice`/`libreoffice` binary**, since its presence is what promotes every Office → `pdf` cell from `◐` to `✓`. Records which are present, absent, or present at a materially different version than assumed. Unblocks US-003 and every EP-3 skill story.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-001
- **Status**: ✅ Done (2026-08-27)
- **Acceptance criteria**:
  - Given the sandbox image, when the inventory script runs, then it reports, for each named library, whether it imports successfully and its version.
  - Given a library that fails to import, when the inventory is reviewed, then the affected skill's `SKILL.md` is written against whichever library actually is available (or the format's authoring capability is marked deferred) rather than against an assumption.
  - Given the inventory result, when it is recorded, then it is committed as a comment or fixture inside the skipped harness so a future re-run of EP-0 (after an image update) can diff against it.

#### US-003: `[enabler]` Establish and publish the supported conversion matrix

- **Story**: `[enabler]` Attempt every conversion pair proposed in §6's matrix against the real sandbox — **assigning each a confirmed fidelity tier (`✓` faithful / `◐` structural / `refused`) rather than a yes-or-no**, and attempting every proposed `refused` pair anyway so the refusal is evidence rather than inheritance — and publish the confirmed matrix as the authoritative source `FileAgentOptions`/the `document-conversion` skill reference against. Unblocks US-406 and US-407.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-002
- **Status**: ✅ Done (2026-08-27)
- **Acceptance criteria**:
  - Given each `✓` cell in §6's proposed matrix, when the pair is attempted with a real sample file, then it either succeeds and is confirmed, or is demoted to `◐` or `refused` with the reason recorded.
  - Given each proposed `◐` cell — every Office → `pdf` pair and `pdf` → `docx` among them — when the pair is attempted, then the structural path is confirmed to produce an openable target carrying the source's content, or the cell is demoted with the reason recorded. A `◐` cell that is quietly refused instead of attempted fails this story.
  - Given the shell probe from US-002, when a `soffice`/`libreoffice` binary is found, then every Office → `pdf` cell is re-attempted through it and promoted to `✓` if it renders faithfully — the one finding that most widens what EP-4 can promise.
  - Given each proposed `refused` cell, when the pair is attempted anyway as a check, then the attempt is recorded — confirming the refusal is evidence-based, not merely copied from documentation about a different environment.
  - Given the confirmed matrix, when it is published, then it lives in a form `document-conversion`'s `SKILL.md` and `FileAgentOptions`' own validation can both reference without disagreeing — a single source, not two hand-copied tables, and it carries the tier per cell, not just membership.
  - Given a pair not in the proposed matrix at all, when it is discovered to be trivially achievable during this spike, then it is added with its own evidence rather than left implicit.

### EP-1: Generated-file contract

#### US-101: `[enabler]` Add the `Generated` discriminator and migrate existing rows

- **Story**: `[enabler]` Add `ConversationDocumentTypes { Uploaded = 1, Generated = 2 }` to `Enterprise.Gpt.Dto/Enums/`, put the column on `ConversationDocument` (not `BaseDocument`), and ship the sixteenth migration, backfilling every existing row to `Uploaded`. Unblocks US-103 and US-106.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given the enum, when it is placed, then it sits beside `JobStatus` and `FileExtensions`, is numbered from 1, and carries the same append-only doc-comment convention `JobStatus` already states.
  - Given `ConversationDocumentConfiguration`, when the model is built, then the property is configured with `HasConversion<int>()` and no `HasColumnName`, preserving the invariant `Tool/DocumentRetrievalSql.cs`'s hand-written SQL depends on.
  - Given the entity, when the column is added, then it is on `ConversationDocument` and not `BaseDocument`, so `ProjectDocument` is unchanged.
  - Given the migration, when it is generated, then it follows Sheet Ingestion's own migration as the sixteenth overall, is applied by the existing startup `Database.Migrate()`, and every pre-existing row reads back as `Uploaded`.

#### US-102: `[enabler]` Provision the `generated-documents` blob container

- **Story**: `[enabler]` Add `AzureStorage:GeneratedContainer`, whose value is the Azure Blob Storage container `generated-documents`, and the write path into it, keeping `IBlobStorageService` and its integration test double in step. Unblocks US-103.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given the committed configuration, when `AzureStorage:GeneratedContainer` is read, then its value is `generated-documents` — a container distinct from the one `AzureStorage:DocumentsContainer` names, so an uploaded file and a generated one never share a retention policy, an access policy or a lifecycle rule.
  - Given `AzureStorage:GeneratedContainer`, when it is read, then it is read on each use rather than cached at construction, matching `DocumentService`'s own consumption of `AzureStorage:DocumentsContainer`.
  - Given the setting is unset, when a generated file would be written, then `StorageNotConfiguredException` is raised and maps to the existing 503 `/problems/storage-not-configured` — no new problem type.
  - Given a blob key, when one is written, then it follows the existing `{userId}/{conversationId}/{documentId}{extension}` convention.
  - Given any change to `IBlobStorageService`, when the integration suite runs, then its fake implementation compiles and the suite passes.

#### US-103: `[enabler]` Persist a generated file without ingesting it

- **Story**: `[enabler]` Add the write path that stores an artifact's bytes in the `generated-documents` container and creates a `ConversationDocument` row with `Type = Generated`, on its own DI scope via `IServiceScopeFactory` rather than the request's shared `EnterpriseGptDbContext`, with **no** extraction, chunking, embedding, or document-pipeline job stage. Unblocks US-104, US-106, and every EP-4 verb.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-101, US-102
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given an artifact and its target format, when it is persisted, then a blob is written to the `generated-documents` container and a `ConversationDocument` row is created with `Type = Generated`, in one operation whose failure leaves neither a row without a blob nor a blob without a row.
  - Given the write, when it is inspected, then it runs on a scope obtained from `IServiceScopeFactory`, never the calling turn's own `EnterpriseGptDbContext` instance — a test asserts the two contexts differ.
  - Given the same write, when the pipeline is inspected, then no `IDocumentTextExtractor` is resolved, no chunker runs, and no embedding is generated.
  - Given the row after persistence, when its chunk table is queried, then it returns zero rows.
  - Given an artifact larger than `Documents:MaxFileSizeBytes`, when it is about to be persisted, then it is rejected before any bytes reach the `generated-documents` container and the run reports the reason.

#### US-104: Download a generated document in its own format

- **Story**: As a chat user, I want a file the assistant made to download with the right name and the right type, so that it opens correctly rather than arriving as an anonymous byte stream.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-103
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given a `Generated` document, when the existing download route is called by the conversation's owner, then a signed link is returned unchanged in shape from an uploaded document's.
  - Given the signed link, when it is built, then it carries `Content-Disposition: attachment` and the extension-derived content type from `DocumentService.ResolveContentType` — which, once `docs/prd/sheet-ingestion/sheet-ingestion.md` ships first (§9), already covers `.xlsx` and `.csv` alongside the six extensions it covers today, so a generated spreadsheet needs no further work here.
  - Given a caller who does not own the parent conversation, when they request the link, then the response is 404, and nothing is signed.

#### US-105: `[enabler]` Carry the generated-file reference on the transcript message

- **Story**: `[enabler]` Add an `attachments[]` array of `{ id, name, extension, mimeType, size }` to `TranscriptMessageDocument`, bump `TranscriptHeaderDocument.CurrentSchemaVersion` from `1` to `2`, and exclude the new field from the Cosmos indexing policy alongside `/content/*`. Unblocks US-601 and US-603.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given a message that introduced a generated file, when it is persisted, then its `attachments[]` array carries the file's identity and never a URL or SAS token.
  - Given a message with no generated attachment, when it is persisted, then the array is absent or empty, and no existing reader of `TranscriptMessageDocument` needs a code change to keep working.
  - Given a transcript persisted before this change, when it is deserialized, then the missing array reads as empty — no transcript migration runs.
  - Given `CurrentSchemaVersion`, when it is bumped to `2`, then the new field is excluded from the indexing policy the same way `/content/*` and `/htmlContent/*` already are.

#### US-106: A generated document is never retrieved or cited

- **Story**: As a chat user, I want a file the assistant made kept out of its own retrieval corpus, so that it is never quoted back to me as if it were a source I uploaded.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-103
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given a conversation holding one uploaded document and one generated document, when `document_search` runs, then only the uploaded document's chunks appear in the candidate set and only it can be cited.
  - Given `DocumentRetrievalSql`, when it is compared before and after this feature, then it is unchanged — the exclusion holds because the generated document has no chunk rows, not because a type predicate was added.
  - Given a `Generated` document, when `DocumentRetrievalService.GetScopeAsync` builds the scope, then it does not contribute to `HasDocuments` nor to the document-name list injected into the retrieval prompt.
  - Given an integration test that inserts one row of each type and asserts zero chunk rows for the generated one, when the suite runs, then it passes — the regression guard for this whole invariant.

**US-107 and US-108 are retired, not reused.** Both governed `.xlsx`/`.csv` files getting *into* the platform — the opposite direction from everything else in this epic — and moved to `docs/prd/sheet-ingestion/sheet-ingestion.md` (its US-101/US-102 and US-201/US-202) when that PRD was carved out as this feature's predecessor, exactly as `document-summarization.md` retires a requirement number rather than reassigning it. See §2 and §9 for the dependency this leaves behind.

### EP-2: Code interpreter execution

#### US-201: `[enabler]` Attach `HostedCodeInterpreterTool` only on the Responses-route client

- **Story**: `[enabler]` Add the service-level wiring that attaches `HostedCodeInterpreterTool` to a request only when it is bound for the Azure OpenAI (`ChatClientKeys.AzureOpenAI`) keyed client — never Azure AI Foundry, Bedrock, or Anthropic — with a code-search-backed guard, since attaching it elsewhere would either be silently ignored by that provider's SDK bridge or fail the request outright. Unblocks US-202.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-001
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given the File Agent's pinned catalog model, when its `ProviderId` is resolved through `Providers.ServiceKeys`, then it maps to `ChatClientKeys.AzureOpenAI`; a test asserts a model row pointed at any other provider fails startup validation (US-303) rather than silently misconfiguring the tool.
  - Given the resolved client, when `HostedCodeInterpreterTool` is constructed, then it is added only to the File Agent's own `ChatOptions.Tools`, never to the outer turn's `chatOptions.Tools` list `CreateChatOptionsAsync` assembles.
  - Given a code search across the solution, when it looks for `Azure.AI.Projects` or any `AIProjectClient` construction, then it finds none — decision 1 is never re-opened.

#### US-202: `[enabler]` Upload turn inputs into the sandbox and read artifacts back

- **Story**: `[enabler]` Implement the mechanism that resolves a source document named in the agent's instruction against the turn's `DocumentRetrievalScope` (via `DocumentRetrievalService.MatchByName`), downloads its blob, uploads it to the Files API and sets the resulting `HostedFileContent` on the shared per-turn `HostedCodeInterpreterTool.Inputs`, and reads produced artifacts back off the completed response — walking every channel that can carry one, then downloading each container-scoped through `IHostedFileClient`. Unblocks US-203, US-204, US-205, and every EP-4 verb.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-201
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given an instruction naming a document in scope, when the run starts, then the API downloads its blob and sets it on `Inputs` before the model sees the request — the sandbox itself never reaches Blob Storage.
  - Given `AllowConcurrentInvocation = false` (`Program.cs:105`), when two `file_agent` calls occur within one turn, then they are confirmed serialized before the shared `Inputs` list is mutated between them — a test exercises this directly rather than assuming the setting.
  - Given a run that produces one or more artifacts, when the response is read, then every artifact is extracted, in the order produced, with its original file name preserved where the sandbox supplied one — from `CodeInterpreterToolResultContent.Outputs`, from hosted file content on the message, and from a `CitationAnnotation`, which is the channel EP-0 recorded this deployment answering on.
  - Given a named document the caller does not own the parent conversation of, when the resolver attempts to read it, then the read is refused by the same ownership rule the download route enforces, and the run reports that it cannot access the file.
  - Given no document name matches the instruction, when the run proceeds, then it starts with empty `Inputs` — a create request with nothing to read from is not an error.

#### US-203: A run that produces no file fails as a named error

- **Story**: As a chat user, I want a request for a file that produced only prose to be reported as a failure, so that I am not handed a confident answer with nothing attached to it.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-202
- **Status**: Not started
- **Acceptance criteria**:
  - Given a run whose response carries text but no artifact on any channel, when it completes, then it is classified as "no file produced" and the activity renders failed, not completed.
  - Given that classification, when the caller receives it, then it is distinguishable from a run that threw and from a run whose artifact failed verification (EP-4) — three different outcomes, three different messages.
  - Given the failure, when the assistant continues, then it receives an error it can explain to the user, and no `ConversationDocument` row is created.
  - Given the run, when telemetry is emitted, then the outcome is tagged distinctly, so the "0 runs report success while producing no file" success criterion has a direct source.

#### US-204: `[enabler]` Bound a sandbox run's time and artifact count

- **Story**: `[enabler]` Bound one `file_agent` invocation by a configurable wall-clock deadline (`FileAgentOptions.ToolTimeoutSeconds`) and a maximum artifact count per run (`FileAgentOptions.MaxArtifactsPerRun`), independent of the outer turn's own `MaximumIterationsPerRequest`. Unblocks US-205 and every EP-4 verb.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-202
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given `ToolTimeoutSeconds`, when a run exceeds it, then the call is abandoned and reported as a bounded failure naming the timeout, via a linked `CancellationTokenSource` exactly as `DocumentSummaryTool` already does.
  - Given `MaxArtifactsPerRun`, when a run's response carries more artifacts than the configured ceiling, then only the ceiling's worth are persisted and the run reports that the rest were dropped, rather than persisting an unbounded number.
  - Given both bounds, when either is exceeded, then the reported reason names which bound was hit — a generic timeout message makes the wrong knob get turned.
  - Given the outer turn's `MaximumIterationsPerRequest = 5`, when this story's bounds are configured, then a test asserts they are set independently and are not silently inherited.

#### US-205: Cancel a File Agent run with the turn

- **Story**: As a chat user, I want Stop to actually stop a long file-generation run, so that I am not left waiting on work I already abandoned.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-202
- **Status**: Not started
- **Acceptance criteria**:
  - Given a running File Agent turn, when the user presses Stop, then the cancellation token reaches the sandbox call and it is abandoned rather than left to complete in the background.
  - Given the cancellation, when the turn unwinds, then the conversation lock is released, no `ConversationDocument` row is committed, and any blob already written is removed or logged for reclamation.
  - Given the cancelled turn, when usage is recorded, then whatever was already spent is still written — a cancelled turn is billed, matching the existing `ChatUsageObserver` behaviour for a cancelled request.
  - Given a second turn started immediately after a Stop, when it is sent, then it is not rejected with 409 `conversation-busy` because the first turn's lock had not been released.

### EP-3: The File Agent & its skills

#### US-301: `[enabler]` Configure the File Agent's pinned model and validate it at startup

- **Story**: `[enabler]` Add `FileAgentOptions` (`Enabled`, `ModelId`, `ToolTimeoutSeconds`, `MaxArtifactsPerRun`, `MaxRunsPerUserPerDay`) bound from a `FileAgent` configuration section, seed a dedicated `Core.Ref.Model` row pointed at `Providers.AzureOpenAI`, and add a `FileAgentBootstrapper`-style validator, mirroring `SummarizerBootstrapper`, that runs regardless of the feature flag. Unblocks US-201 and US-302.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-001
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given `FileAgentOptions`, when it is bound, then it follows `AddOptions<T>().Bind(...).ValidateDataAnnotations().Validate(...).ValidateOnStart()`, with `SectionName = "FileAgent"` and every numeric field carrying a `[Range]`.
  - Given the seeded model row, when the validator runs at startup, then it confirms the row's `ProviderId` resolves to `ChatClientKeys.AzureOpenAI` and fails startup with a message naming the mismatch if it does not — because the Azure AI Foundry route structurally cannot carry this tool.
  - Given the flag is off, when the app starts, then the validator still runs, matching `SummarizerBootstrapper`'s own stated rationale: a misconfigured deployment should say so at deploy time, not on the first request after someone flips the flag months later.
  - Given the seeded model row, when it is inspected, then `IsUserSelectable = false`, so it never appears in the chat model picker, matching the summarizer row's own precedent.

#### US-302: `[enabler]` Compose and name the File Agent behind a Service abstraction

- **Story**: `[enabler]` Build the agent in `Enterprise.Gpt.Api/Agents/` — its `Name`, its instructions (naming the seven formats and the four verbs), its `HostedCodeInterpreterTool`, and its `AgentSkillsProvider` — and expose it to `ConversationService` through `IFileAgentToolProvider`/`IFileAgentToolLease`, declared in `Enterprise.Gpt.Service`, mirroring `IMcpToolProvider`/`IMcpToolLeaseSet`. Record, in a comment beside the composing code, which mechanism (an agent-scoped persistence tool, agent-level middleware, or a session/thread inspection point) is used to reach the run's own response given `WithTracking`'s string-in/string-out bridge (§6, finding 2). Unblocks US-303, US-304, and every EP-4 verb.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-301, US-202
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given `Enterprise.Gpt.Service.csproj`, when it is inspected after this story, then it still references `Andes.Extensions.AI`, `.Mcp` and `.UI` and not `.Agent`, and carries no `Microsoft.Agents.AI` reference.
  - Given `ConversationService`, when it acquires the File Agent tool, then it does so through `IFileAgentToolProvider.AcquireAsync(scope, conversationId, cancellationToken)`, whose implementation lives in `Enterprise.Gpt.Api`, returning the tool plus a disposable bounding its lifetime to the turn.
  - Given the agent, when it is constructed, then it has a non-empty `Name` ("File Agent") — an unnamed agent falls back to rendering as a function name in the activity card.
  - Given the agent's instructions, when they are stored, then they live beside the existing prompt files rather than as an inline string literal, and name the seven formats, the four verbs, and that `.xlsm` is not supported.
  - Given the artifact-extraction mechanism this story chooses, when it is reviewed, then it is documented in a comment naming the alternative it did not choose and why, since neither `Andes.Extensions.AI.Agent` 0.8.0 nor Microsoft's own docs pin one canonical approach.

#### US-303: `[enabler]` Attach the agent as one tracked tool via `WithTracking`

- **Story**: `[enabler]` Wrap the agent with `AIAgent.WithTracking(...)` at the single tool funnel in `CreateChatOptionsAsync`, choosing `trackUsage` explicitly, so it classifies as `ToolKind.Agent` and reports usage correctly rather than doubling or losing it. Unblocks US-401, US-501, US-604, US-701 and US-702.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-302
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given the wrapper, when it is applied, then it is `AgentToolTrackingExtensions.WithTracking(...)` and never a bare `AsAIFunction()` — a code search shows zero `AsAIFunction()` call sites outside the wrapper.
  - Given `trackUsage`, when it is set, then it follows from what the agent's own client carries, and a comment beside the call records the reasoning either way: `false` if that client is decorated with `.UseToolTracking(...)`, because the nested pipeline already rolls its usage up and reporting it again double-counts; `true` if it is not. **Implementation settled this as `true`**: the agent runs on a client of its own, deliberately without tool tracking, because a nested tracker opens its own writer-less root scope — which would swallow every progress line the run reports and make the `depth: 2` children US-604 renders unreachable.
  - Given the function name, when it is set via `AIFunctionFactoryOptions`, then it is `"file_agent"`, and a test asserts no MCP server name in the catalog sanitizes to a colliding `file_agent_`-prefix match.
  - Given a turn that calls the agent, when the stream is read, then an `ActivityStarted` arrives with `toolKind: "Agent"` at `depth: 1`, and the vendored reducer nests its children with no change to the contract.
  - Given the same turn, when the usage report is translated, then `UsageReportTranslator.MapKind` produces `ConversationToolKinds.Agent` and the row is written with `Depth = 0` and its children beneath it.

#### US-304: `[enabler]` Load format-targeted Agent Skills, filtered before advertisement

- **Story**: `[enabler]` Build the `AgentSkillsProvider` via `AgentSkillsProviderBuilder().UseFileSkill(skillsRoot).UseFilter((skill, ctx) => ...).Build()`, filtering to the run's known or inferred source/target formats before any skill is advertised, and construct it fresh per turn rather than caching it. Unblocks US-305, US-306, US-307.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-302
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given a request naming or implying `xlsx` as the target, when skills are advertised, then only `xlsx-authoring` and `artifact-verification` (plus any verb-specific skill the instruction implies) appear in the system prompt — the ~100-token-per-skill advertise cost for the other five format skills is not spent.
  - Given the same provider, when a `load_skill` call is made for an advertised skill, then the returned `SKILL.md` body is under 5,000 tokens, matching the recommended progressive-disclosure ceiling.
  - Given two different turns in two different conversations, when their skill providers are inspected, then they are distinct instances — a test asserts no provider is shared or cached across conversations.
  - Given a request that names no specific format, when skills are advertised, then the filter defaults to advertising every skill rather than silently advertising none.

#### US-305: `[enabler]` Auto-approve read-only skill tools

- **Story**: `[enabler]` Switch approval off for the two read-only skill tools via `AgentSkillsProviderOptions { DisableLoadSkillApproval = true, DisableReadSkillResourceApproval = true }`, so `load_skill` and `read_skill_resource` never stall on an unanswered approval request in a headless server turn. Unblocks EP-4.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-304
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given the agent runs without this configuration, when a skill tool is called, then it returns `ToolApprovalRequestContent` instead of executing — reproduced once as a regression fixture before the fix, so the fix is provably load-bearing.
  - Given the configuration, when `load_skill` or `read_skill_resource` is called, then it executes immediately with no approval round trip.
  - Given `run_skill_script` specifically, when it is called, then it still requires approval — `DisableRunSkillScriptApproval` stays `false`, and the runner behind it refuses (US-306), so this call path can neither be auto-approved nor silently execute.
  - Given the alternative the original draft named — `UseToolApproval` with `AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule` — when it is weighed, then it is **not** used: the rule is documented as able to match any tool by name, and with `UseProvidedChatClientAsIs` the approval decorators it depends on are absent, so it would need both wired by hand for a weaker guarantee.

#### US-306: `[enabler]` Exclude host-side script execution from the skill runner

- **Story**: `[enabler]` Confirm and pin, with a code-search test, that the solution registers exactly one `AgentFileSkillScriptRunner` and that it **refuses to execute**, and set `AgentFileSkillsSourceOptions { AllowedScriptExtensions = [], AllowedResourceExtensions = [".md"] }` as belt-and-braces. Unblocks US-401 (nothing runs Python outside the sandbox).
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-304
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given a solution-wide code search, when it looks for the registered script runner, then it finds exactly one call site, and a test asserts the runner it registers throws when invoked. Zero call sites is not reachable: `AgentSkillsProviderBuilder.Build()` rejects a file-based skill source that has no runner, so a refusing runner is both the strongest available guarantee and a stricter one than absence, because it fails loudly rather than by omission.
  - Given `AgentFileSkillsSourceOptions.AllowedScriptExtensions`, when it is inspected, then it is an empty collection, so `run_skill_script` is never advertised regardless of what a skill's own directory contains.
  - Given a skill directory that happens to contain a `.py` or `.sh` file, when skills are discovered, then that file is never surfaced as a runnable script — `AllowedResourceExtensions = [".md"]` also keeps it out of `read_skill_resource`.
  - Given this configuration, when the test suite runs, then a regression test fails loudly if a future change adds a `UseFileScriptRunner(...)` call anywhere in the solution.

#### US-307: Ship skill content as deployed files

- **Story**: As a backend engineer, I want the skill markdown files to ship with the build the same way the existing prompt files do, so that a skill is available in every deployed environment without a manual copy step.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-304
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given `Enterprise.Gpt.Service.csproj`, when the skills are added, then a `<None Update="Agents\Documents\Skills\**\*.md"><CopyToOutputDirectory>Always</CopyToOutputDirectory></None>` entry ships them, matching the existing pattern for `Prompts/*.md`.
  - Given the nine skills named in §6, when the repository is built, then each has a `SKILL.md` under its own subdirectory of `Enterprise.Gpt.Service/Agents/Documents/Skills/`, discovered at the default search depth of 2.
  - Given a fresh publish output, when `Enterprise.Gpt.Api/Agents/` resolves the skills root via `AppContext.BaseDirectory`, then every `SKILL.md` is present at the expected path — a smoke test asserts this rather than assuming the csproj wiring alone.
  - Given the nine skill names, when they are reviewed, then each names real libraries confirmed present by US-002's inventory, not aspirational ones.

### EP-4: Create · edit · compare · convert

#### US-401: Create a document from a prompt

- **Story**: As a chat user, I want to describe a document and receive it as a real file, so that I can use the assistant's output in the tools my work actually happens in.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-303, US-103
- **Status**: Not started
- **Acceptance criteria**:
  - Given a request naming or implying one of `docx`, `xlsx`, `pptx`, `csv`, `md` or `txt`, when the agent runs, then it writes and executes Python in the sandbox, produces that file, and it is persisted through US-103's write path as a `Generated` document with zero chunk rows.
  - Given a request that does not name a format, when the agent runs, then it chooses one from the content and states its choice in the answer rather than asking a clarifying question that stalls the turn.
  - Given the run, when the timeline is read, then the agent card carries child activities for skill loading and the code run, and the resulting document's identity reaches the persisted assistant message through US-105's reference.
  - Given a script that raises, when the sandbox returns the traceback, then the agent retries within the sandbox's own iterative loop; when `ToolTimeoutSeconds` is exhausted, the activity renders failed and no document row is created.
  - Given a completed create, when the download route is called for the new document, then it returns a `200` with the correct file name and content type.

#### US-402: Verify the artifact before returning it

- **Story**: As a chat user, I want the assistant to check that the file it made actually opens and matches what I asked for, so that I do not discover a corrupt document after downloading it.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-401
- **Status**: Not started
- **Acceptance criteria**:
  - Given a generated artifact, when the agent finishes writing it, then a second sandbox pass re-opens the file and asserts it parses, is non-zero bytes, and matches the requested shape — sheet count for `xlsx`, slide count for `pptx`, page count for `pdf`, a parseable header row for `csv`.
  - Given the verification pass, when it runs, then it makes no model call: it is deterministic Python whose pass/fail is a returned value, and it is what the artifact-validity success criterion counts. No reviewer agent or second LLM is introduced.
  - Given verification fails, when the agent reacts, then it retries the generation within the sandbox's own loop; when the bound is exhausted, the activity renders failed, the assistant explains what did not match, and no document row and no chip are created.
  - Given verification passes, when the timeline is read, then it appears as its own `depth: 2` child activity, so a reader can see the file was checked.
  - Given the benchmark from §6, when it is run, then ≥ 90% of its 30 prompts pass verification and 100% of the remainder surface as a failed activity.

#### US-403: Produce a PDF within the sandbox's fidelity limits

- **Story**: As a chat user, I want a PDF when I ask for one, and I want to know it will look plainer than a Word export, so that I choose the right format for the job.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-402
- **Status**: Not started
- **Acceptance criteria**:
  - Given a request for a PDF, when the agent runs, then it produces one entirely inside the sandbox using only fonts and libraries confirmed present by EP-0 — no web font or external renderer is fetched, because the sandbox has no outbound network access.
  - Given the produced PDF, when it is verified per US-402, then its page count is asserted against the requested shape and it opens in a standard reader.
  - Given the fidelity limitation, when the agent returns, then the answer states plainly that the PDF uses the sandbox's own fonts and will not match a Word-exported document's typography.
  - Given a request that is really "convert this document to PDF", when it is made, then it routes through US-406's conversion path at the tier the confirmed matrix records — a `◐` structural render is served with its caveat, never refused as though the conversion were impossible.

#### US-404: Edit an existing conversation document

- **Story**: As a chat user, I want to hand the assistant a document already in the conversation and ask for changes, so that I do not have to describe the whole thing again from nothing.
- **Priority**: P1 · **Estimate**: L · **Depends on**: US-402
- **Status**: Not started
- **Acceptance criteria**:
  - Given a document in the conversation named in the instruction, when the agent runs, then it is resolved via US-202's matcher, its bytes are supplied to the sandbox, and the agent edits it in place there.
  - Given the edit completes, when it is persisted, then it is written as a **new** `Generated` document; the source — uploaded or generated — is never overwritten, so an edit is always recoverable.
  - Given the source is a document the caller does not own the parent conversation of, when the agent attempts to read it, then the read is refused by the same ownership rule the download route enforces, and the agent reports that it cannot access the file.
  - Given the edited artifact, when it is returned, then it is verified per US-402 against the shape of the original plus the requested change.

#### US-405: Compare two documents

- **Story**: As a chat user, I want to ask what changed between two documents, so that I get an answer instead of reading both.
- **Priority**: P1 · **Estimate**: L · **Depends on**: US-402
- **Status**: Not started
- **Acceptance criteria**:
  - Given two documents in the conversation, in any of the seven formats, when the agent is asked to compare them, then both are resolved and supplied to the sandbox, and the comparison runs there.
  - Given a comparison, when it completes, then the differences are reported in the answer, and a comparison **document** is produced only when the user asked for one.
  - Given a comparison that does produce a file, when it is persisted, then it follows the same `Generated` path and verification as a create.
  - Given one of the two documents cannot be read, when the agent runs, then it names which one and does not silently compare against nothing.

#### US-406: Convert a document between supported formats

- **Story**: As a chat user, I want to turn a document I already have into a different format, so that I can hand it to whichever tool actually needs that format.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-402, US-003
- **Status**: Not started
- **Acceptance criteria**:
  - Given a source document and a requested target format that appear as either `✓` or `◐` in US-003's confirmed matrix, when the agent runs, then it produces the converted file and verifies it per US-402 — a `◐` tier is a conversion to be served, not a reason to decline.
  - Given a pair confirmed at the `◐` structural tier — every Office → `pdf` conversion when no `soffice` binary is present, and `pdf` → `docx` — when it completes, then the answer names what did not survive (exact pagination, typography, vendor-specific layout) in one sentence, without turning it into a standing disclaimer on every future answer.
  - Given the same `◐` conversion, when the artifact is verified per US-402, then verification asserts the target's own shape — page count for `pdf`, openable document with the source's heading and table count for `docx` — so "structural" is a measured claim rather than a hedge.
  - Given a conversion that is inherently lossy — a text-extraction target like `md`/`txt` from a richly formatted source — when it completes, then the answer states plainly what was lost (formatting, images, layout).
  - Given the confirmed matrix, when the `document-conversion` skill is loaded, then its `SKILL.md` names exactly the same pairs as the published matrix — a test asserts the two never disagree.

#### US-407: Refuse an unsupported conversion or an unauthorized/ambiguous source cleanly

- **Story**: As a chat user, I want a conversion request the platform genuinely cannot do told to me plainly, so that I do not wait for a file that was never coming.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-406
- **Status**: Not started
- **Acceptance criteria**:
  - Given a requested pair confirmed `refused` in US-003's matrix — `pdf` → `pptx` being the only one proposed — when the agent is asked for it, then it refuses before any sandbox run starts, naming the pair and why the result would not be worth handing over (a slide deck rebuilt from rendered pages is images with no editable content), and suggests a supported alternative when one exists.
  - Given a pair confirmed at the `◐` structural tier, when it is requested, then it is **served, not refused** — a refusal here is a defect against the conversion-fidelity-honesty criterion, since a fidelity caveat is not a reason to withhold the file.
  - Given an ambiguous document name matching more than one document in scope, when the agent is asked to act on it, then it asks which one rather than guessing, at zero sandbox cost.
  - Given a document name matching zero documents in scope, when the agent is asked to act on it, then it says so and names what is available, at zero sandbox cost.
  - Given any of these refusals, when telemetry is emitted, then the outcome is tagged distinctly from a verification failure or a "no file produced" run — the conversion-honesty success criterion needs a source that does not conflate the three.

#### US-408: Stand down or fail cleanly

- **Story**: As a chat user, I want a file request that cannot run to be explained rather than to break the conversation, so that a missing capability costs me a sentence and not a turn.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-303
- **Status**: Not started
- **Acceptance criteria**:
  - Given a model whose catalog row reports `IsToolEnabled == false`, when a turn runs, then the File Agent is not attached, a warning is logged naming the model, and the turn proceeds normally — an implicit tool, exactly like document retrieval, that stands down rather than failing the turn.
  - Given the feature flag off or the agent's registration unconfigured, when a turn runs, then the tool is not attached and the assistant is never told about a capability it cannot call.
  - Given the agent throws mid-run, when the failure surfaces, then it reaches the stream as `ActivityFailed` on the agent's scope, and the exception handlers' `Response.HasStarted` short-circuit keeps the SSE stream uncorrupted.
  - Given a failure after a blob was written but before the row was committed, when the turn ends, then the orphaned blob is deleted or logged for reclamation.
  - Given any of these failures, when the logs are read, then no prompt content, sandbox source, tool argument or file content appears in them.

### EP-5: Usage, cost & observability

#### US-501: `[enabler]` Fill the `ModelId` and `DeploymentName` nulls for agent rows

- **Story**: `[enabler]` Populate `ConversationUsageToolCall.ModelId`/`DeploymentName` for `Agent`-kind rows by mapping the File Agent to its catalog `Model`, retiring the "none exists yet" comment in `UsageReportTranslator`. Unblocks US-502 and US-503.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-303
- **Status**: Not started
- **Acceptance criteria**:
  - Given an agent tool-call row, when it is built, then `ModelId` resolves to the File Agent's catalog `Model` and `DeploymentName` to that model's deployment name — the two fields currently hard-coded to null.
  - Given the comment at those two assignments, when this story lands, then it is replaced with one describing the resolution rule, not deleted silently; `Function`/`McpTool` rows still write null, and the comment says why.
  - Given the File Agent runs on a model with no catalog row (a misconfiguration US-301's validator should have already caught), when a row is nonetheless built, then `ModelId` is null and a warning is logged, rather than the write failing on a foreign-key violation after the answer has already streamed.

#### US-502: `[enabler]` Add the deferred `(ModelId, DateCreated)` index

- **Story**: `[enabler]` Add the index `ConversationUsageToolCallConfiguration` defers with "add it with the first agent that reports a model," now that one does. Unblocks per-model reporting in US-506's ceiling and any future admin cost view.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-501
- **Status**: Not started
- **Acceptance criteria**:
  - Given the configuration, when the index is added, then it is `(ModelId, DateCreated)`, matching the shape of the `(McpServerId, DateCreated)` and `(Kind, DateCreated)` indexes already there, and the deferral comment is replaced by the index itself.
  - Given the index, when the migration is generated, then it is a normal `dotnet ef migrations add`, following this PRD's own discriminator migration.
  - Given the largest table in the schema, when the index is justified, then agent rows now carry a non-null `ModelId`, which is precisely what the original deferral comment said would justify it.

#### US-503: Nested token attribution is correct end to end

- **Story**: As an administrator, I want a File Agent turn's cost to add up, so that a conversation's totals are not quietly missing what the agent spent, or doubled by a wrong `trackUsage` choice.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-501, US-401
- **Status**: Not started
- **Acceptance criteria**:
  - Given a turn that called the File Agent, when the audit rows are read, then there is one `Kind = Agent`, `Depth = 0` row with its own tokens, and the enclosing turn's own row is unaffected by them.
  - Given those rows, when their own-versus-subtree token split is checked, then `SubtreeTotalTokens` equals own plus descendants', and no `InputTokens`/`OutputTokens` value is negative.
  - Given the conversation's running totals, when compared against the sum of its usage rows, then they agree exactly, including the agent's spend.
  - Given `trackUsage` was set incorrectly in US-303, when this test runs, then it fails on a doubled or a missing total — this is the test that catches that specific mistake.

#### US-504: Measure Code Interpreter session cost

- **Story**: As an operator, I want sandbox session time measured, so that I am not reading a token bill that omits the most expensive part of the feature.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-202
- **Status**: Not started
- **Acceptance criteria**:
  - Given a run, when it completes or fails, then its elapsed sandbox time is recorded as a histogram tagged with the outcome — a session is billed on top of token fees, so tokens alone do not capture this feature's cost.
  - Given concurrent conversations, when each runs its own File Agent turn, then the count of concurrently active sandbox calls is observable.
  - Given no telemetry backend is configured, when the app runs locally, then the instrument still exists and can be asserted in tests, matching the existing chat-telemetry behaviour.

#### US-505: Emit File Agent spans and metrics

- **Story**: As an operator, I want File Agent activity in the traces I already collect, so that a slow or failing run is diagnosable without reading application logs.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-302
- **Status**: Not started
- **Acceptance criteria**:
  - Given a `file_agent.run.duration` histogram and a `file_agent.verification` counter, when they are registered, then they follow the one-line-per-instrument pattern the existing `Meter` in `Enterprise.Gpt.Service/Observability/ChatMetrics.cs` already establishes.
  - Given the instruments, when they are constructed, then they live in `Enterprise.Gpt.Service`, never `Api`, because `Service` does not depend on `Api`.
  - Given a run, when it completes, then `file_agent.verification` increments with a pass/fail outcome — the direct source for the artifact-validity success criterion.
  - Given any instrument, when its tags are inspected, then none carries prompt content, a tool argument, generated source, a file name that is user content, or a signed URL.

#### US-506: Bound what one user or conversation can spend

- **Story**: As an operator, I want a ceiling on file generation per user or per conversation, so that one person's afternoon cannot become the month's cost anomaly.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-504
- **Status**: Not started
- **Acceptance criteria**:
  - Given `FileAgentOptions.MaxRunsPerUserPerDay`, when it is set, then a request that would exceed it does not run and the assistant tells the user the limit was reached.
  - Given the ceiling is unset (`null`), when the feature runs, then it behaves exactly as it does without this story — the ceiling is opt-in, not a surprise default.
  - Given a user at the ceiling, when they hit it, then the refusal is a stood-down tool and a plain explanation, not a 403 or a failed turn.
  - Given the ceiling, when it is measured against, then it counts runs and, where sandbox-second data from US-504 is available, sandbox seconds — a runs-only ceiling would miss the part of the bill that metric exists to expose.

### EP-6: Frontend surface

#### US-601: Show a generated document on the assistant message

- **Story**: As a chat user, I want the file the assistant made to appear under its answer, so that I can tell a file it produced from one I uploaded.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-105
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given a message carrying an `attachments[]` entry, when the transcript renders, then a chip appears under the assistant's message showing the file name, an extension glyph and the size.
  - Given the chip, when it is built, then it is a variant of the existing `shared/chip/attachment-chip` component rather than a second chip component, visually and semantically distinguishable from an upload chip via its leading glyph and accessible name ("Generated file, {name}").
  - Given a message with no attachments, when it renders, then nothing changes about it — no empty container, no placeholder.
  - Given the store holding the transcript, when it is inspected, then it holds the file's identity fields only and never a `downloadUrl`.
  - Given a hand-inserted `Generated` document and a message referencing it, when this component is tested, then it renders correctly with no backend agent existing — testable before EP-3/EP-4 land.

#### US-602: Download a generated document on click

- **Story**: As a chat user, I want clicking the chip to save the file, so that the assistant's output ends up on my machine.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-601, US-104
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given a chip, when it is clicked, then `DocumentDownloadStore` requests the link at that moment and hands it to a detached anchor — no link is prefetched on render.
  - Given the returned URL, when the download starts, then it is never written to store state, `localStorage`, `sessionStorage`, a router URL, or a log.
  - Given one chip is downloading, when the rest of the transcript is used, then only that chip shows a pending state.
  - Given a `404`, then the message reads "no longer available" rather than "deleted"; given a `503` storage-not-configured problem, then it reads as a platform configuration fact — both inherited unchanged from the existing store.

#### US-603: A generated document survives a reload

- **Story**: As a chat user, I want the file still there when I come back to the conversation tomorrow, so that a file announced only while I was watching is not lost.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-601, US-105
- **Status**: ✅ Done (2026-08-28)
- **Acceptance criteria**:
  - Given a conversation reopened after a reload, when the transcript replays, then every generated file's chip renders from the persisted message reference, in the same position under the same message.
  - Given the reopened conversation, when a chip is clicked, then a fresh link is minted; no link from the original turn is reused, because none was ever persisted.
  - Given the activity tree, when the conversation is reopened, then it is not replayed — only the answer text and the file chips are.
  - Given a generated document whose row has since been soft-deleted, when its chip is clicked, then the 404 path from US-602 applies and the chip reports it rather than appearing broken.

#### US-604: Watch the File Agent work

- **Story**: As a chat user, I want to see what the assistant is doing while it builds my file, so that a long wait reads as progress rather than as a hang.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-303
- **Status**: Not started
- **Acceptance criteria**:
  - Given a turn that calls the File Agent, when the stream is folded, then a `depth: 1` activity card renders with the agent's `displayName` and an **Agent** kind badge shown as separate elements — a pre-composed label like "Calling File Agent" fails this criterion.
  - Given the agent's internal steps, when they arrive as `depth: 2` events with a `parentScopeId` naming the agent's scope, then they render nested inside the agent card, using the nesting `activity-card.ts` already performs for MCP children.
  - Given the stream, when it is inspected, then no new `AssistantUiEvent` kind was added and `npm run check:contract` still passes against the vendored `Andes.Extensions.AI.UI` contract.
  - Given an activity that completes, when it renders, then its duration is shown; given one that fails, then the failed state renders with its reason.
  - Given the window between the tool call and the first child event, when it is rendered, then the agent card's own running state fills it — no sub-status line is fabricated to cover the gap.

#### US-605: Generated-file failure states

- **Story**: As a chat user, I want a failed file request to look like a failure, so that I do not wait for a chip that is never coming.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-604, US-408
- **Status**: Not started
- **Acceptance criteria**:
  - Given an `ActivityFailed` on the agent's scope, when it renders, then the card shows failed with its reason and no chip renders for that turn.
  - Given the distinct backend outcomes — verification failed, no file produced, refused conversion, and the run threw — when each renders, then the copy differs; a single generic "something went wrong" fails this criterion.
  - Given a turn cancelled by Stop, when it renders, then the existing detached "Stopped — not saved" card appears and no chip does.

#### US-606: Use the generated-file surface without a mouse

- **Story**: As a chat user relying on a keyboard or a screen reader, I want to reach and download a generated file, so that the feature is usable rather than decorative.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-601, US-604
- **Status**: Not started
- **Acceptance criteria**:
  - Given the chip, when the transcript is navigated by keyboard, then it is a real `<button>` in tab order, activated by Enter and Space, with a visible focus ring, and focus returns to it after the download begins.
  - Given a screen reader, when a chip is focused, then its accessible name states that the file was generated and names it — the icon is not the only carrier of that distinction.
  - Given a chip appearing at the end of a turn, when it renders, then it is announced once through the transcript's existing `aria-live` region; the streaming `depth: 2` sub-status lines are not announced individually.
  - Given the axe-core run on the chat route in both themes, when this story lands, then it reports 0 serious or critical violations attributable to the generated-file chip or the agent activity subtree.
  - Given `prefers-reduced-motion: reduce`, when the agent card's running indicator renders, then its animation is suppressed using the existing motion rules; no new keyframe is added.

#### US-607: `[enabler]` Re-baseline the initial bundle

- **Story**: `[enabler]` Measure and record the production bundle's initial raw/transfer size after this epic's components land, confirming they ride the lazy `chat` chunk rather than the initial graph, and update the documented baseline. Unblocks nothing downstream but is required before this epic is accepted.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-601
- **Status**: Not started
- **Acceptance criteria**:
  - Given `npm run build`, when it runs after this epic's stories land, then `check-initial-chunk.mjs` passes, confirming no static import from `main.ts` reaches any File Agent-specific component or store.
  - Given the initial bundle, when it is measured, then it stays under the 675 kB warn line, and the delta from the prior documented baseline is recorded with which story caused it.
  - Given the generated-file chip variant, when it is inspected in the build output, then it rides the same lazy chunk the existing attachment chip already does — no new lazy chunk is introduced for it.

### EP-7: Governance & rollout

#### US-701: Gate file generation on a new permission

- **Story**: As an administrator, I want to decide who can make the platform generate files, so that a capability with a different cost profile from uploading is granted deliberately.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-303
- **Status**: Not started
- **Acceptance criteria**:
  - Given a new, dedicated permission (not a reuse of `Upload File`), when it is added, then it is seeded in a migration, added to `PermissionIds` **and** to `PermissionIds.Names` — an id absent from `Names` would produce a nameless 403 and fail the map-time validation `PermissionEndpointFilter` already performs.
  - Given a user without the grant, when a turn runs, then the File Agent tool is not attached, the assistant is told nothing about it, and nothing renders an unavailable state.
  - Given the grant, when it is resolved, then it comes from the singleton `IUserPermissionCache`, not a per-request database query, and is invalidated per user by `PermissionService` when changed.
  - Given an administrator, when they hold no file-generation grant, then they do not get the capability implicitly.
  - Given a user whose grant is revoked mid-conversation, when their next turn runs, then the tool is absent from that turn; a turn already streaming is not interrupted.

#### US-702: Feature-flag the whole feature with a documented rollback

- **Story**: As an operator, I want to switch file generation off without a deployment, so that a misbehaving sandbox integration in production is a configuration change rather than an incident.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-303
- **Status**: Not started
- **Acceptance criteria**:
  - Given `FileAgentOptions.Enabled`, when it is turned off, then the File Agent tool is not attached to any turn and conversations behave exactly as they do today — a regression test asserts this.
  - Given a fresh environment with no explicit setting, when it starts, then `Enabled` defaults to **off** — a feature billed per sandbox run should be switched on deliberately.
  - Given the flag is changed, when the app restarts, then no code change or redeploy is needed, and the rollback path is exercised at least once before production enablement rather than documented and left untested.
  - Given the flag off, when a generated document already exists from before the rollback, then it remains downloadable — the flag governs new generation, not access to what was already produced.

## 8. Milestones & rollout

**Phases**, derived from the epic dependency graph.

| Phase | Contents | Relative estimate |
| --- | --- | --- |
| **Wave 0 — the gate** | EP-0 in full (US-001, US-002, US-003). Not a phase, a gate: Code Interpreter's availability, the sandbox's real libraries, and the achievable conversion pairs are all unknowns a spike resolves once rather than three separate teams guessing independently | ~1 week |
| **Wave 1 — storage, catalog, agent composition, and the chip beside them** | EP-1 in full; EP-2's enablers (US-201, US-202, US-204); EP-3 in full (US-301-307); EP-6's early chip lane (US-601-603), testable against a hand-inserted `Generated` row with no agent in existence | ~3 weeks |
| **Wave 2 — the four verbs, usage, and the rest of the frontend** | EP-2's remaining stories (US-203, US-205); EP-4 in full; EP-5 in full; EP-6's remaining stories (US-604-607) | ~3 weeks |
| **Wave 3 — hardening before switch-on** | EP-7 in full (US-701, US-702) | ~0.5 week |

**Critical path.** `US-001 → US-002 → US-003` (conversion matrix needed by US-406/US-407) joins `US-001 → US-201 → US-202 → US-204` and `US-001 → US-301 → US-302 → US-303`, which converge at `US-401 → US-402`, the gate every other EP-4 verb waits behind. EP-1's schema/storage chain (`US-101/US-102 → US-103 → US-104/US-106`) and EP-6's chip lane (`US-105 → US-601 → US-602/US-603`) run clear of the agent chain entirely and can be taken from day one. EP-7 and most of EP-5 sit at the very end, gated only on a working tracked tool, not on any specific verb.

**Risks & mitigations.**

| Risk | Mitigation |
| --- | --- |
| Code Interpreter is not enabled for this tenant/region on the Responses route | EP-0 exists specifically to find this out before anything downstream is estimated in detail |
| The proposed conversion matrix (§6) is wrong in either direction — a proposed `✓` fails, or a proposed refusal turns out achievable | US-003 attempts every cell against a real sandbox, including refused ones, and publishes a single authoritative source the `document-conversion` skill and `FileAgentOptions` both reference |
| `AIAgent.AsAIFunction()`'s query-string-only contract makes artifact extraction harder than assumed | Surfaced explicitly in §6 as an open engineering question rather than asserted as solved; US-302 requires the chosen mechanism to be documented in a comment, and its acceptance criteria are written against observable behaviour, not internals |
| `WithTracking`'s `trackUsage` flag is set wrongly, doubling or losing every agent token | US-303 requires the reasoning recorded in a comment; US-503's totals test fails on a doubled or missing total, independent of the comment being right |
| A future change routes Code Interpreter through a shared/toolbox context | US-201's acceptance criteria assert the tool is constructed only against the Responses-route client and only per this agent's own composition, with a code search guarding against `Azure.AI.Projects`/`AIProjectClient` reappearing |
| A generated document is chunked by a future refactor and becomes retrievable | US-106's integration test is the guard, and `DocumentRetrievalSql` needs no change for the invariant to hold — exactly why it must be tested rather than assumed |
| Sandbox session cost is invisible because token counting looks complete | US-504 measures session time as a first-class metric, distinct from FR-33's token-correctness work |
| A synchronous run inside the turn widens the 409 `conversation-busy` window | US-204 bounds the run, US-205 makes Stop actually stop it, and US-604 renders progress so the wait reads as work |
| The PDF path is judged against Word-export typography and declared broken | US-403 makes the fidelity limit an acceptance criterion and puts it in the answer text at the point of delivery |

**Rollout & rollback.** One flag, `FileAgentOptions.Enabled`, defaulting **off** everywhere including development. With it off, a conversation behaves exactly as it does today, and US-702 asserts that with a regression test. Enablement order: EP-1/EP-6's storage-and-chip lane can go live inert (nothing ever writes a `Generated` row without the agent), then the agent itself once EP-3/EP-4 are accepted against the offline benchmark, then the permission is granted to a pilot group before general availability. Rollback is a configuration change with no redeploy; the path is exercised at least once before production enablement. This PRD ships two schema changes — the discriminator migration (EP-1, the sixteenth overall, following Sheet Ingestion's fifteenth) and the `(ModelId, DateCreated)` index (EP-5) — both additive, so rolling the **code** back leaves the database readable. The permission gate is the last gate before general availability: until US-701 lands, the feature is reachable by anyone in an environment where the flag is on, which is why the flag defaults off.

## 9. Assumptions & open questions

**Dependency.** `docs/prd/sheet-ingestion/sheet-ingestion.md` is a **hard predecessor** of this PRD, mirroring the shape the now-deleted `file-agent` PRD used for its own `image-tool` predecessor (recoverable at `git show 7fc3b83^:docs/prd/file-agent/file-agent.md`): that revision named `image-tool.md` as owning the generated-file storage contract this PRD's own EP-1 now owns instead, consumed unchanged by the dependent PRD's frontend and usage epics. Sheet Ingestion plays the same role here for exactly one slice — uploaded-spreadsheet ingestion — not the whole feature: it ships `.xlsx`/`.csv` extraction, header-aware row-window chunking, the sheet-aware citation, and the `sheet_query` tool, and this PRD's EP-4 (edit/compare/convert) consumes whatever spreadsheet document that pipeline has already ingested through the identical `DocumentRetrievalScope`/`MatchByName` resolution every other source format already uses. US-107/US-108, previously specified here, are retired in favor of Sheet Ingestion's own US-101/US-102 and US-201/US-202 (§2, §4, §7's EP-1 note) — the same "the predecessor ships first, this PRD's epic starts once it has" sequencing, without the deeper coupling `image-tool.md`/this-PRD's original relationship had (no shared client surface, no shared blob container, no shared migration).

**Assumptions.** Each is a guess a reviewer can veto.

- The document-type discriminator is `Uploaded = 1` / `Generated = 2`, self-contained to this PRD. `docs/prd/image-input/image-input.md` (a separate, unbuilt PRD with zero shipped stories) independently proposes `Uploaded = 1` / `Attachment = 2` and reserves `3` for a hypothetical future "Generated" type of its own. This PRD does not build against that reservation, since the invocation names no dependency on that document and building toward an unshipped sibling's numbering would be inventing a coupling nobody asked for. If both PRDs are ever implemented, whichever lands second reconciles the enum — symmetric to how `image-input.md` itself handles the mirror case for the now-deleted `image-tool.md`.
- The outward tool's parameter schema is a single natural-language instruction (confirmed from `AIAgent.AsAIFunction()`'s own doc comment), with source-document resolution happening inside the agent's own chat pipeline rather than as a structured parameter. If a future Agent Framework release adds a structured-parameter overload, US-302 should prefer it — it would remove the delegating-`IChatClient` name-matching layer entirely.
- The mechanism for reaching a `WithTracking`-wrapped agent run's own response, which is where an artifact's identity is read from (an agent-scoped persistence tool vs. agent-level middleware vs. a session/thread inspection point) is left to US-302's implementing engineer to choose and document, rather than pinned here, because neither the installed package nor Microsoft's public docs name one canonical approach as of this writing.
- `trackUsage: false` is correct because the File Agent's underlying `IChatClient` is one of the four keyed clients Program.cs already decorates with `.UseToolTracking(...)`. If a future change resolves the agent's model through an **undecorated** client instead, this flag must flip to `true`, and US-503's regression test is what would catch the mismatch either way.
- A single feature flag (`FileAgentOptions.Enabled`) governs the whole feature, rather than two independent flags (one for Code Interpreter attachment, one for the agent) as the now-superseded prior design proposed. That design's second flag existed to gate a separate Foundry SDK registration decision 1 no longer requires; with Code Interpreter riding the existing Azure OpenAI registration, a second flag has no independent failure mode left to gate.
- File generation defaults off in every environment, including development — a capability billed per sandbox run should be switched on deliberately.
- An edit produces a **new** document rather than a new version of the existing row, matching this codebase's precedent that a document row is immutable once created. Versioning would need a version chain on `ConversationDocument` and a UI for it, and this PRD does not take it.
- The proposed conversion matrix (§6) is derived from the named libraries' documented capabilities, not from an executed sandbox run — it is explicitly provisional pending US-003.
- Numeric targets not supplied in the invocation, proposed here for veto: the ≥ 90% first-attempt verification pass rate, the 30-prompt benchmark size, the ≥ 99% chip-click download success rate, and `FileAgentOptions.ToolTimeoutSeconds`'s default of 300 seconds and `MaxArtifactsPerRun`'s default of 3, both mirrored from `SummarizationOptions`' own placeholder defaults rather than measured.
- Phase durations in §8 are relative sizing derived from story estimates, not a commitment; no team size was specified and none is invented here.
- The tool name `file_agent` carries the same residual, acknowledged MCP-prefix collision risk `document_search`/`document_summarize` already carry for their own naming convention — recorded, not eliminated, matching this codebase's existing posture toward that trap.

**Open questions.**

- **Does v1 need a per-user or per-conversation cost ceiling, or is telemetry-only acceptable at launch?** US-506 implements whichever answer comes back and is P1 rather than P0 precisely because the answer is not yet given — *product owner, before Wave 3*.
- **Is `AllowedResourceExtensions = [".md"]` sufficient, or should a skill ever ship a non-markdown reference (a JSON schema, a sample file)?** The current proposal keeps every skill resource as markdown; a skill needing a structured sample file would need this reopened — *backend engineer, during US-306*.
- **Should a failed verification retain the failing artifact for diagnosis?** This draft discards it: no row, no blob, no chip. Keeping it would help debug a systematic failure and would also persist a file the platform knows is broken — *backend engineer with the operator, during US-402*.
- **Does the sandbox's real image (post-EP-0) carry a `soffice`/`libreoffice` binary?** The matrix proposes every Office → `pdf` pair at the `◐` structural tier, achievable with `weasyprint`/`reportlab` alone; a `soffice` binary would promote all of them to `✓` faithful. Either way the conversion is **served, not refused** — *resolved by US-003, but flagged here since it is the single biggest swing in the quality, not the existence, of what EP-4 promises*.
- **Should the File Agent ever be offered as an explicit, user-selectable tool (like an MCP server a user opts into) rather than purely implicit?** This draft treats it exactly like document retrieval — always attached when eligible, never toggled per turn. A product decision to make it explicit would change US-303's and US-408's stand-down behavior — *product owner, if usage data suggests users want to disable it selectively without revoking the permission entirely*.
