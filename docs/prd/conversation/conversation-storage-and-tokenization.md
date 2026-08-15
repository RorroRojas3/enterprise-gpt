# PRD: Conversation Storage and Tokenization

## 1. Overview

**Problem.** A conversation transcript is one Cosmos DB document with every message embedded in a `messages[]` array (`Enterprise.Gpt.Entity/CosmosConversation.cs`). Each turn appends two array entries (`ConversationService.cs:963-972`) and point-reads the whole document to replay history (`ConversationService.cs:630`). Cosmos caps an item — and a request — at 2 MB, so a conversation that is used long enough becomes unwritable, and the default indexing policy indexes every element of the growing array along the way. Three gaps ride on top of that shape: `CosmosConversationMessage.Tokens` is on the wire but hardcoded to `0` (`ConversationService.cs:942`) and never assigned anywhere, so no per-message token attribution exists; nothing stores rendered HTML, so exporting a conversation means re-rendering markdown from scratch; and nothing stores a price, which `docs/conversations/usage-and-favorites.md:714` already records as a known gap — "No cost, only tokens… nothing stores a price per 1K tokens."

**Solution.** Move the transcript to a new container keyed on a single synthetic partition key `/pk`, whose value is `"{userId}:{conversationId}"`, with one document per message and a `type`-discriminated header document holding the counters. On that clean slate, populate a per-message context cost through a provider-resolved token estimator built on the `Microsoft.ML.Tokenizers` packages the solution already references, store Markdig-rendered HTML at persist time, bound history replay by the model's `ContextWindowSize`, and snapshot prices onto the existing SQL audit trail so cost becomes a query rather than a spreadsheet exercise.

**Success criteria.**

- **A conversation can no longer outgrow its storage.** The largest document a conversation produces is bounded by its largest single message rather than by its message count, measured by the emulator integration test in US-105 writing a 5,000-message transcript and asserting the maximum observed item size does not grow with message count. Target: 0 write failures at 5,000 messages, against today's ceiling of roughly 1.5 MB of accumulated text in one item.
- **Every message carries a context cost.** 100% of transcript messages persisted after EP-3 carry a `tokens` greater than zero for non-empty content and a `tokenAccuracy` of `Estimated` or `Exact`, measured by the US-304 integration test over a multi-turn conversation. Today the figure is 0%, because `Tokens = 0` is written literally.
- **Estimator error is measured, and small where it can be.** On Azure OpenAI turns with zero tool calls, the estimated prompt tokens land within 5% of the provider-reported `inputTokens` at p50 and 15% at p95, measured by the reconciliation metric US-502 emits on the existing `Enterprise.Gpt.Chat` meter (`Api/Observability/TelemetryRegistration.cs`). For Amazon Bedrock and Anthropic the same metric is recorded with no target until US-503's calibration multiplier is tuned from it.
- **History replay stops being unbounded.** 100% of turns are sent with an estimated prompt at or below the selected model's `ContextWindowSize` minus `MaxOutputTokens`, measured by the budget counter US-604 emits; today `ConversationService.cs:651-654` replays every message with no windowing at all. Target: 0 turns sent above the budget.
- **Cost becomes a single-table query.** After EP-7, 100% of new `Core.ConversationUsage` rows carry either both price snapshots or explicit nulls, and a per-conversation cost is computable without joining `[Core.Ref].[Model]`, measured by the reporting query US-704 adds to `docs/conversations/usage-and-favorites.md` §7. The same row also carries the turn's `ContextTokens` after US-306, so billed model spend, billed tool spend, and transcript weight are answerable from one table with no Cosmos read.

## 2. Goals & non-goals

**Goals.**

- Replace the one-document-per-conversation transcript with one document per message under a single synthetic partition key of `{userId}:{conversationId}`, so a conversation's length stops being a storage ceiling and every transcript read is a single-partition query that a foreign user cannot address.
- Separate the two quantities that the word "tokens" currently conflates — a message's **context cost** and a turn's **billed cost** — and track both, at every grain, under one naming rule that keeps a report from adding them together by accident.
- Make the token estimate a measured number rather than an asserted one, by reconciling it against provider-reported usage on the turns where the two are directly comparable.
- Bound what is replayed to the model by the model's own context window, using the per-message tokens as the unit of the budget.
- Give the platform a price, so the audit trail that already answers "how many tokens" can answer "how much money" without joining a catalog that is editable.
- Store a rendered form of each message at persist time, so export is a read rather than a re-render.
- Take the deliberate corrections the clean slate makes free — distinct per-message timestamps, string roles, a real indexing policy, System.Text.Json, a settable `messageCount`, and the removal of the dead `documents[]` array.

**Non-goals.**

- A reporting API or a usage UI. The audit trail stays queryable only in SQL, as `usage-and-favorites.md` §9 already records.
- Per-user quota or budget enforcement. This project makes cost visible; it does not spend it on the user's behalf.
- Archival or pruning of the audit trail. `Core.ConversationUsage` and `Core.ConversationUsageToolCall` continue to grow unbounded.
- Persisting tool calls, tool arguments, tool results, or reasoning into the transcript. Doing so would change what the provider receives on the next turn, which is a behavior change, not a storage change.
- Capturing cached and reasoning tokens from `UsageDetails.AdditionalCounts`. They remain billed and uncaptured on both usage tables.
- Resumable streaming or `Last-Event-ID`. The stream is explicitly not resumable (`docs/conversations/streaming-contract.md` §6.3).
- Migrating or preserving any existing transcript. Legacy data is dropped at cutover; there is no backfill, no dual-read, and no lazy migration.
- Client UI. This project ships API and storage only; rendering per-message tokens and offering an export affordance belong to `docs/prd/enterprise-ui-rebuild.md`.

## 3. Users & access

**Personas.**

- **Chat user**: a signed-in employee holding conversations. Every change here is invisible to them except that long conversations keep working, exports become possible, and a turn is no longer sent with a prompt the model will reject.
- **Administrator**: a user holding `PermissionIds.Administrator` (`a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d`). Owns the model catalog, and gains the price fields on it.
- **Platform analyst**: whoever runs the SQL in `usage-and-favorites.md` §7. The consumer of EP-7's cost columns; has no API of their own and is not getting one here.
- **Platform operator**: runs the cutover in US-207. The only actor who can destroy transcripts, and the only one this document gives a runbook.
- **Backend engineer**: owns every story in this document, working in `enterprise-gpt-api/` under `.claude/rules/csharp.md` and `aspnet-rest-apis.md`.

**Role-based access.**

- **Chat user**: reaches their own conversations through `api/conversations`, which is `.RequireAuthorization()` at the group level and carries no permission filter. Ownership is enforced in `ConversationService`, and another user's conversation is a 404, not a 403 — the new container's partition key makes that stronger rather than weaker, because the key value is built from `(userId, conversationId)` together, so a foreign conversation id addresses a partition that does not exist rather than one the caller has to be filtered out of.
- **Administrator**: the price fields ride the existing admin-gated model routes — `POST api/models`, `PUT api/models/{id}`, and `GET api/models/all`, each already carrying `PermissionEndpointFilter.Require(PermissionIds.Administrator)`. No new permission id is introduced.
- **Export** (EP-8): the same ownership rule as `GET api/conversations/{id}/messages`. Exporting another user's conversation is a 404. No new permission id.
- **Cutover** (US-207): an operator action outside the API surface, gated by configuration rather than by a route, so no HTTP caller can trigger it.

## 4. Functional requirements

| ID | Requirement | Priority | Epic(s) |
| --- | --- | --- | --- |
| FR-1 | Transcripts are stored one document per message in a new container under a single synthetic partition key `/pk` holding `{userId}:{conversationId}` | P0 | EP-1, EP-2 |
| FR-2 | The container is provisioned with an explicit indexing policy that excludes `/content/*` and `/htmlContent/*` | P0 | EP-1 |
| FR-3 | Cosmos configuration binds to a typed options class validated at startup; a missing database or container id fails app start rather than a request | P0 | EP-1 |
| FR-4 | The Cosmos client serializes through System.Text.Json, so the `[JsonPropertyName]` attributes on the document types are the naming contract | P0 | EP-1 |
| FR-5 | `IAzureCosmosService` gains transactional batches, continuation-token paging, and delete-by-partition-key, and sheds the members nothing calls | P0 | EP-1 |
| FR-6 | The storage layer is covered by integration tests against a real Cosmos emulator rather than an in-memory fake | P0 | EP-1, EP-2 |
| FR-7 | A turn's two message documents carry distinct `dateCreated` timestamps — the user message stamped at prompt receipt, the assistant message at completion — so ordering needs neither a counter nor a lock | P0 | EP-2 |
| FR-8 | A completed turn writes both message documents and the header counters in one transactional batch scoped to the conversation's `/pk` | P0 | EP-2 |
| FR-9 | Cancelled and failed turns continue to be billed in SQL and never transcribed | P0 | EP-2 |
| FR-10 | `role` is serialized as a string, matching the SSE wire contract rather than the `ChatRoles` integer | P0 | EP-2 |
| FR-11 | Every transcript read is a single-partition query ordered by `dateCreated`, never a cross-partition scan | P0 | EP-2 |
| FR-12 | `GET api/conversations/{id}/messages` pages a long transcript by `dateCreated` cursor and reports the total | P1 | EP-2 |
| FR-13 | Rename, soft delete, and purge act on the header document; purging a conversation is one delete-by-partition-key, not a query plus N deletes | P0 | EP-2 |
| FR-14 | Cutover is an explicit, gated operator action with a runbook, and it destroys every existing transcript | P0 | EP-2 |
| FR-15 | Every persisted transcript message carries a `tokens` context cost and a `tokenAccuracy` of `Estimated` or `Exact` | P0 | EP-3 |
| FR-16 | Token estimation resolves per provider through a registry that mirrors `Providers.ServiceKeys`, failing the same way `ChatClientResolver` does | P0 | EP-3 |
| FR-17 | The estimator selects `o200k_base` for the models that use it and falls back to `cl100k_base` on an unrecognized deployment name | P0 | EP-3 |
| FR-18 | The estimator adds a configurable per-turn tool-schema overhead and a per-message framing constant, so unattributable prompt tokens are counted | P1 | EP-3 |
| FR-19 | The header's `contextTokens` and `messageCount` stay consistent with the message documents backing them | P1 | EP-2, EP-3 |
| FR-20 | Every persisted message carries server-rendered `htmlContent` produced by Markdig with raw HTML passthrough disabled | P1 | EP-4 |
| FR-21 | The transcript read path returns `tokens`, `tokenAccuracy`, and `htmlContent` per message | P1 | EP-3, EP-4 |
| FR-22 | Estimated context tokens are reconciled against provider-reported usage on zero-tool turns and emitted as metrics | P1 | EP-5 |
| FR-23 | A per-provider calibration multiplier is read from configuration, validated at startup, and applied to the estimate | P2 | EP-5 |
| FR-24 | Replayed history is trimmed to the selected model's `ContextWindowSize` using the per-message tokens | P1 | EP-6 |
| FR-25 | Trimming never drops the system message or the current prompt, and fails the turn rather than sending an over-budget prompt | P1 | EP-6 |
| FR-26 | What the budget dropped is recorded as telemetry | P2 | EP-6 |
| FR-27 | `[Core.Ref].[Model]` carries nullable input and output prices per 1,000,000 tokens, editable by administrators | P1 | EP-7 |
| FR-28 | Prices are snapshotted onto `Core.ConversationUsage` at write time and never joined at read time | P1 | EP-7 |
| FR-29 | A turn's cost is derived from the snapshotted prices as an unmapped computed property with no column of its own | P2 | EP-7 |
| FR-30 | A conversation can be exported as HTML or JSON from its stored `htmlContent` | P2 | EP-8 |
| FR-31 | `Core.Conversation` and `Core.ConversationUsage` carry a `ContextTokens` roll-up beside their billed-token columns, so both quantities are reportable from SQL | P0 | EP-3 |

