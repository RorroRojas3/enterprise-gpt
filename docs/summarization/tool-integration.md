# Document Summarization: Tool, Persistence and Billing

Reference for how [the summarization engine](summarization-engine.md) becomes something a conversation can actually use: the `document_summarize` tool the model calls mid-turn, the two tables that turn a run's output into one canonical, shared summary, and the billing seam that charges every run to the conversation that asked for it — and to that conversation alone, even when the run was made from inside an already-billed turn. Audience: engineers extending the tool or the billing seam, and reviewers evaluating this feature's cost-containment claims.

This document assumes [Document Summarizer Configuration](model-configuration.md) (wave 1 — the pinned model) and [Document Summarization Engine](summarization-engine.md) (wave 2 — the map-reduce pipeline) as background. Everything below sits on top of both and adds nothing to either.

## 1. What this is, and what changed to get here

The [PRD](../prd/document-summarization/document-summarization.md) originally specified summarization as a direct-request feature: a `POST` route, a background job, job-status polling, and a frontend epic on the attachment chip. That design was reversed before it shipped. **Summarization is now an `AIFunction` the model calls mid-turn**, off the conversation's own `IChatClient` — the same shape `document_search` already has. There is no HTTP route, no background job, and no frontend surface; `enterprise-gpt-ui/` is untouched by this feature end to end.

This document covers what the pivot actually built — EP-3 (persistence), EP-4 (accounting and telemetry), and EP-5 (tool integration) — plus the parts of EP-6 (governance) that shipped alongside them. All of it landed in one pass, together with a fix for a real double-billing bug (§5) that testing caught before it reached production. One piece of EP-6 — a per-user/per-conversation rate ceiling, `US-602` — has **no code at all yet**; see §7.

## 2. The tool: `document_summarize`

[`DocumentSummaryTool.Create`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tool/DocumentSummaryTool.cs) follows [`DocumentTool.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tool/DocumentTool.cs)'s own shape exactly: a static factory closing over the turn's `DocumentRetrievalScope`, returning an `AIFunction` built around a private per-turn invoker class. It takes one optional argument:

```
document_summarize(documentName?: string)
```

- **Omitted** → every document in the turn's scope is reduced to one digest.
- **Supplied** → matched against the scope by [`DocumentRetrievalService.MatchByName`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tool/DocumentRetrievalService.cs) — the exact-then-prefix-then-substring matcher `document_search` already used, extracted to `internal static` specifically so the two tools that take a document name agree on what a name means.

### 2.1 Ambiguity refuses; it does not widen

This is the one place `document_summarize` deliberately disagrees with `document_search`. When a supplied name matches zero or several documents, `document_search` **widens to everything** and tells the model it did — a near-miss on a file name reading as "the documents do not say" is a worse failure than a slightly-too-broad search. `document_summarize` does the opposite: it **refuses outright**, before any model call, and returns the available document names for the model to try again with:

```json
{
  "scope": "document",
  "documentCount": 0,
  "availableDocuments": ["policy.pdf", "handbook.pdf"],
  "note": "'polcy.pdf' matches more than one document. Ask again with a full name."
}
```

The asymmetry is the whole point: a wider search is cheap, and summarizing the wrong document is not. Widening here would mean silently generating (and billing) a summary of a document the user never named.

### 2.2 The single-document shortcut

When no name is given and the scope holds exactly **one** document, that document is summarized directly — the same code path as naming it explicitly — rather than routed through the digest reduce. A one-document "digest" would be a second model call whose only job is to reduce one summary down to itself.

### 2.3 The result

`DocumentSummaryToolResult` is serialized straight back to the model: `scope` (`"document"` or `"digest"`), `documentName` (absent for a digest), `documentCount`, `summary`, and — only on a naming failure — `availableDocuments` and `note`. No citations: a summary is prose, not a set of passages, and the tool prompt (§2.5) tells the model not to invent page numbers for one.

### 2.4 Progress, and why nothing changed on the wire

The tool reports progress through the ambient `Andes.Extensions.AI.ChatProgress` reporter — `"Summarizing policy.pdf…"`, `"Reusing an existing summary"` on a cache hit, `"Summarizing 3 documents…"` with a `progress`/`progressTotal` pair on a digest. That reporter is exactly what every other native tool already uses, and it reaches the client through the same `AssistantUiEvent` `ActivityStarted`/`ActivityProgress`/`ActivityCompleted` frames [the streaming contract](../conversations/streaming-contract.md) already documents for `document_search` and the weather tool. **No change to the streaming contract, the SSE framing, or the shipped Angular client was needed** — the activity feed already knew how to render an unfamiliar tool name with a sub-status and a duration; `document_summarize` is just another one.

Progress matters more here than it does for search: a first summary of a large document can take a noticeable while, and a silent tool on an inline turn looks exactly like a stalled answer.

### 2.5 The model-facing prompt

Attaching the tool also adds an instruction block — [`ConversationPrompts.BuildDocumentSummaryToolPrompt`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Prompts/ConversationPrompts.cs) plus [`document-summary-tool-prompt.md`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Prompts/document-summary-tool-prompt.md) — that tells the model when to prefer summarization over search ("what is this about" vs. a specific fact), how to present what comes back, and, explicitly: **"Never call it twice for the same document in one turn."** That sentence is the entire enforcement mechanism for repeat calls in a turn — it is guidance the model is asked to follow, not something the code checks (§7).

### 2.6 The tool's own deadline

`SummarizeAsync` links the caller's cancellation token to a fresh `CancellationTokenSource` cancelled after `SummarizationOptions.ToolTimeoutSeconds` (default 300s) — bounding the **whole invocation**, distinct from `CallTimeoutSeconds` (default 180s), which bounds one model call inside the engine. A breach surfaces to the model as a sanitized sentence ("Summarizing took too long...") rather than failing the turn; the turn's own cancellation, by contrast, is left to propagate as cancellation rather than being turned into a tool error the model would try to work around. `Program.cs` validates at startup that `ToolTimeoutSeconds >= CallTimeoutSeconds` — a run deadline shorter than one call's own timeout would kill every run before its first call could return, which reads as a broken summarizer rather than a misconfigured one.

Every other terminal failure — a `ValidationException` from the engine or the summary service, or anything unhandled — is caught and replaced with a sanitized `InvalidOperationException` before it reaches the model; the real exception is always logged first. Nothing about a provider error, a deployment name, or a stack trace ever reaches the model or the transcript.

## 3. When the tool is attached

Attachment happens in `ConversationService.CreateChatOptionsAsync`, immediately after `document_search`'s own attachment decision, and depends on it:

| Condition | Result |
|---|---|
| `Summarization:Enabled` is `false` | Not attached. This is the feature's whole kill switch (§7). |
| The turn's document scope is empty | Not attached — same as `document_search`. |
| The model has `IsToolEnabled = false` | Not attached; a warning is logged naming the conversation and model. |
| The caller lacks the `Upload File` grant | Not attached, checked through `IUserGrantReader` (§8). |
| A selected MCP tool is already named `document_summarize` | Stands down for the turn, mirroring `document_search`'s own name-collision precedent. |
| `document_search` itself stood down (no documents, non-tool model, or its own MCP collision) | Summarization stands down too, even if every other gate passed. |

The last row is deliberate, not incidental: the tool prompt (§2.5) names `document_search` by name and tells the model when to prefer one over the other. A summarization prompt recommending a tool that is not actually on the request would be actively wrong, so the check runs *after* retrieval's own attachment has settled, not beside it.

**The permission gate is deliberately narrower than retrieval's.** Reading an already-ingested document back through `document_search` costs nothing and is gated on scope membership alone. Summarizing spends money on a cache miss, so it is gated the same way *adding* a document is — the `Upload File` grant, not a new permission id (§8, §9).

## 4. Persistence: one canonical, shared summary per document

[`BaseDocumentSummary`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/BaseDocumentSummary.cs) mirrors `BaseDocumentChunk`'s own split: an abstract, unmapped base carrying the summary text, the summarizer's `ModelId`/`DeploymentName` snapshotted at generation time, a `PromptVersion`, run-total `InputTokens`/`OutputTokens`, `ModelCallCount`, `MapUnitCount`, and `CollapsePasses` — plus two mapped derived types, [`ConversationDocumentSummary`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/ConversationDocumentSummary.cs) and [`ProjectDocumentSummary`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/ProjectDocumentSummary.cs), each with a unique FK to the document it summarizes. Migration [`20260825224934_AddDocumentSummaries`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260825224934_AddDocumentSummaries.cs) is the **fourteenth** migration in `Repository/Migrations/` — additive only, two new tables, nothing existing touched.

A project document's summary is shared: the first conversation to summarize it pays, and every later conversation over the same project reads the same row for free. A conversation document's summary is likewise reusable by that same conversation on every later turn.

### 4.1 No regeneration flag, and why that is safe

A document's text never changes after ingestion, so the **only** thing that can stale a cached summary is a change to the prompts that produced it. `PromptVersion` (a `const string` on `SummarizationPrompts`, currently `"1"`) is the entire invalidation mechanism: `DocumentSummaryService.ReadCachedTextAsync` filters on it, and a stored row whose version does not match the running one is treated as a miss and rewritten in place, in the same row, never as a second version.

There is no way for the model — or a user — to force a fresh run. This is deliberate, not an oversight: a regenerate flag the model could set on its own tool call is a flag the model could spend money with. The only way to invalidate every cached summary in the system is to bump `PromptVersion`, which is an engineering change to the prompt templates, not a request anyone makes through the tool.

### 4.2 The filtered unique index

Each table's unique index on its document FK is filtered `[DateDeactivated] IS NULL`, matching `ConversationDocumentChunkConfiguration`'s own ordinal index:

```csharp
builder.HasIndex(x => x.ConversationDocumentId)
    .HasFilter("[DateDeactivated] IS NULL")
    .IsUnique();
```

Without the filter, a summary deactivated alongside its document (§4.3) would leave a dead row occupying the unique slot, and summarizing the same document again later — a new upload with the same content, or a restored document — would be a constraint violation instead of a fresh insert. The filter is what lets "one live summary per document" coexist with "soft delete never actually removes a row."

### 4.3 Joining the soft-delete cascades

A summary that outlives its document is a fully readable account of everything that document said, sitting behind a soft delete nobody expects to still expose it. All four places that deactivate a document now deactivate its summary **first**, in the same `ExecuteUpdateAsync` sequence, ahead of the chunk cascade:

- `ConversationService.DeactivateConversationAsync`
- `ConversationService.DeactivateConversationsBulkAsync`
- `ProjectService.DeactivateProjectAsync`
- `ProjectService.DeactivateProjectDocumentAsync`

```csharp
// Ahead of the chunks and the document itself, so a summary never outlives what it
// summarizes: a live summary row under a deleted document is a fully readable account of
// everything that document said.
await _ctx.ConversationDocumentSummaries
    .Where(s => s.ConversationDocument.ConversationId == id && !s.DateDeactivated.HasValue)
    .ExecuteUpdateAsync(s => s
        .SetProperty(x => x.DateDeactivated, date)
        .SetProperty(x => x.DateModified, date),
        cancellationToken);
```

Each of the four is exercised against real SQL Server by its own test in [`DocumentSummaryCascadeIntegrationTests`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/Persistence/DocumentSummaryCascadeIntegrationTests.cs).

### 4.4 Testing infrastructure: a required FK to `Core.Ref.Model`

Any table with a **required** FK to `Core.Ref.Model` has to be cleared before an integration fixture resets the model catalog, or the failure depends on which test class happens to run first — the same failure mode `IntegrationTestFixture` already warned about for conversations. The two summary tables were not initially wired into that reset path, and it broke five unrelated `RequestLoggingIntegrationTests` purely through execution order before anyone touched a summarization test. The fix is a shared `ClearDocumentSummariesAsync` helper, called from `ResetModelsAsync` ahead of the model reset itself:

```csharp
// Summaries name the model that produced them, and that FK is required, so the rows have to
// go for the same reason the usage rows above do. Any future table with a required FK to
// Core.Ref.Model belongs here too.
await ClearDocumentSummariesAsync(ctx, cancellationToken);
```

Anyone adding a further table with a required `Model` FK should extend this same helper rather than rediscovering the failure.

## 5. Billing: one row per run, and never a second one on the turn

[`DocumentSummaryService`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Summarization/DocumentSummaryService.cs) is the only caller of `IDocumentSummarizer`. Everything below the engine's own seam — caching, ownership, money — lives here.

### 5.1 The grain: one row per run

`ConversationUsageKinds.Summarization = 3` bills at the **run** grain, not the call grain: the single-pass call, or the entire map/collapse/reduce sequence, or a digest's own reduce over per-document summaries, is exactly one `Core.ConversationUsage` row. This matches how a chat turn is already recorded — several provider round trips roll into one row, with the per-call breakdown kept elsewhere (the summary row's own `ModelCallCount`, `MapUnitCount`, `CollapsePasses`). The row carries the **summarizer's own** `ModelId`, `ProviderId`, `DeploymentName`, and both prices — read back from `run.ModelId` rather than resolved from configuration a second time, so the row can never disagree with the model the run actually snapshotted. `ContextTokens` is always `null`: a summarization run is billed and never transcribed, so there is no transcript message to estimate a context cost from — `null` says "not transcribed," where `0` would say "transcribed nothing."

A cache hit writes **zero** usage rows and moves no conversation counter. `GetOrCreateAsync` returns straight out of `ReadCachedTextAsync` on a hit, before anything that could bill runs.

### 5.2 The double-billing bug this fix closes

This is the subtlest thing in the feature, and it was a real, measured bug rather than a theoretical worry.

`IChatClientResolver.Resolve(providerId)` hands back the **fully decorated** pipeline for every provider — `.UseToolTracking(...).UseFunctionInvocation(...)` is applied once, at DI registration, and there is no separate, undecorated client to ask for instead. `DocumentSummarizer` resolves the summarizer's own client through this exact same resolver. That is fine when a summarization run is the only thing happening — but `document_summarize` is *itself* a tracked tool invocation, called from inside a turn whose own model call is already running under the tracking middleware's ambient scope. When the summarizer then makes its own nested calls through that same decorated pipeline, the tracker sees them as calls made *by the tool*, not as an independent unit of work, and rolls their tokens up onto the **enclosing turn's** `ConversationUsage.ToolInputTokens` — where `EstimatedCost` prices them at the *turn's own chat model's* rate, and `ConversationUsageToolCall.ModelId` stays `null` because nothing records which model a tool call actually used.

The practical effect, if left alone: a summarization run gets billed **twice** — once against the turn's own row, at whatever the conversation's selected chat model costs, and once against the dedicated `Summarization` row, at the summarizer's own (much cheaper) rate. For a feature whose entire premise is "summarization costs the summarizer's flat rate, never the conversation's own model's," that is not a rounding error — it defeats the goal outright. This was demonstrated with a test that drove a real nested call through the real middleware and watched 900,000 input tokens land on the turn's own row.

**There is no public opt-out in Andes for this.** The tracking middleware's ambient scope (`AmbientScope`) is `internal`, and its own doc comment states plainly that connecting a nested tracked pipeline to the invoking tracker is the *intended* behavior of the package, not a bug — there was nothing to configure or disable.

**The fix** is `DocumentSummaryService.RunDetachedAsync`:

```csharp
private static async Task<SummarizationRun> RunDetachedAsync(Func<Task<SummarizationRun>> run)
{
    var parent = Activity.Current;

    Task<SummarizationRun> detached;

    using (ExecutionContext.SuppressFlow())
    {
        detached = Task.Run(() =>
        {
            Activity.Current = parent;

            return run();
        });
    }

    return await detached.ConfigureAwait(false);
}
```

The tracker's ambient scope lives in an `AsyncLocal<T>`, and the only way to leave one is to run on an execution context that never inherited it. `ExecutionContext.SuppressFlow()` around the `Task.Run` is exactly that: the delegate runs with a clean ambient state, so nothing the enclosing tool invocation set is visible inside it. `Activity.Current` is carried across **by hand**, immediately inside the detached delegate, because the aim is narrowly to detach from the *usage tracker* — not from tracing generally. Without that one line, the summarizer's own spans would orphan from the turn's trace instead of hanging off it.

Two things worth knowing about the boundary this draws:

- **Only the engine's own model calls are detached.** Whatever the tool reports through `ChatProgress.Report` stays fully attributed to the turn, because that is what draws the activity card the user is actually watching (§2.4). Detaching that too would silence the "Summarizing…" sub-status.
- **The known cost: `ILogger` scopes do not survive the hop.** A log scope pushed on the calling execution context (a correlation id, a request-scoped property) is gone inside the detached delegate, because suppressing flow suppresses exactly the ambient state a logging scope rides on. Anything logged from inside the engine's detached calls carries less ambient context than a log line written elsewhere in the same request. This is accepted, not fixed — the alternative is carrying the double-billing bug back.

The regression guard is [`ConversationServiceTests.StreamConversationAsync_SummaryToolMakesItsOwnModelCall_DoesNotBillItToTheTurn`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Services/ConversationServiceTests.cs), which drives the *real* `DocumentSummaryService` over a substitute `IDocumentSummarizer` that makes a genuine nested call through the real middleware pipeline, and asserts both halves: the turn's own row does not carry the summarizer's tokens, and exactly one `Summarization` row does, at the summarizer's own model and deployment. It fails without `RunDetachedAsync`.

### 5.3 Writes run on their own DI scope

`PersistAsync` and `BillAsync` both open a fresh `IServiceScopeFactory.CreateAsyncScope()` and a fresh `EnterpriseGptDbContext`, rather than writing through the turn's own shared context:

> A fresh scope rather than the request's shared `EnterpriseGptDbContext` [...] EF accepts changes only on a *successful* save, so a failure here [...] would leave the summary and usage rows tracked as `Added` on the turn's context. The tool turns that failure into a sentence for the model and the turn carries on, so the turn's own save would then re-attempt those inserts: either failing the turn's finalization after a fully streamed answer, or committing a second usage row and double-billing the run.

In other words: sharing the context turns *any* transient failure in the summary write — a concurrent identical insert tripping the filtered unique index, a rowversion conflict, a transient SQL fault — into either a failed turn after the user already watched a complete answer stream in, or a silently duplicated usage row. A scope of its own makes the two writes independent failure domains.

That independence carries one ordering invariant `DocumentSummaryService.PersistAsync`'s own remarks call out by name, because nothing else enforces it: `BillCoreAsync` takes an exclusive-enough lock on the conversation row that an uncommitted transaction held open on the *request's own* context for the life of the tool call would block this second-scope write until command timeout — and since the request is the one awaiting the tool, that would never resolve on its own. It is safe today only because nothing holds a transaction open on the request's context across the tool's lifetime: conversation naming commits before chat options are built, and the turn's own transaction opens only after the stream has fully drained. A future change that opens a transaction earlier and holds it across a tool call would deadlock here rather than merely fail — a reviewer touching that ordering should re-read `PersistAsync`'s remarks first.

### 5.4 Finalization is uncancellable, on purpose

Both `PersistAsync` and `BillAsync` are called with `CancellationToken.None`, for the identical reason `ConversationService.PersistTurnAsync`'s own finalization write is: by the time execution reaches that line, the run has already been paid for at the provider. The token that would abort the write is the same one a client disconnect or the tool's own deadline (§2.6) signals — losing that race would spend the money and record neither the summary nor the usage row, which is strictly worse than a slightly-late write.

## 6. Telemetry

[`ChatMetrics.RecordSummaryRun`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Observability/ChatMetrics.cs) adds two instruments to the existing `Meter`, at **run** grain:

| Instrument | Kind | Tags |
|---|---|---|
| `enterprise_gpt.document_summary.run.duration` | Histogram (seconds) | `deployment.name`, `summary.path` ∈ {`cache-hit`, `single-pass`, `map-reduce`, `digest`}, `summary.outcome` ∈ {`success`, `failure`, `cancelled`} |
| `enterprise_gpt.document_summary.run.map_units` | Histogram (int) | Same tags |

A cache hit records with `deploymentName: null` (tagged `unknown` — nothing actually served it) and `path: "cache-hit"`, so the cache's hit rate sits in the same series as its misses rather than being invisible. The path tag is read off the *finished* run rather than predicted ahead of time, so a document that turned out to need map-reduce is not filed under the single pass it was hoped to be. No instrument carries a document name, a conversation id, or summary text — only a deployment name, a path, and an outcome, matching the privacy posture the rest of this application's telemetry already holds to.

## 7. Governance and the known gap

`Summarization:Enabled` (default `false`, everywhere, development included) is the entire rollback path: with it off, the tool is never attached to any turn, so the model cannot discover or call it, and nothing costs anything. A summary already generated before a rollback is untouched in the database and is served immediately, at no cost, the moment the flag is switched back on. `Summarization:ModelId`'s own startup validation ([model-configuration.md §4](model-configuration.md#4-startup-validation)) runs regardless of this flag — it is a deploy-time check, not a feature gate.

Two ceilings are enforced **unconditionally**, because both guard against one pathological request rather than a tunable usage pattern an operator might legitimately want off:

- `MaxMapUnits` (default 40) — checked inside the engine before the first map call (see [summarization-engine.md §6](summarization-engine.md#6-splitting-an-oversized-document-into-map-units)).
- `MaxDigestDocuments` (default 25) — checked inside `CreateDigestAsync` before any document is summarized, refusing with a message naming the limit.

**What is not built yet: a per-user or per-conversation rate ceiling (`US-602`).** There is no code anywhere in this feature that counts how many times one user, or one conversation, has invoked `document_summarize` in a period. The only thing standing between a turn and an unbounded number of summarization runs is the tool prompt's own instruction to the model — "call it once and wait for it... never call it twice for the same document in one turn" (§2.5) — which is guidance the model is asked to follow, not something enforced in code. A model that ignores that instruction, or a conversation that asks for several distinct documents in quick succession across several turns, has no ceiling here at all. This is the one genuinely open piece of EP-6; the map-unit and digest ceilings above were never this story's concern once both were confirmed unconditional.

## 8. The shared permission-grant reader

Attaching the tool needs to ask "does this caller hold `Upload File`" from **service code**, not from an endpoint filter — there is no HTTP route to filter. [`UserGrantReader`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Caching/UserGrantReader.cs) (`IUserGrantReader.GetGrantsAsync(userId, cancellationToken)`) is the answer, and it is now the **only** grant-loading query in the codebase: `PermissionEndpointFilter.Require(...)` used to carry its own private loader in parallel, and that duplication is gone — the filter now resolves `IUserGrantReader` and calls it too, exactly the way the tool-attachment path does. See [Permission Cache §4](../permissions/permission-cache.md#4-how-a-request-reads-the-cache) for the reader's place in that request path.

This was more than tidying. The filter's old inline loader captured the *request's own* `IServiceProvider`, and `IUserPermissionCache.GetOrLoadAsync`'s own doc comment warns that a loader may be shared with concurrent callers and can outlive the request that started it. `UserGrantReader.LoadAsync` creates its **own** scope via `IServiceScopeFactory` specifically so a cache miss never closes over a scoped `EnterpriseGptDbContext` that could already be disposed by the time a later, concurrent waiter's continuation runs — something the filter's inline loader never did. Consolidating onto the shared reader closed a latent correctness gap, not merely a style one.

A grant check through the reader costs nothing on the normal path: the entry is written at sign-in (`POST api/users/me`), so attaching the tool is a `FrozenSet<Guid>.Contains` check against an already-warm cache entry for every turn after a user's first.

## 9. Known limitations

Recorded here rather than papered over, because a cost-containment feature is exactly the kind of thing worth being honest about:

- **No per-turn ceiling on how many summarization runs one turn can buy.** The tool prompt asks the model to call once; nothing enforces it (§7).
- **The usage report folds a summarization run into the same "turn" it counts a chat call or a naming call as.** `Enterprise.Gpt.Service.Reports.ReportService` groups `Core.ConversationUsage` by `Status` and `Kind`, and while it isolates `Naming` into its own `NamingTurns`/`NamingTokens` figures, it has no equivalent carve-out for `Summarization` — a summarization row's tokens land in the same `AssistantTokens`/`Turns` totals a real chat turn's do. `UsageReportDto`'s own doc comment, written when there were only two kinds, still says every figure counts "both kinds of call... a chat turn and the out-of-band naming completion alike" — that statement predates this feature and is now inaccurate on a table that has three kinds. See [Conversation Usage and Favourites §3.1](../conversations/usage-and-favorites.md#31-one-row-per-model-call) and §9's known-limits list for where this is tracked.
- **The digest's progress indicator reports a total but never ticks.** `SummarizeAllAsync` reports `progressTotal: scope.Documents.Count` once, at the start, and then reports only the completion — the number never counts up per document as the digest works through its scope.
- **Concurrent identical requests are not deduplicated.** Two turns racing on the same not-yet-summarized project document may both run the engine; the filtered unique index (§4.2) still guarantees exactly one final row, but the loser's work — and its billed tokens — is simply wasted. `IUserPermissionCache`'s own `Lazy<Task<T>>` single-flight pattern is the precedent a follow-up could copy if telemetry ever shows this happening often enough to matter.
- **Whether Azure OpenAI over the Responses API honours `ChatOptions.MaxOutputTokens` as a hard cap is still unverified against a live deployment.** This is wave 2's own open assumption ([summarization-engine.md §8](summarization-engine.md#8-the-first-call-site-to-set-maxoutputtokens)), unaffected and unresolved by this wave.

## 10. Testing

| File | Covers |
|---|---|
| [`Summarization/DocumentSummaryServiceTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Summarization/DocumentSummaryServiceTests.cs) (14 cases) | Cache miss generates and persists; billing lands one row at the summarizer's own rate; conversation counters move by the run's own tokens; nothing is staged on the caller's own context (§5.3); finalization survives the caller's token being cancelled (§5.4); a cache hit costs nothing, including for a second conversation reading the same project document; a stale `PromptVersion` regenerates in place rather than duplicating; a deactivated summary is a miss that inserts beside it (§4.2); digest refusals (empty scope, over the configured maximum) spend nothing; a digest only summarizes the documents still missing one. |
| [`Tool/DocumentSummaryToolTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Tool/DocumentSummaryToolTests.cs) (14 cases) | The declared schema (`cancellationToken` hidden from the model); a name matching exactly one document; both ambiguous/unknown name failure shapes refusing without spending anything (§2.1); the digest and single-document-shortcut paths (§2.2); project-document sourcing; the tool's own deadline firing as a sanitized message rather than a failed turn (§2.6); caller cancellation staying cancellation; sanitized provider and validation failures. |
| `ConversationServiceTests` summarization block (8 cases) | Every attachment-gate combination (§3): the flag off, no documents, a non-tool model, the caller lacking `Upload File`, an MCP name collision, retrieval itself standing down; the tool prompt naming `document_search` when both are attached; and the double-billing regression guard (§5.2). |
| [`Filters/PermissionEndpointFilterTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Filters/PermissionEndpointFilterTests.cs) | Unchanged behavior after the `IUserGrantReader` extraction (§8) — AND semantics, duplicate ids, denial body shape, map-time validation, and that a cache hit never resolves a `DbContext`. |
| [`Persistence/DocumentSummaryCascadeIntegrationTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/Persistence/DocumentSummaryCascadeIntegrationTests.cs) (4 cases, real SQL Server) | Each of the four soft-delete cascades (§4.3) deactivates a document's live summary alongside its chunks, in the same transaction. |

## 11. Key files

| Concern | File |
|---|---|
| The tool | [`Enterprise.Gpt.Service/Tool/DocumentSummaryTool.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tool/DocumentSummaryTool.cs) |
| Shared name matching | [`Enterprise.Gpt.Service/Tool/DocumentRetrievalService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tool/DocumentRetrievalService.cs) — `MatchByName` |
| The tool prompt | [`Enterprise.Gpt.Service/Prompts/document-summary-tool-prompt.md`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Prompts/document-summary-tool-prompt.md), `ConversationPrompts.BuildDocumentSummaryToolPrompt` |
| The persistence/caching/billing seam | [`Enterprise.Gpt.Service/Summarization/DocumentSummaryService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Summarization/DocumentSummaryService.cs) |
| Schema and configuration | [`Enterprise.Gpt.Entity/BaseDocumentSummary.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/BaseDocumentSummary.cs), `ConversationDocumentSummary.cs`, `ProjectDocumentSummary.cs`; [`Repository/Configurations/ConversationDocumentSummaryConfiguration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/ConversationDocumentSummaryConfiguration.cs), `ProjectDocumentSummaryConfiguration.cs` |
| Migration | [`Migrations/20260825224934_AddDocumentSummaries.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260825224934_AddDocumentSummaries.cs) (fourteenth) |
| Soft-delete cascades | `ConversationService.DeactivateConversationAsync`/`DeactivateConversationsBulkAsync`, `ProjectService.DeactivateProjectAsync`/`DeactivateProjectDocumentAsync` |
| Where the tool is attached | `ConversationService.CreateChatOptionsAsync` |
| Usage grain and enum | [`Enterprise.Gpt.Common/Enums/ConversationUsageKinds.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Common/Enums/ConversationUsageKinds.cs) — `Summarization = 3` |
| Telemetry | [`Enterprise.Gpt.Service/Observability/ChatMetrics.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Observability/ChatMetrics.cs) — `RecordSummaryRun` |
| Shared grant reader | [`Enterprise.Gpt.Service/Caching/UserGrantReader.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Caching/UserGrantReader.cs) |
| Tuning configuration | [`Enterprise.Gpt.Service/Settings/SummarizationOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/SummarizationOptions.cs) — `Enabled`, `ToolTimeoutSeconds`, `MaxMapUnits`, `MaxDigestDocuments` are the four keys this wave added |
| Default configuration | [`appsettings.json`](../../enterprise-gpt-api/Enterprise.Gpt.Api/appsettings.json) — `Summarization` section |
| Test infrastructure fix | `tests/Enterprise.Gpt.Integration.Test/TestInfrastructure/IntegrationTestFixture.cs` — `ClearDocumentSummariesAsync` (§4.4) |

## 12. What's next

`US-602` — the per-user/per-conversation rate ceiling (§7) — is the one piece of this feature with no code at all. Everything else described here is implemented and tested; what remains before production traffic depends on it is committing and reviewing the working tree, and exercising the `Summarization:Enabled` rollback path once in a real environment. See the [PRD](../prd/document-summarization/document-summarization.md) §8 for the full rollout plan.