## 5. Technical considerations

**Integration points.** All verified against the working tree.

| Concern | Where |
| --- | --- |
| Transcript document types | `Enterprise.Gpt.Entity/CosmosConversation.cs` — `CosmosConversation`, `CosmosConversationMessage`, `CosmosConversationUsage`, `CosmosConversationDocument` |
| Cosmos access | `Enterprise.Gpt.Service/AzureCosmosService.cs` — `IAzureCosmosService` and its `Container` wrapper; `MaxPatchOperations = 10` at `:107` |
| Cosmos bootstrap | `Enterprise.Gpt.Api/Program.cs:466-482` (loose `CosmosDb:*` reads, serializer options, service registration) and `:578-581` (`CreateDatabaseIfNotExistsAsync` / `CreateContainerIfNotExistsAsync(cosmosContainerId, "/userId")`), the latter inside the `!IsEnvironment("Testing")` block at `:557` |
| Turn persistence | `ConversationService.PersistTurnAsync` at `:849` — the single `date` at `:851`, the conversation roll-up `ExecuteUpdateAsync` at `:881`, the `ConversationUsage` entity built at `:893`, the `if (!completed) return;` at `:927`, and the transcript patch at `:963-972` |
| History replay | `ConversationService.cs:630` (point read), `:649` (`transcriptExists`), `:651-654` (unbounded replay), `:661` (project instructions, carried onto `ChatOptions.Instructions` at `:665` and assigned at `:1247`) |
| Seeded system message | `ConversationService.cs:213-228`, built by `ConversationPrompts.BuildDefaultSystemPrompt()` |
| Transcript read | `ConversationService.GetConversationMessagesAsync` at `:1050`, behind `GET api/conversations/{id}/messages` in `Api/Endpoints/ConversationEndpoints.cs:45` |
| Tokenizer | `Enterprise.Gpt.Service/Chunking/TokenTextChunker.cs` — `ITextChunker.CountTokens(string)` at `:94`, `CreateTokenizer` at `:438-458`, `FallbackEncoding = "cl100k_base"` at `:41` |
| Tokenizer packages | `Enterprise.Gpt.Service.csproj:42-43` — `Microsoft.ML.Tokenizers` and `Microsoft.ML.Tokenizers.Data.Cl100kBase`, both 2.0.0 |
| Markdown renderer | `Enterprise.Gpt.Service.csproj:27` — `Markdig` 1.3.2, referenced with **zero call sites** in the solution today |
| Provider registry | `Enterprise.Gpt.Dto/Enums/Providers.cs` — three seeded ids and `FrozenDictionary<Guid, string> ServiceKeys`; consumed by `Chat/ChatClientResolver.cs:42-46` |
| Usage collection | `Service/Chat/ChatUsageScope.cs`, `Chat/ChatUsageObserver.cs`, `Chat/UsageReportTranslator.cs` — correct, and unchanged by this project |
| Usage schema | `Enterprise.Gpt.Entity/ConversationUsage.cs` — the billed columns `InputTokens`/`OutputTokens` at `:60`/`:66` and `ToolInputTokens`/`ToolOutputTokens` at `:87`/`:93`, which US-306's `ContextTokens` sits beside, plus `AssistantTokens` at `:98` and `TotalTokens` at `:108`, both unmapped getters; also `ConversationUsageMcpServer.cs`, `ConversationUsageToolCall.cs` |
| Conversation roll-up | `Enterprise.Gpt.Entity/Conversation.cs` — billed `InputTokens`/`OutputTokens` at `:34`/`:36` and the unmapped `TotalTokens` at `:38`, all moved by the `ExecuteUpdateAsync` at `ConversationService.cs:881` |
| Model catalog | `Enterprise.Gpt.Entity/Model.cs` — `[Table(nameof(Model), Schema = "Core.Ref")]`, with `ContextWindowSize` and `MaxOutputTokens` as `decimal(18, 2)` at `:28-32` |
| Migrations | `Enterprise.Gpt.Repository/Migrations/` — `20260811024339_InitialCreate`, `20260813233326_AddModelIsReasoningEnabled`, and `20260814031023_AddAzureAIFoundryProvider`, applied by `Database.Migrate()` at `Program.cs:567`, so a schema story adds an ordinary migration |
| Telemetry | `Api/Observability/TelemetryRegistration.cs` — `Enterprise.Gpt.Chat` registered as both an `ActivitySource` and a `Meter`; documented in `docs/observability/request-logging.md` §7.3 |
| Options pattern | `Enterprise.Gpt.Service/Settings/` — `UserPermissionCacheOptions` is the shape to copy, registered with `AddOptions<T>().Bind(...).Validate(...).ValidateOnStart()` at `Program.cs:498-502` |
| Reference docs | `docs/conversations/usage-and-favorites.md` and `docs/conversations/streaming-contract.md` — read both before touching this area |

**The token model.** "Tokens" is two quantities, and this document keeps them apart by name. One rule governs every token field in the project:

> **`Context*` means estimated, recomputable, and replayed. Every other token field is provider-billed.**

- **Context cost** — what a message contributes to the prompt on every *future* turn. A pure function of the message's own text, deterministic, recomputable, and comparable across roles. This is the `tokens` property on a message document, and it rolls up into the `Context*` fields below.
- **Billed cost** — what a turn actually charged, including tool round trips, agent runs, reasoning, and cached tokens. Only the provider knows it, and it is reported per *turn*, never per message. This is the existing `usage` block on the assistant message plus the SQL audit trail underneath it.

Both are tracked, at all three grains. The naming rule is what stops a report from summing across the two:

| Quantity | Home | Name | Status |
| --- | --- | --- | --- |
| Message context cost | Cosmos message document | `tokens`, `tokenAccuracy` | US-304 |
| Turn context cost | `Core.ConversationUsage` | `ContextTokens` (nullable) | **new** — US-306 |
| Conversation context cost | `Core.Conversation` | `ContextTokens` | **new** — US-306 |
| Conversation context cost (mirror) | Cosmos header document | `contextTokens` | renamed from `totalTokens` |
| Turn billed — model | `Core.ConversationUsage` | `InputTokens`, `OutputTokens` | exists, unchanged |
| Turn billed — tools | `Core.ConversationUsage` | `ToolInputTokens`, `ToolOutputTokens` | exists, unchanged |
| Conversation billed | `Core.Conversation` | `InputTokens`, `OutputTokens`, `TotalTokens` | exists, unchanged |

`ConversationUsage.ContextTokens` is **nullable** on purpose. A cancelled or failed turn is billed in SQL and never transcribed, so it has no context cost at all, and null says "not transcribed" where 0 would say "transcribed nothing" — the same lesson as "Null is not zero" (`usage-and-favorites.md` §3.6). `Conversation.ContextTokens` is not nullable, because a conversation that has never completed a turn genuinely weighs nothing.

**Neither roll-up costs a round trip.** The conversation figure is one added `.SetProperty` on the `ExecuteUpdateAsync` already running at `ConversationService.cs:881`; the turn figure is one more initializer on the `ConversationUsage` entity already being built at `:893`. Both are inside the transaction that is already open.

**The reporting payoff.** One `ConversationUsage` row then carries billed-model tokens, billed-tool tokens, and context tokens for the same turn, so cost, tool overhead, and transcript weight come out of a single-table query — no Cosmos read and no join to `[Core.Ref].[Model]`. That is what the queries in `usage-and-favorites.md` §7 gain from EP-3 and EP-7 together, and US-704's reporting query is where it is written down.

The assistant message's `tokens` is `estimate(final answer)` and deliberately excludes tool spend. Per `streaming-contract.md` §6.2, only `TextDelta` text is transcribed — tool calls, arguments, results, and reasoning are never written to Cosmos and never replayed to the model — so the answer text is literally all that re-enters a future prompt. Folding tool spend into `tokens` would describe a prompt that never gets sent, and tool spend is already attributed per tool in `Core.ConversationUsageToolCall`.

The target message shape:

```jsonc
{
  "role": "assistant",
  "content": "…final answer…",
  "htmlContent": "<p>…</p>",
  "tokens": 412,                    // context cost = estimate(content). Present on every role.
  "tokenAccuracy": "Estimated",     // Estimated | Exact
  "usage": { "inputTokens": 18240, "outputTokens": 1109 }  // billed turn total. Assistant only.
}
```

Four consequences worth stating, because each is counterintuitive:

1. **The system message is not privileged.** It is a seeded transcript message (`ConversationService.cs:213-228`) and goes through the same estimator as user text. What *is* special is project instructions: they ride on `ChatOptions.Instructions` (read at `ConversationService.cs:661`, assigned at `:1247`) rather than as a message, so they are billed but belong to no message.
2. **Tool and function JSON schemas cost input tokens attributable to no message.** Every leased MCP tool's schema is injected into the prompt, and with several servers attached that is easily thousands of tokens. The estimator therefore needs an explicit, configurable per-turn overhead term alongside a small per-message framing constant, or the sum of the messages will always under-report the prompt.
3. **`tokens` on an assistant message will not equal `usage.outputTokens`, and that is correct.** `outputTokens` includes tool-call arguments the model generated that are never transcribed. On a turn with zero tool calls the two *should* agree, and that agreement is the ground-truth signal EP-5 uses to measure estimator accuracy for free.
4. **`ContextTokens` and the billed columns beside it are supposed to disagree, and the gap widens with tool usage.** Tool calls, tool results, and reasoning are billed and never transcribed, so every tool a turn runs pushes the billed figure further above the context figure. The two are not a reconciliation pair outside the zero-tool case EP-5 isolates; a report that treats a divergence as a defect is reading the wrong invariant.

**Multi-provider accuracy.** tiktoken is OpenAI's tokenizer; it undercounts Claude by roughly 15–20% on prose and by more on code and non-English text. Anthropic's `count_tokens` endpoint is exact but is a network round trip per message and does not belong on the write path. The design is therefore a local tiktoken estimate, a per-provider calibration multiplier read from configuration, and a measured accuracy signal (EP-5) so the multipliers are tuned from real traffic rather than guessed. Azure OpenAI is the accurate path today; the resolver is the seam a future provider drops into, exactly as `Providers.ServiceKeys` is the seam for chat clients. Note that `Microsoft.ML.Tokenizers.Data.Cl100kBase` is installed but the modern OpenAI families use `o200k_base`, so adding `Microsoft.ML.Tokenizers.Data.O200kBase` (2.0.0 is published, matching the pinned tokenizer version) is a required story rather than an optimization.

**Data storage and privacy.** Two `type`-discriminated document kinds share the new container, both partitioned on a single synthetic `/pk` whose value is `"{userId}:{conversationId}"`:

```jsonc
// type: "conversation"  — id = conversationId
{ "id", "pk", "userId", "conversationId", "type", "name", "contextTokens", "messageCount",
  "schemaVersion", "dateCreated", "dateModified", "dateDeactivated" }

// type: "message"  — id = messageId, one document per message
{ "id", "pk", "userId", "conversationId", "type", "role", "content", "htmlContent",
  "tokens", "tokenAccuracy", "model", "providerId", "usage", "dateCreated" }
```

`userId` and `conversationId` stay on the documents as their own properties even though the key concatenates them, because a document that can only be read by parsing its partition key is a document no query and no export can project cleanly.

**Why one synthetic key rather than a hierarchical one.** A hierarchical key on `/userId` then `/conversationId` is the obvious alternative and is deliberately rejected, for reasons that are non-obvious enough to be worth recording so the choice is not re-litigated:

- The three justifications a hierarchical key would normally earn are all measured against today's `/userId`-only container, not against a key that already contains the conversation id. Against the synthetic key, each is a wash. Prefix routing is a wash, because a single key value is already one logical partition and one target. The 20 GB logical-partition ceiling is a wash, because [with a hierarchical key the logical partition *is* the full key path](https://learn.microsoft.com/azure/cosmos-db/hierarchical-partition-keys-faq) — 20 GB per `(userId, conversationId)` either way. Delete-by-partition-key is a wash and simpler, because a single key value is trivially a complete key, with no partial-key case to guard.
- The hierarchical key is not neutral: it [co-locates every document sharing the first-level key on one physical partition](https://learn.microsoft.com/azure/cosmos-db/hierarchical-partition-keys#choose-your-hierarchical-partition-keys), which is the deliberate trade it makes to buy cheap `WHERE userId = @x` prefix queries. Nothing in this project ever issues such a query — `Core.Conversation` owns every by-user question, and Cosmos is only ever asked about one conversation at a time. So a heavy user's entire history would land on a single physical partition, capped at 10,000 RU/s until a split that takes four to six hours, in exchange for a query that is never written.
- Microsoft's own guidance points the same way: when the first level has low cardinality and does not appear in queries, use a synthetic key instead. User count here is in the thousands at most, and `userId` appears in no query this project issues.
- The synthetic key keeps the tenancy guarantee **in storage**. A foreign `conversationId` cannot address the partition even if a code-level ownership check is missed, which preserves the property `ConversationService.cs:1053` relies on today — it reads the transcript with the user id as the partition key and carries no SQL ownership check of its own.

`type` is stamped on both document kinds and stays in the read predicate. The container is heterogeneous by design, and the predicate is what stops a future third document kind from silently entering queries written before it existed.

`content` and `htmlContent` hold user prompts and model output, and neither is ever filtered on, so both are excluded from the indexing policy — pure RU and storage cost otherwise. Today no indexing policy is specified at all (`Program.cs:580`), so everything is indexed. Nothing new is stored about a user beyond what the transcript already holds; the message documents carry the same content the array elements did.

**Deliberate changes the clean slate makes free.** Each fixes something real in the current code:

- **Distinct message timestamps, which is what makes `dateCreated` a legitimate sort key.** Today `PersistTurnAsync` computes `DateTimeOffset.UtcNow` once at `ConversationService.cs:851` and stamps both the user message (`:939`) and the assistant message (`:948`) with the identical value; array position is the only thing hiding the tie. Split into documents there is no array, so `ORDER BY dateCreated` would be a guaranteed tie with no deterministic tiebreak — the answer could render before the question, and the wrong order would be replayed to the model on every later turn. The fix is to stamp the user message at prompt receipt and the assistant message at completion. Those are genuinely different moments, seconds apart, and the pair is more truthful in the UI than one shared value ever was; it needs the turn-start time threaded onto the `TurnOutcome` record. A strict-monotonic guard clamps the assistant timestamp to at least the user timestamp plus one tick, so distinctness is an invariant of the write rather than an assumption about how long a turn takes.
- **`role` as a string.** `ChatRoles` is `System = 1, Assistant = 2, User = 3, Tool = 4`; the SSE wire contract already serializes string enum values, and a string keeps an exported document readable.
- **System.Text.Json on the `CosmosClient`.** Today only `CosmosSerializationOptions { PropertyNamingPolicy = CamelCase }` is set (`Program.cs:470-476`), so the SDK's Newtonsoft default is active while the document types carry `[JsonPropertyName]` attributes it never reads. They agree only by coincidence, and the repository already carries a recorded bug from that mismatch — see the comment at `ConversationService.cs:302-305`, where a cross-partition query's predicates did not match the documents' camelCase names and so never selected anything. `CosmosClientOptions.UseSystemTextJsonSerializerWithOptions` exists in the pinned `Microsoft.Azure.Cosmos` 3.62.1.
- **The `documents[]` array is dropped.** It has been dead since inception — nothing ever adds to it, only `Documents = []` at `ConversationService.cs:222`. Conversation documents live in SQL.
- **`messageCount` becomes a real settable property.** Today it is an expression-bodied getter `=> Messages.Count`, which the write path *also* targets with `PatchOperation.Increment("/messageCount", 2)`, so the persisted and in-memory values silently diverge.

**Write path.** One `TransactionalBatch` on the conversation's `/pk` runs exactly three operations — create the user message document, create the assistant message document, patch the header counters — and retires the `TranscriptExists` seed-versus-append branch at `ConversationService.cs:965-967`. There is no pre-allocation step and no extra round trip before it. Because the batch is atomic within the partition, `messageCount` cannot diverge from the documents backing it: either all three land or none do. A batch is scoped to a single partition key and capped at 100 operations and 2 MB, both of which this write is far inside. The rule that cancelled and failed turns are billed in SQL but never transcribed is preserved exactly (`ConversationService.cs:927-930`, rationale at `:843-848`): a truncated answer appended to the transcript would be replayed to the model forever as something the assistant said. The SQL side of the same turn gains one `.SetProperty` on the `ExecuteUpdateAsync` at `:881` and one initializer on the entity at `:893`, both already inside the open transaction.

**Read path.** `SELECT … WHERE c.type = 'message' AND c.conversationId = @id ORDER BY c.dateCreated`, with `QueryRequestOptions.PartitionKey` set to a plain `new PartitionKey(pk)`. `IAzureCosmosService.GetItemsAsync` today drains every page into a `List<T>` with no partition-key option and has no callers; `UpdateItemAsync` and `DeleteItemAsync` are likewise called only from a `DidNotReceive` assertion in the unit tests. The interface needs real paged query support with continuation tokens and a transactional-batch surface; `AzureCosmosService.cs:107` caps a patch at 10 operations, which the new write path must respect.

Lexicographic `ORDER BY` over an ISO-8601 string is chronological only while the format is fixed-width and uniform, and US-102 is swapping the serializer that produces it. The date format is therefore pinned by a converter rather than inherited — see US-102.

**Security.** Cosmos authenticates today with an account-key connection string (`CosmosDb:ConnectionString`, `Program.cs:467`), which is the one place in the API that does not follow the project's `DefaultAzureCredential` preference. This project does not change that — moving to managed identity is a separate decision with its own deployment consequences — but US-101 is written so that the options type has room for it and the open question is recorded in §8. There is also no options class and no startup validation for Cosmos at all today: a missing `CosmosDb:DatabaseId` surfaces as a `null!` bang at `Program.cs:481`, unlike `AmazonBedrockOptions`, `AnthropicOptions`, `DocumentOptions`, `DocumentRetrievalOptions`, and `UserPermissionCacheOptions`, all of which use `ValidateOnStart()`. Markdig runs with raw HTML passthrough disabled so model output — which is attacker-influenceable through uploaded documents and MCP tool results — cannot smuggle markup into a stored artifact.

**Scalability and performance.** Every transcript read is a single-partition query; the acceptance criteria assert that no read is issued without a `/pk` value, so a cross-partition fan-out is a test failure rather than a cost surprise. Because the key is built from both ids, conversations spread evenly across physical partitions no matter how lopsided per-user volume is. Excluding `/content/*` and `/htmlContent/*` from indexing removes the dominant term in write RU, since those two properties are the bulk of a message document's bytes. The context estimator is a pure CPU cost on the write path with a singleton tokenizer, matching how `TokenTextChunker` is already registered — building a tokenizer loads and indexes a large vocabulary and must not happen per request.

**AI system requirements.** The estimator is evaluated, not assumed. The evaluation set is production traffic filtered to turns with zero `ConversationUsageToolCall` rows, where `estimate(prompt) + configured overhead` is directly comparable to the provider's reported `inputTokens`. Pass threshold for Azure OpenAI: within 5% at p50 and 15% at p95 over a rolling 7-day window, at a minimum sample of 200 turns. Amazon Bedrock and Anthropic record the same metric with no pass threshold until US-503 tunes their multipliers; the threshold for those providers is an open question in §8, not an invented number.

## 6. Epics & user stories

| ID | Epic | Goal | Priority | Estimate | Depends on |
| --- | --- | --- | --- | --- | --- |
| EP-1 | Cosmos storage foundation | The transcript container, its client, and its access layer are correct, validated, and provable | P0 | L | — |
| EP-2 | Message-per-document transcript | A conversation's messages are stored, ordered, read, paged, and purged as individual documents | P0 | L | EP-1 |
| EP-3 | Per-message tokenization | Every message carries a context cost the system can compare and budget against, rolled up beside the billed cost in SQL | P0 | M | EP-1 (except US-306, which is SQL only and depends on nothing) |
| EP-4 | Rendered HTML content | Every message carries a safe rendered form, produced once at persist time | P1 | M | EP-1 |
| EP-5 | Estimator accuracy telemetry | The estimate's error against provider truth is measured, and correctable per provider | P1 | M | EP-2, EP-3 |
| EP-6 | Context-window budgeting | A turn's replayed history fits the model that will answer it | P1 | M | EP-2, EP-3 |
| EP-7 | Pricing and cost approximation | The audit trail answers "how much money", not just "how many tokens" | P1 | M | — |
| EP-8 | Conversation export | A user takes a conversation out of the platform as HTML or JSON | P2 | S | EP-2, EP-4 |

EP-1 is entirely `[enabler]` stories. That is a property of replacing a storage layer rather than a decomposition failure: none of them exceeds M, and each names the stories it unblocks. EP-1 and EP-7 touch no common file and start together; EP-3 and EP-4 build their services against no shared surface and run concurrently, with only their wiring stories waiting on EP-2.

Two numbering notes. **EP-2 has no US-202**: it held a sequence allocator that disappeared when message ordering moved to distinct `dateCreated` timestamps, and the number is left unreused so any existing reference to it stays unambiguously dead rather than silently repointed. **US-306 unblocks US-304 and US-305 despite its higher number**: it is the SQL migration EP-3 needs, added after the rest of the epic was written, and appended rather than inserted so every other identifier stays stable. The `Depends on` lines, not the numbering, are the order of work.

### EP-1: Cosmos storage foundation

#### US-101: `[enabler]` Bind and validate the Cosmos configuration at startup

- **Story**: Replace the three loose `builder.Configuration["CosmosDb:*"]` reads with a typed `CosmosOptions` validated at startup, so a misconfigured deployment fails at boot rather than on the first conversation. Unblocks US-102, US-103, and US-104.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Acceptance criteria**:
  - Given `Enterprise.Gpt.Service/Settings/CosmosOptions.cs` bound from the `CosmosDb` section, when the application starts, then it is registered with `AddOptions<CosmosOptions>().Bind(...).Validate(...).ValidateOnStart()`, matching the `UserPermissionCacheOptions` registration at `Program.cs:498-502`.
  - Given configuration with `CosmosDb:DatabaseId` absent, when the application starts, then startup throws an `OptionsValidationException` naming the missing key, and no `null!` bang remains at the former `Program.cs:481`.
  - Given configuration with an empty `CosmosDb:ConnectionString`, when the application starts, then startup fails with a message naming the setting and never the value.
  - Given the `Testing` environment, when the integration host starts, then validation still runs and `CustomWebApplicationFactory`'s fake configuration satisfies it.

#### US-102: `[enabler]` Serialize Cosmos documents with System.Text.Json

- **Story**: Switch the `CosmosClient` to the System.Text.Json serializer so the `[JsonPropertyName]` attributes the document types already carry are the naming contract, rather than agreeing with the Newtonsoft default by coincidence, and pin the date format that message ordering depends on. Unblocks US-201 and US-204.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-101
- **Acceptance criteria**:
  - Given the client registration, when `CosmosClientOptions` is built, then it sets `UseSystemTextJsonSerializerWithOptions` with `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` and a `JsonStringEnumConverter`, and the `CosmosSerializationOptions` block at `Program.cs:470-476` is gone.
  - Given a document type whose property carries a `[JsonPropertyName]` that differs from its camelCased member name, when it is written and read back, then the stored property name is the attribute's value.
  - Given a query whose predicate names a camelCase property, when it runs against a stored document, then it matches — the failure recorded at `ConversationService.cs:302-305` does not recur.
  - Given an enum-typed property, when the document is serialized, then the JSON carries the string name rather than the integer.
  - Given the serializer options, when they are built, then they register a `JsonConverter<DateTimeOffset>` that normalizes every value to UTC and writes it fixed-width as `yyyy-MM-ddTHH:mm:ss.fffffffZ`, because `ORDER BY dateCreated` (FR-11) is a lexicographic string comparison in Cosmos and is chronological only while the format is uniform — and this story is itself changing the formatter that produced it.
  - Given a set of `DateTimeOffset` values differing in offset and in fractional-second precision, when they are serialized and sorted as strings, then the resulting order is identical to sorting them chronologically, asserted by a unit test that is treated as a correctness gate on transcript ordering rather than a formatting nicety.

#### US-103: `[enabler]` Provision the transcript container with a synthetic partition key

- **Story**: Create the new transcript container with a single `/pk` partition key path and an explicit indexing policy, so every read targets one partition and the two largest properties are not indexed. Unblocks US-104 and every story in EP-2.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-101
- **Acceptance criteria**:
  - Given the startup bootstrap, when the container is created, then `ContainerProperties.PartitionKeyPath` is `/pk`, and the container id comes from `CosmosOptions` rather than the old `CosmosDb:ContainerId` literal.
  - Given the container's indexing policy, when it is read back, then `/content/*` and `/htmlContent/*` are excluded paths and `/type`, `/conversationId`, and `/dateCreated` are indexed.
  - Given a container that already exists with a different partition key path, when the bootstrap runs, then it logs an error naming both the expected and the actual path and fails startup rather than silently using the old container.
  - Given the `Testing` environment, when the host starts, then the bootstrap is still skipped, exactly as `Program.cs:557` does today.

#### US-104: `[enabler]` Give the Cosmos service batches, paging, and partition purge

- **Story**: Extend `IAzureCosmosService` with a transactional-batch surface, continuation-token paged queries scoped to a partition key, and delete-by-partition-key, and remove the three members nothing calls. Unblocks US-203, US-204, US-205, and US-206.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-102, US-103
- **Acceptance criteria**:
  - Given the interface, when it is reviewed, then `GetItemsAsync`, `UpdateItemAsync`, and `DeleteItemAsync` are gone — none has a production caller — and a paged `QueryAsync<T>` taking a plain `PartitionKey`, a page size, and a continuation token has replaced them.
  - Given a paged query over 250 matching documents with a page size of 100, when it is drained, then it issues 3 requests and returns 250 items in query order with a null continuation token on the last page.
  - Given a query issued without a partition key, when it is submitted, then the service throws rather than executing a cross-partition scan.
  - Given a transactional batch of 3 operations on one `/pk` value where the second operation fails, when it executes, then no document is created and the returned status names the failing operation index.
  - Given a batch built with more than 100 operations or a patch built with more than the `MaxPatchOperations = 10` the service already enforces at `:107`, when it is submitted, then the service throws before the request leaves the process.

#### US-105: `[enabler]` Prove the storage foundation against a real Cosmos emulator

- **Story**: Stand up the Cosmos DB Linux emulator in Testcontainers alongside the existing SQL Server 2025 container, because transactional batches and cross-document ordered queries are exactly what `FakeAzureCosmosService` cannot prove. Unblocks confidence in US-207, which destroys data. Note that dropping the hierarchical partition key removes this story's central risk: the emulator's known gaps are in hierarchical-key support, and a single-path partition key is the shape it has always handled, so the story is now ordinary integration-test plumbing.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-104
- **Acceptance criteria**:
  - Given `IntegrationTestFixture`, when it starts, then it launches `mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator` alongside the existing `MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest")` container, waits on the emulator's health endpoint, and the Cosmos bootstrap is no longer skipped for these tests.
  - Given the emulator, when a transcript of 5,000 message documents is written with distinct `dateCreated` values and read back, then every read is served from one partition, the results come back ordered by `dateCreated`, and no single document exceeds the size of its own message plus 2 kB of envelope.
  - Given a transactional batch whose second operation violates a unique id, when it executes against the emulator, then no document from that batch is present afterwards.
  - Given two conversations belonging to the same user, when one is purged by partition key, then the other's documents are untouched and a subsequent read returns them in full.
  - Given the emulator container is unavailable, when the test class runs, then it fails with a message naming the emulator rather than timing out silently, and it carries both `[Collection("Integration")]` and `[Trait("Category", "Integration")]` so `dotnet test --filter "Category!=Integration"` still passes without Docker.

### EP-2: Message-per-document transcript

#### US-201: `[enabler]` Define the header and message document types

- **Story**: Replace `CosmosConversation` with a `type`-discriminated header and a per-message document, taking the corrections the clean slate allows. Unblocks every remaining story in this epic.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-102
- **Acceptance criteria**:
  - Given the header type, when it is serialized, then it carries `id`, `pk`, `userId`, `conversationId`, `type: "conversation"`, `name`, `contextTokens`, `messageCount`, `schemaVersion`, `dateCreated`, `dateModified`, and `dateDeactivated`, and `messageCount` is a settable property rather than the `=> Messages.Count` getter it is today.
  - Given the message type, when it is serialized, then it carries `id`, `pk`, `userId`, `conversationId`, `type: "message"`, `role`, `content`, `htmlContent`, `tokens`, `tokenAccuracy`, `model`, `providerId`, `usage`, and `dateCreated`, and carries no sequence field of any kind — ordering is `dateCreated` alone.
  - Given both document types, when a `pk` value is produced anywhere in the solution, then it comes from a single construction site — a `PartitionKeys.For(userId, conversationId)` helper or equivalent — so the `"{userId}:{conversationId}"` format cannot drift between the write path, the read path and the purge path, and a test asserts the helper is the only producer.
  - Given a message with `Role = ChatRoles.Assistant`, when it is serialized, then `role` reads `"assistant"` rather than `2`.
  - Given the new types, when they are reviewed, then neither carries a `documents[]` array and `CosmosConversationDocument` is deleted, because nothing has ever added to it beyond `Documents = []` at `ConversationService.cs:222`.
  - Given `schemaVersion`, when a document is written, then it is stamped with the current version constant, so a future shape change has a discriminator to read.

#### US-203: Persist a completed turn as one transactional batch

- **Story**: As the platform, write a completed turn's user message, assistant message, and header counters in one transaction, so a conversation whose counters moved always has the messages that moved them.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-201, US-104
- **Acceptance criteria**:
  - Given a completed turn, when it persists, then one `TransactionalBatch` on the conversation's `/pk` runs exactly three operations — create the user message document, create the assistant message document, patch `/messageCount`, `/contextTokens` and `/dateModified` on the header — with no preceding allocation call, and the `TranscriptExists` seed-versus-append branch at `ConversationService.cs:965-967` is gone along with the `transcriptExists` flag and its `TurnOutcome` field.
  - Given the batch is atomic within the partition, when it is reviewed, then the story records that `messageCount` cannot diverge from the documents backing it: the counter patch and the document creates either all land or none do, which is what makes the header's count exact rather than advisory.
  - Given a completed turn, when its two message documents are written, then the user message's `dateCreated` is the moment the prompt was received and the assistant message's is the moment the answer completed — two genuinely different values, not the single `DateTimeOffset.UtcNow` computed at `ConversationService.cs:851` and stamped on both today — which requires the turn-start time to be carried on the `TurnOutcome` record.
  - Given a turn that completes within one tick of its prompt, when the two timestamps are written, then the assistant timestamp is clamped to at least the user timestamp plus one tick, so `ORDER BY dateCreated` is total for a turn's own pair regardless of turn duration, asserted by a test that constructs exactly that case.
  - Given a cancelled or failed turn, when finalization runs, then the SQL usage row and the conversation counters are still written and nothing is transcribed, preserving the rule at `ConversationService.cs:927-930`.
  - Given the batch fails, when the error returns, then the existing error log fires naming both the usage row id and the assistant message id, because the usage row already asserts an `AssistantMessageId` that no transcript contains.
  - Given a completed turn, when the assistant message is written, then its `model` holds the `DeploymentName` rather than the display name, keeping the append-only reasoning documented at `ConversationService.cs:932-934`.
  - Given a conversation being created, when the seeded system message is written, then it is a message document whose `dateCreated` precedes every later message, and the header is created in the same batch.

#### US-204: Read a transcript in chronological order

- **Story**: As a user, I want to reopen a conversation and see its messages in the order they happened, so the transcript reads the same way it was written.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-203
- **Acceptance criteria**:
  - Given `GetConversationMessagesAsync`, when it runs, then it issues `SELECT … WHERE c.type = 'message' AND c.conversationId = @id ORDER BY c.dateCreated` with `QueryRequestOptions.PartitionKey` set to `new PartitionKey(pk)` built from the US-201 helper, replacing the point read at `ConversationService.cs:1053`.
  - Given the history replay path, when a turn starts, then it reads the same way rather than point-reading the whole document at `ConversationService.cs:630`, and the system message is still first in the replayed `List<ChatMessage>`.
  - Given the read, when it projects to `ConversationMessageDto`, then system-role messages are still excluded from the API response, as they are today.
  - Given a conversation id belonging to another user, when the read runs, then the `pk` built from the caller's user id and that conversation id addresses a partition holding nothing, the service throws `NotFoundException`, and the caller receives a 404 rather than a 403.
  - Given a conversation whose header exists but which has no message documents, when the read runs, then it returns an empty message list rather than throwing.

#### US-205: Page a long transcript

- **Story**: As a user with a very long conversation, I want the client to load the newest part of it first, so opening the conversation does not require transferring every message ever sent.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-204
- **Acceptance criteria**:
  - Given `GET api/conversations/{id}/messages` with no query parameters, when it runs, then it returns the newest 100 messages in ascending `dateCreated` order plus the conversation's total message count, taken from the header's `messageCount` — which US-203's batch atomicity makes exact — so no Cosmos count query is issued.
  - Given `?take=` outside 1–100, when the request is handled, then `take` is clamped server-side to that range, matching the clamp every other paginated list endpoint applies.
  - Given `?before={dateCreated}`, when the request is handled, then it returns the page of messages immediately preceding that timestamp and reports whether more remain.
  - Given `?before=` with a value that does not parse as a `DateTimeOffset`, when the request is handled, then it returns a 400 `validation-error` problem naming the parameter.
  - Given this changes the default response from "every message" to "the newest 100", when the story ships, then `docs/conversations/streaming-contract.md` and the `enterprise-gpt-ui/` transcript store are named in the story as breaking-change consumers.

#### US-206: Rename, deactivate, and purge a conversation against the header

- **Story**: As a user, I want renaming, deleting, and permanently removing a conversation to keep working, so splitting the document does not cost me a capability I have today.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-203
- **Acceptance criteria**:
  - Given a rename, when it persists, then it patches `/name` and `/dateModified` on the header document only and touches no message document, preserving the behavior at `ConversationService.cs:442` and `:537`.
  - Given a soft delete, when it persists, then it sets `/dateDeactivated` on the header and leaves the message documents in place, preserving the per-id patch loop's behavior at `ConversationService.cs:308`.
  - Given a purge, when it runs, then it issues one `DeleteAllItemsByPartitionKeyStreamAsync` against `new PartitionKey(pk)` rather than a query plus N deletes — a single-path key is trivially complete, so there is no partial-key case to guard.
  - Given a bulk deactivation of 50 conversations, when it runs, then it issues 50 header patches and no message-document reads.

#### US-207: Cut over to the new container as a gated operator action

- **Story**: Retire the old container and its data behind an explicit operator gate with a written runbook, because the cutover destroys every existing transcript and must not be reachable by accident. Depends on the read and write paths existing so the gate can be flipped with a working system on the other side.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-204, US-206
- **Acceptance criteria**:
  - Given a deployment where `CosmosOptions` still names the old container, when the application starts, then it fails startup with a message naming the setting to change, rather than starting against a container whose shape it cannot read.
  - Given the new container is configured and the old one still exists, when the application starts, then it reads and writes only the new container and never opens the old one.
  - Given a conversation created before cutover, when a user opens it afterwards, then its SQL row is intact — name, counters, project, favorite, and every `Core.ConversationUsage` row — and its transcript is empty rather than an error.
  - Given the cutover runbook, when it is followed, then it states in its first paragraph that every existing transcript is destroyed and is not recoverable, and it names the operator action that deletes the old container as a separate, later step.
  - Given the operator skips the old-container deletion, when the system runs, then nothing reads it and it costs only storage, so the destructive step is reversible in scheduling if not in effect.

### EP-3: Per-message tokenization

#### US-301: `[enabler]` Add the o200k_base tokenizer vocabulary

- **Story**: Reference `Microsoft.ML.Tokenizers.Data.O200kBase` 2.0.0 alongside the `Cl100kBase` package already pinned at `Enterprise.Gpt.Service.csproj:43`, because the modern OpenAI families use `o200k_base` and the installed vocabulary cannot count them. Unblocks US-302.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Acceptance criteria**:
  - Given `Enterprise.Gpt.Service.csproj`, when it is restored, then `Microsoft.ML.Tokenizers.Data.O200kBase` resolves at 2.0.0, matching the pinned `Microsoft.ML.Tokenizers` version, with no downgrade warning.
  - Given `TiktokenTokenizer.CreateForEncoding("o200k_base")`, when it is called, then it returns a tokenizer rather than throwing for a missing data file.
  - Given the existing `TokenTextChunker`, when the package is added, then its embedding-model tokenizer selection is unchanged and its tests still pass — this story adds a vocabulary, it does not re-point the chunker.

#### US-302: `[enabler]` Build the provider-keyed token estimator

- **Story**: Add an `ITokenEstimator` and a provider-keyed resolver that mirrors `ChatClientResolver`, so a message's context cost is computed by whichever estimator the model's provider registers. Unblocks US-303, US-304, US-503, and US-601.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-301
- **Acceptance criteria**:
  - Given the resolver, when it is reviewed, then it reads a `FrozenDictionary<Guid, string>` in the shape of `Providers.ServiceKeys` and resolves a keyed `ITokenEstimator`, exactly as `ChatClientResolver.cs:42-46` resolves a keyed `IChatClient`.
  - Given a provider id with no registered estimator, when it is resolved, then it falls back to the tiktoken estimator and logs the provider id once per provider rather than throwing, because a missing estimator must not fail a turn the way a missing chat client must.
  - Given a model whose `DeploymentName` is a deployment alias the tokenizer library does not recognize, when the estimator is built, then it catches `NotSupportedException` and `ArgumentException` and falls back to a configured encoding, matching `TokenTextChunker.CreateTokenizer` at `:438-458`.
  - Given the estimator, when it is registered, then it is a singleton, because building a tokenizer loads and indexes a large vocabulary and must not happen per request.
  - Given the string `""`, when it is estimated, then it returns 0 without touching the tokenizer.

#### US-303: Account for the tokens no message owns

- **Story**: As the platform, add a configurable per-turn overhead and per-message framing constant to the estimate, so the tool schemas and chat framing that cost input tokens but belong to no message are not silently missing from the total.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-302
- **Acceptance criteria**:
  - Given a turn with three MCP servers leased, when the prompt estimate is computed, then it adds a per-turn overhead term derived from the configured per-tool schema cost times the number of attached tools, in addition to the sum of the message estimates.
  - Given the configured per-message framing constant, when a prompt of 10 messages is estimated, then the framing term is applied 10 times.
  - Given project instructions on `ChatOptions.Instructions` (read at `ConversationService.cs:661`, assigned at `:1247`), when the prompt estimate is computed, then their text is estimated into the turn overhead and attributed to no message.
  - Given a configured overhead value below zero, when the application starts, then `ValidateOnStart` fails naming the setting.
  - Given a turn with zero tools and zero project instructions, when the estimate is computed, then the overhead term contributes only the framing constants and nothing else.

#### US-304: Record a context cost on every transcript message

- **Story**: As a user, I want each message in my conversation to carry what it costs to keep, so the platform can budget and I can eventually see it.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-302, US-203, US-306
- **Acceptance criteria**:
  - Given a completed turn, when its two message documents are written, then each carries `tokens` equal to the estimator's count of its own `content`, and the literal `Tokens = 0` at `ConversationService.cs:942` is gone.
  - Given the seeded system message, when it is written at conversation creation, then it carries a `tokens` value produced by the same estimator as user text, with no special-casing.
  - Given a message estimated locally, when it is written, then `tokenAccuracy` reads `Estimated`; given one whose count came from a provider-reported exact figure, then it reads `Exact`.
  - Given the assistant message on a turn with tool calls, when it is written, then its `tokens` counts only the transcribed answer text and does not equal `usage.outputTokens`, and a test asserts that inequality rather than treating it as a defect.
  - Given a completed turn, when the SQL side is written, then the turn's two message `tokens` are summed into `ConversationUsage.ContextTokens` on the entity already built at `ConversationService.cs:893`, and added to `Conversation.ContextTokens` by one added `.SetProperty` on the `ExecuteUpdateAsync` already running at `:881` — no additional round trip in either case.
  - Given a cancelled or failed turn, when the usage row is written, then `ContextTokens` is null rather than 0, because the turn was billed and never transcribed, and a test asserts null rather than zero.
  - Given the estimator throws, when a turn persists, then the message is still written with `tokens` of 0 and `tokenAccuracy` of `Estimated`, and the failure is logged — a tokenizer fault must not lose a turn that has already streamed.

#### US-305: Keep the header's context roll-up honest

- **Story**: As the platform, keep the header's `contextTokens` and `messageCount` consistent with the message documents beneath them and with the SQL roll-up beside them, so a counter can be trusted without reading the whole transcript.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-304, US-306
- **Acceptance criteria**:
  - Given a completed turn, when the batch patches the header, then `contextTokens` moves by the sum of the turn's two message `tokens` — the **context** cost, explicitly not the billed input-plus-output figure written to `Core.ConversationUsage` — and `messageCount` moves by exactly the number of message documents created.
  - Given the `contextTokens` field, when its documentation is written, then it states that this figure and the conversation's billed `TotalTokens` are *supposed* to disagree, and that the gap widens the more tools a conversation runs, because tool calls, tool results and reasoning are billed and never transcribed. A divergence is the model working, not a defect to reconcile.
  - Given a conversation of 20 turns, when its header is read, then `contextTokens` equals the sum of the `tokens` on its message documents and equals `Core.Conversation.ContextTokens` for the same conversation, both asserted against the emulator.
  - Given the same conversation, when its header is read, then `messageCount` equals the count of its message documents exactly — guaranteed rather than approximated, because US-203's batch is atomic within the partition.
  - Given a turn that is billed but not transcribed, when finalization runs, then `messageCount` and `contextTokens` do not move, `ConversationUsage.ContextTokens` is null, and the billed SQL counters do move — preserving today's split.

#### US-306: `[enabler]` Add the context-token columns to SQL

- **Story**: Add `ContextTokens` to `Core.Conversation` and `Core.ConversationUsage` in a real EF migration, so the transcript's weight is reportable from the same row that already holds what the turn was billed. Unblocks US-304 and US-305.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Acceptance criteria**:
  - Given `Enterprise.Gpt.Entity/Conversation.cs`, when it is updated, then it carries a non-nullable `long ContextTokens` alongside the billed `InputTokens` at `:34` and `OutputTokens` at `:36`, and the unmapped `TotalTokens` getter at `:38` is left computing the billed figure only — the two quantities never merge into one member.
  - Given `Enterprise.Gpt.Entity/ConversationUsage.cs`, when it is updated, then it carries a **nullable** `long? ContextTokens`, because a cancelled or failed turn is billed and never transcribed: null means "not transcribed" where 0 would mean "transcribed nothing", per "Null is not zero" (`usage-and-favorites.md` §3.6). The property's documentation says so.
  - Given the change, when `dotnet ef migrations add` is run, then a new migration lands in `Enterprise.Gpt.Repository/Migrations/` alongside `20260811024339_InitialCreate` and the migrations that follow it, and `Database.Migrate()` at `Program.cs:567` applies it at startup. This is the second schema story in this project, beside US-701; both apply through the same startup call and the cutover runbook covers them together.
  - Given existing `Core.Conversation` rows, when the migration is applied, then `ContextTokens` is 0 with no backfill question to answer, because US-207's cutover destroys every existing transcript — 0 is the true weight of a conversation with no transcript.
  - Given existing `Core.ConversationUsage` rows, when the migration is applied, then `ContextTokens` is null and no default backfills it to zero, for the same reason the price columns in US-703 stay null.
  - Given both unit and integration test harnesses, when they build the schema from the EF model, then the new columns exist without any additional fixture change.

### EP-4: Rendered HTML content

#### US-401: `[enabler]` Build the Markdig rendering pipeline

- **Story**: Configure a `MarkdownPipeline` with raw HTML passthrough disabled, giving Markdig — referenced at `Enterprise.Gpt.Service.csproj:27` with zero call sites today — its first use. Unblocks US-402 and EP-8.
- **Priority**: P1 · **Estimate**: M · **Depends on**: —
- **Acceptance criteria**:
  - Given the pipeline, when it renders `<script>alert(1)</script>` inside markdown, then the output contains the escaped text and no `<script>` element.
  - Given markdown containing `<img src=x onerror=alert(1)>`, when it is rendered, then the output carries no `onerror` attribute and no raw `img` element sourced from the input.
  - Given a fenced code block, when it is rendered, then the output is a `<pre><code>` pair with the language recorded as a class and the block's contents HTML-escaped.
  - Given a `javascript:` link target, when it is rendered, then the resulting anchor does not carry that scheme in its `href`.
  - Given the pipeline, when it is registered, then it is a singleton built once, because `MarkdownPipelineBuilder` work is per-process configuration rather than per-request.

#### US-402: Store rendered HTML with every message

- **Story**: As a user, I want the platform to keep a rendered form of each message, so exporting my conversation does not depend on a client re-rendering it.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-401, US-203
- **Acceptance criteria**:
  - Given a completed turn, when its message documents are written, then each carries `htmlContent` produced by the US-401 pipeline from its own `content`.
  - Given a user prompt containing markdown syntax, when it is persisted, then it is rendered by the same pipeline as assistant text, so an export needs one rendering path rather than two.
  - Given the renderer throws on a pathological input, when the turn persists, then `htmlContent` is written as the HTML-escaped plain text of `content` and the failure is logged, so a rendering fault never loses a turn.
  - Given `htmlContent` is stored, when the container's indexing policy is read, then `/htmlContent/*` is an excluded path, so the added bytes cost storage but not index RU.

#### US-403: Serve the stored HTML and token fields on the transcript

- **Story**: As a client, I want the transcript response to carry each message's rendered HTML, context cost, and accuracy, so I can render and display them without recomputing anything.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-402, US-204, US-304
- **Acceptance criteria**:
  - Given `GET api/conversations/{id}/messages`, when it responds, then each `ConversationMessageDto` carries `htmlContent`, `tokens`, and `tokenAccuracy` alongside the existing `text` and `role`.
  - Given a message persisted before EP-3 and EP-4 wiring, when it is returned, then `htmlContent` is null and `tokens` is 0, and the DTO's documentation states that a null `htmlContent` means the client must render `text` itself.
  - Given a client that ignores the new fields, when it reads the response, then the existing `text` and `role` are unchanged in name, type, and meaning.
  - Given the response, when it is inspected, then `tokenAccuracy` serializes as the string `"Estimated"` or `"Exact"`, matching the string-enum convention the SSE contract already uses.

### EP-5: Estimator accuracy telemetry

#### US-501: Reconcile the estimate against provider truth on zero-tool turns

- **Story**: As the platform, compare the estimated prompt against the provider's reported `inputTokens` on turns where no tool ran, because that is the only condition under which the two measure the same thing.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-304, US-203
- **Acceptance criteria**:
  - Given a completed turn whose `UsageReportTranslator` output contains zero tool calls, when finalization runs, then it computes the signed relative error between the estimated prompt and the reported `inputTokens`.
  - Given a turn with one or more tool calls, when finalization runs, then no reconciliation is recorded, because the reported `inputTokens` includes prompts the estimator never saw.
  - Given a turn with no usage report at all — the `turn.Report is null` path already logged at `ConversationService.cs:857-866` — when finalization runs, then no reconciliation is recorded and the existing warning is unchanged.
  - Given reconciliation runs, when it completes, then it adds no database round trip and no provider call to the turn.
  - Note, changing no criterion here: once US-306 lands, `ContextTokens` and `InputTokens` sit on the same `ConversationUsage` row, so estimator error on zero-tool turns is also answerable as a SQL query over any past window. The metric below covers a rolling window only; the row covers history.

#### US-502: Emit estimator accuracy as metrics

- **Story**: As a platform operator, I want the estimator's error visible in Application Insights, so a provider whose estimate has drifted shows up as a metric rather than as a support ticket.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-501
- **Acceptance criteria**:
  - Given a reconciled turn, when the metric is emitted, then it is a histogram on the existing `Enterprise.Gpt.Chat` meter, tagged with the provider id and the deployment name, so it exports through `TelemetryRegistration.AddEnterpriseTelemetry` with no new meter registration.
  - Given the metric, when it is queried for Azure OpenAI over a 7-day window with at least 200 samples, then p50 relative error is within 5% and p95 within 15%.
  - Given no Application Insights connection string is configured, when a turn reconciles, then the metric is still recorded to the meter and the distro is still skipped, matching the behavior documented in `docs/observability/request-logging.md` §7.1.
  - Given the metric's tags, when they are inspected, then no conversation id, user id, or message content appears on any dimension.
  - Note, changing no criterion here: this histogram is the live signal and covers a rolling window. The durable one is the `ContextTokens`/`InputTokens` pair US-306 puts on every usage row, which answers the same question over any window that is still in the table.

#### US-503: Calibrate the estimate per provider

- **Story**: As a platform operator, I want to correct a provider's estimate with a multiplier read from configuration, so a known undercount can be fixed without a deployment of new code.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-302, US-502
- **Acceptance criteria**:
  - Given a configured multiplier for a provider id, when a message is estimated for a model of that provider, then the raw tiktoken count is multiplied by it before being stored.
  - Given no multiplier is configured for a provider, when a message is estimated, then the multiplier defaults to 1.0 and the estimate is the raw count.
  - Given a multiplier at or below zero, or above a configured sane ceiling, when the application starts, then `ValidateOnStart` fails naming the provider id and the value.
  - Given a multiplier is changed, when the US-502 metric is read afterwards, then the recorded error reflects the calibrated estimate, so the tuning loop closes on its own signal.

### EP-6: Context-window budgeting

#### US-601: `[enabler]` Build the context budget calculator

- **Story**: Add a pure calculator that, given a model and a candidate message list, returns the messages that fit and those that do not, so the trimming decision is testable without a chat client. Unblocks US-602, US-603, and US-604.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-302, US-303
- **Acceptance criteria**:
  - Given a model with `ContextWindowSize` of 128000 and `MaxOutputTokens` of 4096, when the budget is computed, then the available prompt budget is 123904 minus the configured turn overhead.
  - Given a message list whose summed `tokens` plus overhead exceeds the budget, when it is evaluated, then the calculator drops the oldest non-system messages until it fits and reports how many it dropped and how many tokens they held.
  - Given a message list that already fits, when it is evaluated, then it returns the list unchanged and reports 0 dropped.
  - Given a model whose `ContextWindowSize` is 0 — the seeded default for a catalog row nobody has filled in — when the budget is computed, then the calculator treats the model as unbounded, logs once per model, and drops nothing.
  - Given the calculator, when it is tested, then it runs with no `EnterpriseGptDbContext`, no chat client, and no Cosmos dependency.

#### US-602: Trim replayed history to the model's context window

- **Story**: As a user with a long conversation, I want the platform to send only what fits, so my next turn is answered instead of rejected.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-601, US-204
- **Acceptance criteria**:
  - Given a conversation whose messages exceed the selected model's budget, when a turn starts, then the replay built at `ConversationService.cs:651-654` carries only the messages the calculator kept.
  - Given a conversation that fits, when a turn starts, then every message is replayed, exactly as today.
  - Given a user switches from a large-context model to a small-context one mid-conversation, when the next turn starts, then the budget is recomputed against the newly selected model rather than the previous one.
  - Given trimming occurs, when the turn persists, then the transcript is untouched — trimming decides what is *sent*, never what is *stored*.
  - Given messages with `tokens` of 0 because they predate US-304, when the budget is computed, then they are estimated on the fly rather than counted as free.

#### US-603: Never drop the system message or the current prompt

- **Story**: As a user, I want my current question and the assistant's standing instructions to survive any trimming, so a long conversation does not silently change how the assistant behaves.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-602
- **Acceptance criteria**:
  - Given any trimming outcome, when the replay is built, then the system message is present and first.
  - Given any trimming outcome, when the replay is built, then the current user prompt is present and last.
  - Given a system message and current prompt that together exceed the budget on their own, when the turn starts, then it fails before contacting the provider with a `validation-error` problem naming the model's context window, rather than sending a prompt that will be rejected.
  - Given trimming drops a user message, when the replay is built, then the assistant message that answered it is dropped with it, so the model never sees an answer to a question that is no longer there.

#### US-604: Record what the budget dropped

- **Story**: As a platform operator, I want trimming to be visible, so a conversation quietly losing its early context shows up as a number rather than as a user complaint.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-602
- **Acceptance criteria**:
  - Given a trimmed turn, when it starts, then a counter on the `Enterprise.Gpt.Chat` meter records the messages dropped and the tokens they held, tagged with the model's deployment name.
  - Given an untrimmed turn, when it starts, then the counter is not incremented, so the metric measures trimming rather than traffic.
  - Given the counter's tags, when they are inspected, then no conversation id, user id, or message content appears on any dimension.
  - Given 100% of turns across a window, when the estimated prompt is compared to the model's budget, then none exceeds it, which is the success criterion this metric measures.

### EP-7: Pricing and cost approximation

#### US-701: `[enabler]` Add nullable prices to the model catalog

- **Story**: Add nullable input and output price columns to `[Core.Ref].[Model]` in a real EF migration, so the catalog can say what a model costs. Unblocks US-702, US-703, and US-704.
- **Priority**: P1 · **Estimate**: M · **Depends on**: —
- **Acceptance criteria**:
  - Given `Enterprise.Gpt.Entity/Model.cs`, when it is updated, then it carries nullable `InputPricePerMillionTokens` and `OutputPricePerMillionTokens` as `decimal(18, 6)`, alongside the existing `decimal(18, 2)` `ContextWindowSize` and `MaxOutputTokens`.
  - Given the change, when `dotnet ef migrations add` is run, then a new migration lands in `Enterprise.Gpt.Repository/Migrations/` alongside the three that already exist and `Database.Migrate()` at `Program.cs:567` applies it at startup — this is one of the two schema stories in this project, beside US-306, and `docs/conversations/usage-and-favorites.md` §6.5 is stale in saying the folder is empty.
  - Given an existing model row, when the migration is applied, then both prices are null rather than zero, so "unpriced" stays distinguishable from "free".
  - Given the migration is applied to a SQL Server instance below 2025, when startup runs, then it fails on `UseCompatibilityLevel(170)` as it already does, and the runbook says so.
  - Given both unit and integration test harnesses, when they build the schema from the EF model, then the new columns exist without any additional fixture change.

#### US-702: Manage model prices as an administrator

- **Story**: As an administrator, I want to set what a model costs per million tokens, so the platform's cost figures reflect the contract we are actually on.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-701
- **Acceptance criteria**:
  - Given `POST api/models` and `PUT api/models/{id}`, when their action DTOs are updated, then both accept nullable input and output prices, and both routes keep their existing `PermissionEndpointFilter.Require(PermissionIds.Administrator)` filter with no new permission id introduced.
  - Given a non-administrator, when they submit a price, then the request returns a 403 `permission-required` problem naming `Administrator`.
  - Given a negative price, when it is submitted, then the FluentValidation validator rejects it and the response is a 400 `validation-error` whose `errors` dictionary is keyed by the failing property name.
  - Given a price omitted from the request body, when the model is updated, then the stored price becomes null, matching the full-representation `PUT` semantics the rest of the catalog already uses.
  - Given `GET api/models/all`, when an administrator reads it, then `ModelDto` carries both prices; given `GET api/models`, then the prices are present too, since a chat user's client may display them.

#### US-703: Snapshot prices onto the usage row

- **Story**: As a platform analyst, I want the price that applied at the time of a call stored on the call, so editing the catalog does not rewrite history.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-701
- **Acceptance criteria**:
  - Given a turn being recorded, when the `ConversationUsage` row is written, then it carries the model's two prices copied from the catalog, for the same reason `DeploymentName` is copied (`usage-and-favorites.md` §3.2).
  - Given an administrator changes a model's price after a turn, when that turn's usage row is read, then it still holds the old price and a join to `[Core.Ref].[Model]` would produce a different answer — which is the point.
  - Given a model with no price set, when the usage row is written, then both price columns are null rather than zero, so an unpriced call is distinguishable from a free one, matching "Null is not zero" (`usage-and-favorites.md` §3.6).
  - Given a naming call — `ConversationUsageKinds.Naming`, written at `ConversationService.cs:422-435` — when its usage row is written, then it carries the same price snapshot as a chat row.
  - Given the migration, when it is applied to a database with existing usage rows, then those rows' prices are null and no default backfills them to zero.

#### US-704: Derive a turn's cost from its snapshot

- **Story**: As a platform analyst, I want a turn's cost available as a computed value and as a reporting query, so money is one read rather than a spreadsheet.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-703
- **Acceptance criteria**:
  - Given `ConversationUsage`, when the cost member is added, then it is an unmapped expression-bodied getter with no column, following `AssistantTokens` at `:98` and `TotalTokens` at `:108` and the reasoning in `usage-and-favorites.md` §3.5.
  - Given a row with both prices set, when cost is computed, then it equals `(InputTokens + ToolInputTokens) / 1000000 * InputPrice + (OutputTokens + ToolOutputTokens) / 1000000 * OutputPrice`, and the member's documentation states that tool tokens are priced at the driving model's rate because nothing in a tool call names the model behind it.
  - Given a row with either price null, when cost is computed, then it returns null rather than a partial figure.
  - Given `ContextTokens` is on the same row after US-306, when the cost member and the reporting query are written, then neither uses it in any arithmetic that produces money, and the member's documentation says so explicitly: cost is what the provider reported it billed, never what the transcript weighs. `ContextTokens` is reportable beside cost, never as an input to it.
  - Given the reporting query added to `usage-and-favorites.md` §7, when it runs over a date range, then it produces per-conversation and per-model cost touching only `Core.ConversationUsage`, with no join to the catalog, and reports billed-model tokens, billed-tool tokens and `ContextTokens` as three separate columns so the difference between what was charged and what is replayed is visible rather than inferred.

### EP-8: Conversation export

#### US-801: Export a conversation as HTML

- **Story**: As a user, I want to download one of my conversations as a self-contained HTML file, so I can keep or share it outside the platform.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-403
- **Acceptance criteria**:
  - Given `GET api/conversations/{id}/export?format=html`, when a user requests their own conversation, then the response is a `text/html` document assembled from the stored `htmlContent` of every non-system message, in `dateCreated` order, with the conversation name as its title.
  - Given a conversation belonging to another user, when the export is requested, then the response is a 404 `resource-not-found` problem, matching how every other conversation route treats a foreign id.
  - Given `?format=` with an unsupported value, when the export is requested, then the response is a 400 `validation-error` problem naming the supported formats.
  - Given a message whose `htmlContent` is null, when it is exported, then its `content` is HTML-escaped into the output rather than omitted.
  - Given a conversation with no messages, when it is exported, then the response is a valid HTML document with an empty body section rather than a 404.

#### US-802: Export a conversation as JSON

- **Story**: As a user, I want the same export as structured JSON, so I can process my conversation with a tool instead of reading it.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-801
- **Acceptance criteria**:
  - Given `GET api/conversations/{id}/export?format=json`, when it responds, then the body carries the conversation's name and dates plus an array of messages in `dateCreated` order, each with `role` as a string, `content`, `htmlContent`, `tokens`, `tokenAccuracy`, `model`, and `dateCreated`.
  - Given the JSON export, when it is compared to the stored documents, then the property names match the message document's `[JsonPropertyName]` values, so the export is the storage shape rather than a third representation.
  - Given a conversation belonging to another user, when the export is requested, then the response is a 404 `resource-not-found` problem.
  - Given an assistant message with a `usage` block, when it is exported, then the block is present with `inputTokens` and `outputTokens` and no per-tool breakdown, because that data lives in SQL and is not exposed over HTTP.

## 7. Milestones & rollout

**Epic dependency and parallelism.**

| Wave | Epics | May run concurrently with | Blocked until |
| --- | --- | --- | --- |
| 1 | EP-1, EP-7 | each other — no shared file | — |
| 2 | EP-2, EP-3, EP-4 | each other; EP-7 continues alongside | EP-1 (EP-3 and EP-4 build their services independently — US-301, US-302, US-303 and US-306 need nothing from EP-2; only US-304, US-305, US-402, and US-403 wait on it) |
| 3 | EP-5, EP-6 | each other; EP-7 continues alongside | EP-2 and EP-3 |
| 4 | EP-8 | — | EP-2 and EP-4 |

**Phases**, derived from that graph.

| Phase | Contents | Relative estimate |
| --- | --- | --- |
| **P1 — Foundation and pricing** | EP-1 in full (US-101 … US-105) and EP-7 in full (US-701 … US-704). The two share no file: EP-1 is Cosmos and `Program.cs`, EP-7 is SQL and the model catalog | ~1.5 weeks |
| **P2 — The new transcript** | EP-2 US-201, US-203, US-204 and US-206 (there is no US-202), EP-3 US-306 then US-301 … US-304, EP-4 US-401 … US-403. **This is the minimum viable release** — the point at which a conversation is stored per message with a context cost and a rendered form, and the cutover becomes possible. Slightly lighter than before: the sequence allocator is gone and the migration that replaces it is an S | ~2.5 weeks |
| **P3 — Cutover** | EP-2 US-207, gated and run by an operator against a non-production environment first. Nothing else lands in this phase; it is a deployment step with a runbook | ~2 days |
| **P4 — Budgeting and measurement** | EP-6 US-601 … US-603, EP-5 US-501 and US-502, EP-3 US-305, EP-2 US-205 | ~1.5 weeks |
| **P5 — Tuning and export** | EP-5 US-503, EP-6 US-604, EP-8 US-801 and US-802 | ~1 week |

EP-7 is scheduled first precisely because it depends on nothing: it is the only epic that can absorb slack while EP-1 is being proved against the emulator, and it is the only one whose value — a price on the catalog — is deliverable without any Cosmos work at all.

**Risks & mitigations.**

| Risk | Mitigation |
| --- | --- |
| The cutover destroys every existing transcript, irreversibly | US-207 makes it a gated operator action with a runbook whose first paragraph states the loss, and it is its own phase so it is never bundled with a feature deploy. The old container is retired in a separate later step, so scheduling is reversible even though the data is not |
| Transactional batches and ordered cross-document queries are unproven here — the only Cosmos coverage today is `FakeAzureCosmosService`, which explicitly does not interpret patch operations | US-105 stands up the real Linux emulator in Testcontainers before US-207 destroys anything, and its criteria target exactly those two things a fake cannot prove. This risk shrank when the partition key became a single synthetic path: the emulator's documented gaps are in hierarchical-key handling, which this design no longer uses |
| `DeleteAllItemsByPartitionKeyStreamAsync` is a background operation that may require account-level enablement | Enablement is an open question in §8, independent of key shape; the fallback is a paged id query plus batched deletes, which the US-104 surface already supports |
| Message ordering rests on wall-clock timestamps, so a clock that jumps backwards between two turns could interleave them | US-203's clamp makes a turn's own pair strictly ordered, and the deployment is single-replica (§8), so there is one clock. A scale-out reopens this — recorded as an assumption rather than solved here |
| US-205 changes the transcript endpoint's default from "every message" to "the newest 100", which is a breaking change for `enterprise-gpt-ui/` | The story names the consumer explicitly and is P1 rather than P0, so it lands after the client's transcript store exists rather than under it |
| The tiktoken estimate undercounts Claude by 15–20% on prose and more on code, so a budget built on it could still overflow on Bedrock and Anthropic | EP-5 measures the error before EP-6 relies on it, and US-503's multiplier is the correction. Until it is tuned, the §5 pass threshold applies to Azure OpenAI only, and the non-OpenAI threshold is an open question rather than an asserted number |
| Cost is an approximation by construction, because tool tokens can only be priced at the driving model's rate | Stated on the computed member itself in US-704 and recorded as an assumption in §8, so no report is built on it believing otherwise |
| Two independent schema stories — US-306 and US-701 — both apply through `Database.Migrate()` at startup, so a deployment carrying one and not the other is possible | Both are P0/P1 enablers scheduled inside the same phases their dependents sit in, both are additive nullable-or-defaulted columns that a partial deployment tolerates, and each story's criteria include applying its migration against a real SQL Server 2025 container so the compatibility-level failure mode is named in the runbook rather than discovered in production |
| Switching the Cosmos serializer changes the JSON contract for every document type at once | US-102 lands before any document type changes, and its criteria assert the attribute-driven names round-trip and that the query-predicate mismatch recorded at `ConversationService.cs:302-305` does not recur |

**Rollout & rollback.** The cutover is the only irreversible step, and it is gated by configuration: until `CosmosOptions` names the new container, the application refuses to start rather than running against a shape it cannot read, so a half-configured deployment fails loudly at boot instead of quietly at the first turn. Every epic after EP-2 is additive and independently deployable — EP-3, EP-4, EP-5, EP-6, and EP-8 each add fields, metrics, or routes without changing an existing contract, so any one of them can be backed out by reverting its own deployment. Both SQL stories are additive: US-306 adds one defaulted column and one nullable one, US-701 adds two nullable ones and an unmapped getter, and `DROP COLUMN` is the back-out for either. Backing out US-306 costs the context roll-up and nothing else — the per-message `tokens` that feed it live in Cosmos and survive. The back-out for EP-6 specifically is a configuration switch that sets every model's effective budget to unbounded, restoring today's unbounded replay without a deployment.

## 8. Assumptions & open questions

**Assumptions.**

- Prices are stored per **1,000,000** tokens rather than per 1,000, because current provider price sheets are quoted that way; the gap note at `usage-and-favorites.md:714` says "per 1K tokens" and is being deliberately departed from.
- A single currency is assumed, and it is USD. No currency column is added, and a multi-currency deployment would need one.
- Price columns are **nullable**, so "unpriced" stays distinguishable from "free" — the same lesson as "Null is not zero" (`usage-and-favorites.md` §3.6).
- Tool tokens can only be priced at the **driving model's** rate. Nothing in a tool call names the model behind it: `ConversationUsageToolCall.ModelId` is null on every row this application can write (`usage-and-favorites.md:715`). Cost is therefore an approximation by construction, which matches the stated goal.
- `htmlContent` is an **export-fidelity artifact**, not a byte-identical mirror of what the UI shows. The client renders through `ngx-markdown` over marked and Prism; the two will differ in whitespace, syntax-highlighting markup, and extension support.
- `htmlContent` is rendered for **every** persisted message, including user prompts, so an export has one rendering path rather than two.
- Recreating the container **destroys every existing transcript**. US-207 makes that an explicit, gated operator action with a runbook; the SQL side — conversations, usage rows, tool-call trees, favorites, projects — survives untouched.
- The deployment is **single-replica**, so a wall-clock timestamp is a sufficient ordering key for a conversation's messages: one process, one clock, and `IConversationLockService` — process-local by its own interface documentation (`IConversationLockService.cs:9-13`) — genuinely serializes a conversation's turns. This is the assumption Decision 2's ordering rests on, and it is the one to challenge first.
- A future **scale-out re-opens message ordering as a design question**, and the lock does not close it: it gives no mutual exclusion across replicas, so two replicas could stamp two turns from two clocks. The answers available then are a Cosmos-side monotonic value, a distributed lock, or a tiebreak column — none of which is worth building for a single replica, and none of which the current design forecloses, since a tiebreak is an added indexed property rather than a re-partition.
- `docs/conversations/usage-and-favorites.md` §6.5 is **stale** in stating that `Enterprise.Gpt.Repository/Migrations/` is empty. Three migrations exist, so US-306 and US-701 each add an ordinary one and the doc needs correcting when it is next revised.
- `ContextTokens` is an **estimate on both tables**, not a measurement, and inherits every limitation of the estimator behind it — including the per-provider undercount EP-5 quantifies. It is comparable across conversations because it is computed the same way for all of them; it is not a figure to bill against, which US-704 states on the cost member itself.
- The per-turn tool-schema overhead is modeled as a configured cost **per attached tool**, not per server and not by serializing each schema and counting it. A schema-accurate count is more work than the accuracy is worth until EP-5 says otherwise.
- Cosmos continues to authenticate with an account-key connection string. Moving to `DefaultAzureCredential` is the project standard and is deliberately out of scope here, because it is a deployment change with its own rollout.
- `ChatRoles.Tool` remains unused in the transcript. Tool messages are never transcribed today and this project does not start; the enum value stays because the enum is shared.

**Open questions.**

- What accuracy threshold should Amazon Bedrock and Anthropic estimates be held to once US-503's multipliers are tuned? The Azure OpenAI threshold in §5 is derivable because tiktoken is that provider's own tokenizer; the other two need a number chosen from the US-502 data. **Owner**: platform owner, after two weeks of US-502 metrics.
- Is delete-by-partition-key enabled on the target Cosmos accounts? It is a background operation that has historically required account-level opt-in. If it is not, US-206 falls back to a paged id query plus batched deletes. **Owner**: platform operator, before US-206 starts.
- What is the retention policy for a purged conversation's SQL audit rows? Today nothing prunes `Core.ConversationUsage` and purging a conversation does not touch it, deliberately. Now that a conversation's transcript can be destroyed in one call, the mismatch between the two is more visible. **Owner**: platform owner.
- Should prices be versioned with an effective date rather than snapshotted per row? Snapshotting answers "what did this call cost"; a price history would additionally answer "what would last quarter cost at today's rates". **Owner**: platform analyst.
- Should the estimator use the Anthropic `count_tokens` endpoint for an `Exact` accuracy on a background reconciliation pass, rather than only on the write path where it does not belong? **Owner**: backend engineer, after EP-5 quantifies the error.
