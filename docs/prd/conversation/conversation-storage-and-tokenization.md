# PRD: Conversation Storage and Tokenization

## 1. Overview

**Problem.** A conversation transcript is one Cosmos DB document with every message embedded in a `messages[]` array (`Enterprise.Gpt.Entity/CosmosConversation.cs`). Each turn appends two array entries (`ConversationService.cs:912-921`) and point-reads the whole document to replay history (`ConversationService.cs:605`). Cosmos caps an item — and a request — at 2 MB, so a conversation that is used long enough becomes unwritable, and the default indexing policy indexes every element of the growing array along the way. Three gaps ride on top of that shape: `CosmosConversationMessage.Tokens` is on the wire but hardcoded to `0` (`ConversationService.cs:891`) and never assigned anywhere, so no per-message token attribution exists; nothing stores rendered HTML, so exporting a conversation means re-rendering markdown from scratch; and nothing stores a price, which `docs/conversations/usage-and-favorites.md:714` already records as a known gap — "No cost, only tokens… nothing stores a price per 1K tokens."

**Solution.** Move the transcript to a new container keyed hierarchically on `/userId` then `/conversationId`, with one document per message and a `type`-discriminated header document holding the counters. On that clean slate, populate a per-message context cost through a provider-resolved token estimator built on the `Microsoft.ML.Tokenizers` packages the solution already references, store Markdig-rendered HTML at persist time, bound history replay by the model's `ContextWindowSize`, and snapshot prices onto the existing SQL audit trail so cost becomes a query rather than a spreadsheet exercise.

**Success criteria.**

- **A conversation can no longer outgrow its storage.** The largest document a conversation produces is bounded by its largest single message rather than by its message count, measured by the emulator integration test in US-105 writing a 5,000-message transcript and asserting the maximum observed item size does not grow with message count. Target: 0 write failures at 5,000 messages, against today's ceiling of roughly 1.5 MB of accumulated text in one item.
- **Every message carries a context cost.** 100% of transcript messages persisted after EP-3 carry a `tokens` greater than zero for non-empty content and a `tokenAccuracy` of `Estimated` or `Exact`, measured by the US-304 integration test over a multi-turn conversation. Today the figure is 0%, because `Tokens = 0` is written literally.
- **Estimator error is measured, and small where it can be.** On Azure OpenAI turns with zero tool calls, the estimated prompt tokens land within 5% of the provider-reported `inputTokens` at p50 and 15% at p95, measured by the reconciliation metric US-502 emits on the existing `Enterprise.Gpt.Chat` meter (`Api/Observability/TelemetryRegistration.cs`). For Amazon Bedrock and Anthropic the same metric is recorded with no target until US-503's calibration multiplier is tuned from it.
- **History replay stops being unbounded.** 100% of turns are sent with an estimated prompt at or below the selected model's `ContextWindowSize` minus `MaxOutputTokens`, measured by the budget counter US-604 emits; today `ConversationService.cs:626-629` replays every message with no windowing at all. Target: 0 turns sent above the budget.
- **Cost becomes a single-table query.** After EP-7, 100% of new `Core.ConversationUsage` rows carry either both price snapshots or explicit nulls, and a per-conversation cost is computable without joining `[Core.Ref].[Model]`, measured by the reporting query US-704 adds to `docs/conversations/usage-and-favorites.md` §7.

## 2. Goals & non-goals

**Goals.**

- Replace the one-document-per-conversation transcript with one document per message under a hierarchical partition key, so a conversation's length stops being a storage ceiling and a transcript read is prefix-routed to a single subpartition.
- Separate the two quantities that the word "tokens" currently conflates — a message's **context cost** and a turn's **billed cost** — and give each its own home, the first on the transcript message and the second on the SQL audit trail that already holds it.
- Make the token estimate a measured number rather than an asserted one, by reconciling it against provider-reported usage on the turns where the two are directly comparable.
- Bound what is replayed to the model by the model's own context window, using the per-message tokens as the unit of the budget.
- Give the platform a price, so the audit trail that already answers "how many tokens" can answer "how much money" without joining a catalog that is editable.
- Store a rendered form of each message at persist time, so export is a read rather than a re-render.
- Take the deliberate corrections the clean slate makes free — explicit sequencing, string roles, a real indexing policy, System.Text.Json, a settable `messageCount`, and the removal of the dead `documents[]` array.

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

- **Chat user**: reaches their own conversations through `api/conversations`, which is `.RequireAuthorization()` at the group level and carries no permission filter. Ownership is enforced in `ConversationService`, and another user's conversation is a 404, not a 403 — the new container's partition key makes that stronger rather than weaker, because a read is scoped to `(userId, conversationId)` and a mismatched user cannot address the subpartition at all.
- **Administrator**: the price fields ride the existing admin-gated model routes — `POST api/models`, `PUT api/models/{id}`, and `GET api/models/all`, each already carrying `PermissionEndpointFilter.Require(PermissionIds.Administrator)`. No new permission id is introduced.
- **Export** (EP-8): the same ownership rule as `GET api/conversations/{id}/messages`. Exporting another user's conversation is a 404. No new permission id.
- **Cutover** (US-207): an operator action outside the API surface, gated by configuration rather than by a route, so no HTTP caller can trigger it.

## 4. Functional requirements

| ID | Requirement | Priority | Epic(s) |
| --- | --- | --- | --- |
| FR-1 | Transcripts are stored one document per message in a new container under a hierarchical partition key of `/userId` then `/conversationId` | P0 | EP-1, EP-2 |
| FR-2 | The container is provisioned with an explicit indexing policy that excludes `/content/*` and `/htmlContent/*` | P0 | EP-1 |
| FR-3 | Cosmos configuration binds to a typed options class validated at startup; a missing database or container id fails app start rather than a request | P0 | EP-1 |
| FR-4 | The Cosmos client serializes through System.Text.Json, so the `[JsonPropertyName]` attributes on the document types are the naming contract | P0 | EP-1 |
| FR-5 | `IAzureCosmosService` gains transactional batches, continuation-token paging, and delete-by-partition-key, and sheds the members nothing calls | P0 | EP-1 |
| FR-6 | The storage layer is covered by integration tests against a real Cosmos emulator rather than an in-memory fake | P0 | EP-1, EP-2 |
| FR-7 | Each message document carries an explicit monotonic `sequence`, allocated atomically and independently of `IConversationLockService` | P0 | EP-2 |
| FR-8 | A completed turn writes both message documents and the header counters in one transactional batch scoped to `(userId, conversationId)` | P0 | EP-2 |
| FR-9 | Cancelled and failed turns continue to be billed in SQL and never transcribed | P0 | EP-2 |
| FR-10 | `role` is serialized as a string, matching the SSE wire contract rather than the `ChatRoles` integer | P0 | EP-2 |
| FR-11 | Every transcript read is a single-subpartition query ordered by `sequence`, never a cross-partition scan | P0 | EP-2 |
| FR-12 | `GET api/conversations/{id}/messages` pages a long transcript by sequence cursor and reports the total | P1 | EP-2 |
| FR-13 | Rename, soft delete, and purge act on the header document; purging a conversation is one delete-by-partition-key, not a query plus N deletes | P0 | EP-2 |
| FR-14 | Cutover is an explicit, gated operator action with a runbook, and it destroys every existing transcript | P0 | EP-2 |
| FR-15 | Every persisted transcript message carries a `tokens` context cost and a `tokenAccuracy` of `Estimated` or `Exact` | P0 | EP-3 |
| FR-16 | Token estimation resolves per provider through a registry that mirrors `Providers.ServiceKeys`, failing the same way `ChatClientResolver` does | P0 | EP-3 |
| FR-17 | The estimator selects `o200k_base` for the models that use it and falls back to `cl100k_base` on an unrecognized deployment name | P0 | EP-3 |
| FR-18 | The estimator adds a configurable per-turn tool-schema overhead and a per-message framing constant, so unattributable prompt tokens are counted | P1 | EP-3 |
| FR-19 | The header's `totalTokens` and `messageCount` stay consistent with the message documents backing them | P1 | EP-2, EP-3 |
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

## 6. Technical considerations

**Integration points.** All verified against the working tree.

| Concern | Where |
| --- | --- |
| Transcript document types | `Enterprise.Gpt.Entity/CosmosConversation.cs` — `CosmosConversation`, `CosmosConversationMessage`, `CosmosConversationUsage`, `CosmosConversationDocument` |
| Cosmos access | `Enterprise.Gpt.Service/AzureCosmosService.cs` — `IAzureCosmosService` and its `Container` wrapper; `MaxPatchOperations = 10` at `:107` |
| Cosmos bootstrap | `Enterprise.Gpt.Api/Program.cs:338-353` (client, serializer options, service registration) and `:450-451` (`CreateDatabaseIfNotExistsAsync` / `CreateContainerIfNotExistsAsync(cosmosContainerId, "/userId")`), both inside the `!IsEnvironment("Testing")` block at `:428` |
| Turn persistence | `ConversationService.PersistTurnAsync` — the SQL transaction, the `if (!completed) return;` at `:876`, and the patch at `:912-920` |
| History replay | `ConversationService.cs:605` (point read), `:624` (`transcriptExists`), `:626-629` (unbounded replay), `:636` (project instructions onto `ChatOptions.Instructions`, set at `:1147`) |
| Seeded system message | `ConversationService.cs:209-215`, built by `ConversationPrompts.BuildDefaultSystemPrompt()` |
| Transcript read | `ConversationService.GetConversationMessagesAsync` at `:1002`, behind `GET api/conversations/{id}/messages` in `Api/Endpoints/ConversationEndpoints.cs:39` |
| Tokenizer | `Enterprise.Gpt.Service/Chunking/TokenTextChunker.cs` — `ITextChunker.CountTokens(string)` at `:84`, `CreateTokenizer` at `:428-449`, `FallbackEncoding = "cl100k_base"` at `:42` |
| Tokenizer packages | `Enterprise.Gpt.Service.csproj:42-43` — `Microsoft.ML.Tokenizers` and `Microsoft.ML.Tokenizers.Data.Cl100kBase`, both 2.0.0 |
| Markdown renderer | `Enterprise.Gpt.Service.csproj:27` — `Markdig` 1.3.2, referenced with **zero call sites** in the solution today |
| Provider registry | `Enterprise.Gpt.Dto/Enums/Providers.cs` — three seeded ids and `FrozenDictionary<Guid, string> ServiceKeys`; consumed by `Chat/ChatClientResolver.cs:42-46` |
| Usage collection | `Service/Chat/ChatUsageScope.cs`, `Chat/ChatUsageObserver.cs`, `Chat/UsageReportTranslator.cs` — correct, and unchanged by this project |
| Usage schema | `Enterprise.Gpt.Entity/ConversationUsage.cs` (`AssistantTokens` at `:98`, `TotalTokens` at `:108`, both unmapped getters), `ConversationUsageMcpServer.cs`, `ConversationUsageToolCall.cs` |
| Model catalog | `Enterprise.Gpt.Entity/Model.cs` — `[Table(nameof(Model), Schema = "Core.Ref")]`, with `ContextWindowSize` and `MaxOutputTokens` as `decimal(18, 2)` at `:28-32` |
| Migrations | `Enterprise.Gpt.Repository/Migrations/20260811024339_InitialCreate.cs` — migrations now exist, so a schema story adds a real one |
| Telemetry | `Api/Observability/TelemetryRegistration.cs` — `Enterprise.Gpt.Chat` registered as both an `ActivitySource` and a `Meter`; documented in `docs/observability/request-logging.md` §7.3 |
| Options pattern | `Enterprise.Gpt.Service/Settings/` — `UserPermissionCacheOptions` is the shape to copy, registered with `AddOptions<T>().Bind(...).Validate(...).ValidateOnStart()` at `Program.cs:369-373` |
| Reference docs | `docs/conversations/usage-and-favorites.md` and `docs/conversations/streaming-contract.md` — read both before touching this area |

**The token model.** "Tokens" is two quantities, and this document keeps them apart by name:

- **Context cost** — what a message contributes to the prompt on every *future* turn. A pure function of the message's own text, deterministic, recomputable, and comparable across roles. This is the `tokens` property on a message document.
- **Billed cost** — what a turn actually charged, including tool round trips, agent runs, reasoning, and cached tokens. Only the provider knows it, and it is reported per *turn*, never per message. This is the existing `usage` block on the assistant message plus the SQL audit trail underneath it.

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

Three consequences worth stating, because each is counterintuitive:

1. **The system message is not privileged.** It is a seeded transcript message (`ConversationService.cs:209-215`) and goes through the same estimator as user text. What *is* special is project instructions: they ride on `ChatOptions.Instructions` (`ConversationService.cs:636`, assigned at `:1147`) rather than as a message, so they are billed but belong to no message.
2. **Tool and function JSON schemas cost input tokens attributable to no message.** Every leased MCP tool's schema is injected into the prompt, and with several servers attached that is easily thousands of tokens. The estimator therefore needs an explicit, configurable per-turn overhead term alongside a small per-message framing constant, or the sum of the messages will always under-report the prompt.
3. **`tokens` on an assistant message will not equal `usage.outputTokens`, and that is correct.** `outputTokens` includes tool-call arguments the model generated that are never transcribed. On a turn with zero tool calls the two *should* agree, and that agreement is the ground-truth signal EP-5 uses to measure estimator accuracy for free.

**Multi-provider accuracy.** tiktoken is OpenAI's tokenizer; it undercounts Claude by roughly 15–20% on prose and by more on code and non-English text. Anthropic's `count_tokens` endpoint is exact but is a network round trip per message and does not belong on the write path. The design is therefore a local tiktoken estimate, a per-provider calibration multiplier read from configuration, and a measured accuracy signal (EP-5) so the multipliers are tuned from real traffic rather than guessed. Azure OpenAI is the accurate path today; the resolver is the seam a future provider drops into, exactly as `Providers.ServiceKeys` is the seam for chat clients. Note that `Microsoft.ML.Tokenizers.Data.Cl100kBase` is installed but the modern OpenAI families use `o200k_base`, so adding `Microsoft.ML.Tokenizers.Data.O200kBase` (2.0.0 is published, matching the pinned tokenizer version) is a required story rather than an optimization.

**Data storage and privacy.** Two `type`-discriminated document kinds share the new container, both partitioned on `/userId` then `/conversationId`:

```jsonc
// type: "conversation"  — id = conversationId
{ "id", "userId", "conversationId", "type", "name", "totalTokens", "messageCount",
  "schemaVersion", "dateCreated", "dateModified", "dateDeactivated" }

// type: "message"  — id = messageId, one document per message
{ "id", "userId", "conversationId", "type", "sequence", "role", "content", "htmlContent",
  "tokens", "tokenAccuracy", "model", "providerId", "usage", "dateCreated" }
```

The hierarchical key earns its place three ways. A transcript read is prefix-routed to one subpartition instead of filtered inside a whole user's partition. The 20 GB logical-partition ceiling applies per conversation rather than per user, because with a hierarchical key the logical partition is the entire key path. And because `(userId, conversationId)` is a *complete* partition key, purging one conversation is a single server-side `DeleteAllItemsByPartitionKeyStreamAsync` rather than a query plus N deletes — the delete-by-partition-key feature supports hierarchical keys only when every level is supplied, which is precisely the shape here.

`content` and `htmlContent` hold user prompts and model output, and neither is ever filtered on, so both are excluded from the indexing policy — pure RU and storage cost otherwise. Today no indexing policy is specified at all (`Program.cs:451`), so everything is indexed. Nothing new is stored about a user beyond what the transcript already holds; the message documents carry the same content the array elements did.

**Deliberate changes the clean slate makes free.** Each fixes something real in the current code:

- **`sequence`, an explicit monotonic integer.** Today a turn's user and assistant messages share one `DateTimeOffset.UtcNow` (`ConversationService.cs:800`) and are ordered only by array position. Separate documents have no array to lean on, so time alone cannot order them.
- **Sequence allocated atomically** by patch-incrementing the header's `messageCount` with `EnableContentResponseOnWrite = true` so the new value comes back. That makes ordering independent of `ConversationLockService`, whose own interface documentation (`IConversationLockService.cs:9-13`) states it is process-local and gives no mutual exclusion across replicas.
- **`role` as a string.** `ChatRoles` is `System = 1, Assistant = 2, User = 3, Tool = 4`; the SSE wire contract already serializes string enum values, and a string keeps an exported document readable.
- **System.Text.Json on the `CosmosClient`.** Today only `CosmosSerializationOptions { PropertyNamingPolicy = CamelCase }` is set (`Program.cs:341-347`), so the SDK's Newtonsoft default is active while the document types carry `[JsonPropertyName]` attributes it never reads. They agree only by coincidence, and the repository already carries a recorded bug from that mismatch — see the comment at `ConversationService.cs:289-291`, where a cross-partition query's predicates did not match the documents' camelCase names and so never selected anything. `CosmosClientOptions.UseSystemTextJsonSerializerWithOptions` exists in the pinned `Microsoft.Azure.Cosmos` 3.62.1.
- **The `documents[]` array is dropped.** It has been dead since inception — nothing ever adds to it, only `Documents = []` at `ConversationService.cs:208`. Conversation documents live in SQL.
- **`messageCount` becomes a real settable property.** Today it is an expression-bodied getter `=> Messages.Count`, which the write path *also* targets with `PatchOperation.Increment("/messageCount", 2)`, so the persisted and in-memory values silently diverge.

**Write path.** One `TransactionalBatch` on `(userId, conversationId)` creates the user message document, creates the assistant message document, and patches the header counters — atomic, and it retires the `TranscriptExists` seed-versus-append branch at `ConversationService.cs:914-916`. A batch is scoped to a single partition key and capped at 100 operations and 2 MB, both of which this write is far inside. The rule that cancelled and failed turns are billed in SQL but never transcribed is preserved exactly (`ConversationService.cs:876-879`, rationale at `:792-797`): a truncated answer appended to the transcript would be replayed to the model forever as something the assistant said.

**Read path.** `SELECT … WHERE c.type = 'message' AND c.conversationId = @id ORDER BY c.sequence`, with `QueryRequestOptions.PartitionKey` built through `PartitionKeyBuilder`. `IAzureCosmosService.GetItemsAsync` today drains every page into a `List<T>` with no partition-key option and has no callers; `UpdateItemAsync` and `DeleteItemAsync` are likewise called only from a `DidNotReceive` assertion in the unit tests. The interface needs real paged query support with continuation tokens and a transactional-batch surface; `AzureCosmosService.cs:107` caps a patch at 10 operations, which the new write path must respect.

**Security.** Cosmos authenticates today with an account-key connection string (`CosmosDb:ConnectionString`, `Program.cs:338`), which is the one place in the API that does not follow the project's `DefaultAzureCredential` preference. This project does not change that — moving to managed identity is a separate decision with its own deployment consequences — but US-101 is written so that the options type has room for it and the open question is recorded in §9. There is also no options class and no startup validation for Cosmos at all today: a missing `CosmosDb:DatabaseId` surfaces as a `null!` bang at `Program.cs:352`, unlike `AmazonBedrockOptions`, `AnthropicOptions`, `DocumentOptions`, `DocumentRetrievalOptions`, and `UserPermissionCacheOptions`, all of which use `ValidateOnStart()`. Markdig runs with raw HTML passthrough disabled so model output — which is attacker-influenceable through uploaded documents and MCP tool results — cannot smuggle markup into a stored artifact.

**Scalability and performance.** Every transcript read is a single-subpartition query; the acceptance criteria assert that no read is issued without a complete `(userId, conversationId)` partition key, so a cross-partition fan-out is a test failure rather than a cost surprise. Excluding `/content/*` and `/htmlContent/*` from indexing removes the dominant term in write RU, since those two properties are the bulk of a message document's bytes. The context estimator is a pure CPU cost on the write path with a singleton tokenizer, matching how `TokenTextChunker` is already registered — building a tokenizer loads and indexes a large vocabulary and must not happen per request.

**AI system requirements.** The estimator is evaluated, not assumed. The evaluation set is production traffic filtered to turns with zero `ConversationUsageToolCall` rows, where `estimate(prompt) + configured overhead` is directly comparable to the provider's reported `inputTokens`. Pass threshold for Azure OpenAI: within 5% at p50 and 15% at p95 over a rolling 7-day window, at a minimum sample of 200 turns. Amazon Bedrock and Anthropic record the same metric with no pass threshold until US-503 tunes their multipliers; the threshold for those providers is an open question in §9, not an invented number.

## 7. Epics & user stories

| ID | Epic | Goal | Priority | Estimate | Depends on |
| --- | --- | --- | --- | --- | --- |
| EP-1 | Cosmos storage foundation | The transcript container, its client, and its access layer are correct, validated, and provable | P0 | L | — |
| EP-2 | Message-per-document transcript | A conversation's messages are stored, ordered, read, paged, and purged as individual documents | P0 | L | EP-1 |
| EP-3 | Per-message tokenization | Every message carries a context cost the system can compare and budget against | P0 | M | EP-1 |
| EP-4 | Rendered HTML content | Every message carries a safe rendered form, produced once at persist time | P1 | M | EP-1 |
| EP-5 | Estimator accuracy telemetry | The estimate's error against provider truth is measured, and correctable per provider | P1 | M | EP-2, EP-3 |
| EP-6 | Context-window budgeting | A turn's replayed history fits the model that will answer it | P1 | M | EP-2, EP-3 |
| EP-7 | Pricing and cost approximation | The audit trail answers "how much money", not just "how many tokens" | P1 | M | — |
| EP-8 | Conversation export | A user takes a conversation out of the platform as HTML or JSON | P2 | S | EP-2, EP-4 |

EP-1 is entirely `[enabler]` stories. That is a property of replacing a storage layer rather than a decomposition failure: none of them exceeds M, and each names the stories it unblocks. EP-1 and EP-7 touch no common file and start together; EP-3 and EP-4 build their services against no shared surface and run concurrently, with only their wiring stories waiting on EP-2.

### EP-1: Cosmos storage foundation

#### US-101: `[enabler]` Bind and validate the Cosmos configuration at startup

- **Story**: Replace the three loose `builder.Configuration["CosmosDb:*"]` reads with a typed `CosmosOptions` validated at startup, so a misconfigured deployment fails at boot rather than on the first conversation. Unblocks US-102, US-103, and US-104.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Acceptance criteria**:
  - Given `Enterprise.Gpt.Service/Settings/CosmosOptions.cs` bound from the `CosmosDb` section, when the application starts, then it is registered with `AddOptions<CosmosOptions>().Bind(...).Validate(...).ValidateOnStart()`, matching the `UserPermissionCacheOptions` registration at `Program.cs:369-373`.
  - Given configuration with `CosmosDb:DatabaseId` absent, when the application starts, then startup throws an `OptionsValidationException` naming the missing key, and no `null!` bang remains at the former `Program.cs:352`.
  - Given configuration with an empty `CosmosDb:ConnectionString`, when the application starts, then startup fails with a message naming the setting and never the value.
  - Given the `Testing` environment, when the integration host starts, then validation still runs and `CustomWebApplicationFactory`'s fake configuration satisfies it.

#### US-102: `[enabler]` Serialize Cosmos documents with System.Text.Json

- **Story**: Switch the `CosmosClient` to the System.Text.Json serializer so the `[JsonPropertyName]` attributes the document types already carry are the naming contract, rather than agreeing with the Newtonsoft default by coincidence. Unblocks US-201.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-101
- **Acceptance criteria**:
  - Given the client registration, when `CosmosClientOptions` is built, then it sets `UseSystemTextJsonSerializerWithOptions` with `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` and a `JsonStringEnumConverter`, and the `CosmosSerializationOptions` block at `Program.cs:341-347` is gone.
  - Given a document type whose property carries a `[JsonPropertyName]` that differs from its camelCased member name, when it is written and read back, then the stored property name is the attribute's value.
  - Given a query whose predicate names a camelCase property, when it runs against a stored document, then it matches — the failure recorded at `ConversationService.cs:289-291` does not recur.
  - Given an enum-typed property, when the document is serialized, then the JSON carries the string name rather than the integer.

#### US-103: `[enabler]` Provision the transcript container with a hierarchical partition key

- **Story**: Create the new transcript container with `/userId` then `/conversationId` as its partition key paths and an explicit indexing policy, so reads are prefix-routed and the two largest properties are not indexed. Unblocks US-104 and every story in EP-2.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-101
- **Acceptance criteria**:
  - Given the startup bootstrap, when the container is created, then `ContainerProperties.PartitionKeyPaths` is `["/userId", "/conversationId"]` in that order, and the container id comes from `CosmosOptions` rather than the old `CosmosDb:ContainerId` literal.
  - Given the container's indexing policy, when it is read back, then `/content/*` and `/htmlContent/*` are excluded paths and `/sequence`, `/type`, `/conversationId`, and `/dateCreated` are indexed.
  - Given a container that already exists with different partition key paths, when the bootstrap runs, then it logs an error naming both the expected and the actual paths and fails startup rather than silently using the old container.
  - Given the `Testing` environment, when the host starts, then the bootstrap is still skipped, exactly as `Program.cs:428` does today.

#### US-104: `[enabler]` Give the Cosmos service batches, paging, and partition purge

- **Story**: Extend `IAzureCosmosService` with a transactional-batch surface, continuation-token paged queries scoped to a partition key, and delete-by-partition-key, and remove the three members nothing calls. Unblocks US-202, US-203, US-204, US-205, and US-206.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-102, US-103
- **Acceptance criteria**:
  - Given the interface, when it is reviewed, then `GetItemsAsync`, `UpdateItemAsync`, and `DeleteItemAsync` are gone — none has a production caller — and a paged `QueryAsync<T>` taking a `PartitionKey`, a page size, and a continuation token has replaced them.
  - Given a paged query over 250 matching documents with a page size of 100, when it is drained, then it issues 3 requests and returns 250 items in query order with a null continuation token on the last page.
  - Given a query issued without a partition key, when it is submitted, then the service throws rather than executing a cross-partition scan.
  - Given a transactional batch of 3 operations on one `(userId, conversationId)` key where the second operation fails, when it executes, then no document is created and the returned status names the failing operation index.
  - Given a batch built with more than 100 operations or a patch built with more than the `MaxPatchOperations = 10` the service already enforces at `:107`, when it is submitted, then the service throws before the request leaves the process.

#### US-105: `[enabler]` Prove the storage foundation against a real Cosmos emulator

- **Story**: Stand up the Cosmos DB Linux emulator in Testcontainers alongside the existing SQL Server 2025 container, because hierarchical partition keys, transactional batches, and cross-document ordered queries are exactly what `FakeAzureCosmosService` cannot prove. Unblocks confidence in US-207, which destroys data.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-104
- **Acceptance criteria**:
  - Given `IntegrationTestFixture`, when it starts, then it launches `mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator` alongside the existing `MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest")` container, waits on the emulator's health endpoint, and the Cosmos bootstrap is no longer skipped for these tests.
  - Given the emulator, when a transcript of 5,000 message documents is written and read, then every read is served from one subpartition, the results come back ordered by `sequence`, and no single document exceeds the size of its own message plus 2 kB of envelope.
  - Given a transactional batch whose second operation violates a unique id, when it executes against the emulator, then no document from that batch is present afterwards.
  - Given two conversations in the same user's partition, when one is purged by partition key, then the other's documents are untouched and a subsequent read returns them in full.
  - Given the emulator container is unavailable, when the test class runs, then it fails with a message naming the emulator rather than timing out silently, and it carries both `[Collection("Integration")]` and `[Trait("Category", "Integration")]` so `dotnet test --filter "Category!=Integration"` still passes without Docker.

### EP-2: Message-per-document transcript

#### US-201: `[enabler]` Define the header and message document types

- **Story**: Replace `CosmosConversation` with a `type`-discriminated header and a per-message document, taking the corrections the clean slate allows. Unblocks every remaining story in this epic.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-102
- **Acceptance criteria**:
  - Given the header type, when it is serialized, then it carries `id`, `userId`, `conversationId`, `type: "conversation"`, `name`, `totalTokens`, `messageCount`, `schemaVersion`, `dateCreated`, `dateModified`, and `dateDeactivated`, and `messageCount` is a settable property rather than the `=> Messages.Count` getter it is today.
  - Given the message type, when it is serialized, then it carries `id`, `userId`, `conversationId`, `type: "message"`, `sequence`, `role`, `content`, `htmlContent`, `tokens`, `tokenAccuracy`, `model`, `providerId`, `usage`, and `dateCreated`.
  - Given a message with `Role = ChatRoles.Assistant`, when it is serialized, then `role` reads `"assistant"` rather than `2`.
  - Given the new types, when they are reviewed, then neither carries a `documents[]` array and `CosmosConversationDocument` is deleted, because nothing has ever added to it beyond `Documents = []` at `ConversationService.cs:208`.
  - Given `schemaVersion`, when a document is written, then it is stamped with the current version constant, so a future shape change has a discriminator to read.

#### US-202: Allocate a message sequence atomically

- **Story**: As the platform, allocate each message's `sequence` by atomically incrementing the header's counter, so two messages can never claim the same position even when the process-local conversation lock does not apply.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-201, US-104
- **Acceptance criteria**:
  - Given a turn about to persist, when it allocates sequences, then it patches `/messageCount` by 2 with `EnableContentResponseOnWrite = true` and reads the new value back to derive the two positions.
  - Given two turns racing against the same conversation from two processes, when both allocate, then the four resulting messages hold four distinct sequences with no gaps of more than the allocations that occurred.
  - Given the allocation succeeds and the subsequent batch fails, when the conversation is read, then the sequence gap is tolerated — ordering is by `sequence` ascending, not by contiguity — and no message is lost or duplicated.
  - Given a header document that does not exist, when allocation is attempted, then it returns not-found and the turn is recorded in SQL and logged exactly as the missing-document path at `ConversationService.cs:922-931` does today.

#### US-203: Persist a completed turn as one transactional batch

- **Story**: As the platform, write a completed turn's user message, assistant message, and header counters in one transaction, so a conversation whose counters moved always has the messages that moved them.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-202
- **Acceptance criteria**:
  - Given a completed turn, when it persists, then one `TransactionalBatch` on `(userId, conversationId)` creates both message documents and patches `/totalTokens` and `/dateModified` on the header, and the `TranscriptExists` seed-versus-append branch at `ConversationService.cs:914-916` is gone along with the `transcriptExists` flag at `:624` and its `TurnOutcome` field.
  - Given a cancelled or failed turn, when finalization runs, then the SQL usage row and the conversation counters are still written and nothing is transcribed, preserving the rule at `ConversationService.cs:876-879`.
  - Given the batch fails, when the error returns, then the existing error log fires naming both the usage row id and the assistant message id, because the usage row already asserts an `AssistantMessageId` that no transcript contains.
  - Given a completed turn, when the assistant message is written, then its `model` holds the `DeploymentName` rather than the display name, keeping the append-only reasoning documented at `ConversationService.cs:880-882`.
  - Given a conversation being created, when the seeded system message is written, then it is a message document at sequence 0 and the header is created in the same batch.

#### US-204: Read a transcript in sequence order

- **Story**: As a user, I want to reopen a conversation and see its messages in the order they happened, so the transcript reads the same way it was written.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-203
- **Acceptance criteria**:
  - Given `GetConversationMessagesAsync`, when it runs, then it issues `SELECT … WHERE c.type = 'message' AND c.conversationId = @id ORDER BY c.sequence` with `QueryRequestOptions.PartitionKey` built via `PartitionKeyBuilder` from `(userId, conversationId)`, replacing the point read at `ConversationService.cs:1002`.
  - Given the history replay path, when a turn starts, then it reads the same way rather than point-reading the whole document at `ConversationService.cs:605`, and the system message is still first in the replayed `List<ChatMessage>`.
  - Given the read, when it projects to `ConversationMessageDto`, then system-role messages are still excluded from the API response, as they are today.
  - Given a conversation id belonging to another user, when the read runs, then the partition key does not resolve, the service throws `NotFoundException`, and the caller receives a 404 rather than a 403.
  - Given a conversation whose header exists but which has no message documents, when the read runs, then it returns an empty message list rather than throwing.

#### US-205: Page a long transcript

- **Story**: As a user with a very long conversation, I want the client to load the newest part of it first, so opening the conversation does not require transferring every message ever sent.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-204
- **Acceptance criteria**:
  - Given `GET api/conversations/{id}/messages` with no query parameters, when it runs, then it returns the newest 100 messages in ascending sequence order plus the conversation's total message count.
  - Given `?take=` outside 1–100, when the request is handled, then `take` is clamped server-side to that range, matching the clamp every other paginated list endpoint applies.
  - Given `?before={sequence}`, when the request is handled, then it returns the page of messages immediately preceding that sequence and reports whether more remain.
  - Given `?before=` with a non-numeric value, when the request is handled, then it returns a 400 `validation-error` problem naming the parameter.
  - Given this changes the default response from "every message" to "the newest 100", when the story ships, then `docs/conversations/streaming-contract.md` and the `enterprise-gpt-ui/` transcript store are named in the story as breaking-change consumers.

#### US-206: Rename, deactivate, and purge a conversation against the header

- **Story**: As a user, I want renaming, deleting, and permanently removing a conversation to keep working, so splitting the document does not cost me a capability I have today.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-203
- **Acceptance criteria**:
  - Given a rename, when it persists, then it patches `/name` and `/dateModified` on the header document only and touches no message document, preserving the behavior at `ConversationService.cs:428` and `:512`.
  - Given a soft delete, when it persists, then it sets `/dateDeactivated` on the header and leaves the message documents in place, preserving the per-id patch loop's behavior at `ConversationService.cs:294`.
  - Given a purge, when it runs, then it issues one `DeleteAllItemsByPartitionKeyStreamAsync` against the complete `(userId, conversationId)` key rather than a query plus N deletes.
  - Given a purge attempted with only `userId`, when it is built, then the service refuses it before the request leaves the process, because a partial hierarchical key is not a valid delete-by-partition-key target and would error at the service.
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
  - Given a model whose `DeploymentName` is a deployment alias the tokenizer library does not recognize, when the estimator is built, then it catches `NotSupportedException` and `ArgumentException` and falls back to a configured encoding, matching `TokenTextChunker.CreateTokenizer` at `:428-449`.
  - Given the estimator, when it is registered, then it is a singleton, because building a tokenizer loads and indexes a large vocabulary and must not happen per request.
  - Given the string `""`, when it is estimated, then it returns 0 without touching the tokenizer.

#### US-303: Account for the tokens no message owns

- **Story**: As the platform, add a configurable per-turn overhead and per-message framing constant to the estimate, so the tool schemas and chat framing that cost input tokens but belong to no message are not silently missing from the total.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-302
- **Acceptance criteria**:
  - Given a turn with three MCP servers leased, when the prompt estimate is computed, then it adds a per-turn overhead term derived from the configured per-tool schema cost times the number of attached tools, in addition to the sum of the message estimates.
  - Given the configured per-message framing constant, when a prompt of 10 messages is estimated, then the framing term is applied 10 times.
  - Given project instructions on `ChatOptions.Instructions` (`ConversationService.cs:636`), when the prompt estimate is computed, then their text is estimated into the turn overhead and attributed to no message.
  - Given a configured overhead value below zero, when the application starts, then `ValidateOnStart` fails naming the setting.
  - Given a turn with zero tools and zero project instructions, when the estimate is computed, then the overhead term contributes only the framing constants and nothing else.

#### US-304: Record a context cost on every transcript message

- **Story**: As a user, I want each message in my conversation to carry what it costs to keep, so the platform can budget and I can eventually see it.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-302, US-203
- **Acceptance criteria**:
  - Given a completed turn, when its two message documents are written, then each carries `tokens` equal to the estimator's count of its own `content`, and the literal `Tokens = 0` at `ConversationService.cs:891` is gone.
  - Given the seeded system message, when it is written at conversation creation, then it carries a `tokens` value produced by the same estimator as user text, with no special-casing.
  - Given a message estimated locally, when it is written, then `tokenAccuracy` reads `Estimated`; given one whose count came from a provider-reported exact figure, then it reads `Exact`.
  - Given the assistant message on a turn with tool calls, when it is written, then its `tokens` counts only the transcribed answer text and does not equal `usage.outputTokens`, and a test asserts that inequality rather than treating it as a defect.
  - Given the estimator throws, when a turn persists, then the message is still written with `tokens` of 0 and `tokenAccuracy` of `Estimated`, and the failure is logged — a tokenizer fault must not lose a turn that has already streamed.

#### US-305: Keep the header's totals honest

- **Story**: As the platform, keep the header's `totalTokens` and `messageCount` consistent with the message documents beneath them, so a counter can be trusted without reading the whole transcript.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-304
- **Acceptance criteria**:
  - Given a completed turn, when the batch patches the header, then `totalTokens` moves by the turn's billed input plus output — the same numbers written to `Core.ConversationUsage` — and `messageCount` moves by exactly the number of message documents created.
  - Given a conversation of 20 turns, when its header is read, then `messageCount` equals the count of its message documents, asserted against the emulator.
  - Given a turn that is billed but not transcribed, when finalization runs, then `messageCount` does not move and the SQL counters do, preserving today's split.
  - Given a reconciliation query over the header and its messages, when it runs, then any divergence is reportable, because `messageCount` is now a stored value rather than a getter that cannot disagree with itself.

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
  - Given a turn with no usage report at all — the `turn.Report is null` path already logged at `ConversationService.cs:806-813` — when finalization runs, then no reconciliation is recorded and the existing warning is unchanged.
  - Given reconciliation runs, when it completes, then it adds no database round trip and no provider call to the turn.

#### US-502: Emit estimator accuracy as metrics

- **Story**: As a platform operator, I want the estimator's error visible in Application Insights, so a provider whose estimate has drifted shows up as a metric rather than as a support ticket.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-501
- **Acceptance criteria**:
  - Given a reconciled turn, when the metric is emitted, then it is a histogram on the existing `Enterprise.Gpt.Chat` meter, tagged with the provider id and the deployment name, so it exports through `TelemetryRegistration.AddEnterpriseTelemetry` with no new meter registration.
  - Given the metric, when it is queried for Azure OpenAI over a 7-day window with at least 200 samples, then p50 relative error is within 5% and p95 within 15%.
  - Given no Application Insights connection string is configured, when a turn reconciles, then the metric is still recorded to the meter and the distro is still skipped, matching the behavior documented in `docs/observability/request-logging.md` §7.1.
  - Given the metric's tags, when they are inspected, then no conversation id, user id, or message content appears on any dimension.

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
  - Given a conversation whose messages exceed the selected model's budget, when a turn starts, then the replay built at `ConversationService.cs:626-629` carries only the messages the calculator kept.
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
  - Given the change, when `dotnet ef migrations add` is run, then a new migration lands beside `20260811024339_InitialCreate.cs` and `Database.Migrate()` at startup applies it — this is the first schema change since migrations began existing, and `docs/conversations/usage-and-favorites.md` §6.5 is stale in saying the folder is empty.
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
  - Given the reporting query added to `usage-and-favorites.md` §7, when it runs over a date range, then it produces per-conversation and per-model cost touching only `Core.ConversationUsage`, with no join to the catalog.

### EP-8: Conversation export

#### US-801: Export a conversation as HTML

- **Story**: As a user, I want to download one of my conversations as a self-contained HTML file, so I can keep or share it outside the platform.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-403
- **Acceptance criteria**:
  - Given `GET api/conversations/{id}/export?format=html`, when a user requests their own conversation, then the response is a `text/html` document assembled from the stored `htmlContent` of every non-system message, in `sequence` order, with the conversation name as its title.
  - Given a conversation belonging to another user, when the export is requested, then the response is a 404 `resource-not-found` problem, matching how every other conversation route treats a foreign id.
  - Given `?format=` with an unsupported value, when the export is requested, then the response is a 400 `validation-error` problem naming the supported formats.
  - Given a message whose `htmlContent` is null, when it is exported, then its `content` is HTML-escaped into the output rather than omitted.
  - Given a conversation with no messages, when it is exported, then the response is a valid HTML document with an empty body section rather than a 404.

#### US-802: Export a conversation as JSON

- **Story**: As a user, I want the same export as structured JSON, so I can process my conversation with a tool instead of reading it.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-801
- **Acceptance criteria**:
  - Given `GET api/conversations/{id}/export?format=json`, when it responds, then the body carries the conversation's name and dates plus an ordered array of messages, each with `sequence`, `role` as a string, `content`, `htmlContent`, `tokens`, `tokenAccuracy`, `model`, and `dateCreated`.
  - Given the JSON export, when it is compared to the stored documents, then the property names match the message document's `[JsonPropertyName]` values, so the export is the storage shape rather than a third representation.
  - Given a conversation belonging to another user, when the export is requested, then the response is a 404 `resource-not-found` problem.
  - Given an assistant message with a `usage` block, when it is exported, then the block is present with `inputTokens` and `outputTokens` and no per-tool breakdown, because that data lives in SQL and is not exposed over HTTP.

## 8. Milestones & rollout

**Epic dependency and parallelism.**

| Wave | Epics | May run concurrently with | Blocked until |
| --- | --- | --- | --- |
| 1 | EP-1, EP-7 | each other — no shared file | — |
| 2 | EP-2, EP-3, EP-4 | each other; EP-7 continues alongside | EP-1 (EP-3 and EP-4 build their services independently — only US-304, US-305, US-402, and US-403 wait on EP-2) |
| 3 | EP-5, EP-6 | each other; EP-7 continues alongside | EP-2 and EP-3 |
| 4 | EP-8 | — | EP-2 and EP-4 |

**Phases**, derived from that graph.

| Phase | Contents | Relative estimate |
| --- | --- | --- |
| **P1 — Foundation and pricing** | EP-1 in full (US-101 … US-105) and EP-7 in full (US-701 … US-704). The two share no file: EP-1 is Cosmos and `Program.cs`, EP-7 is SQL and the model catalog | ~1.5 weeks |
| **P2 — The new transcript** | EP-2 US-201 … US-204 and US-206, EP-3 US-301 … US-304, EP-4 US-401 … US-403. **This is the minimum viable release** — the point at which a conversation is stored per message with a context cost and a rendered form, and the cutover becomes possible | ~2.5 weeks |
| **P3 — Cutover** | EP-2 US-207, gated and run by an operator against a non-production environment first. Nothing else lands in this phase; it is a deployment step with a runbook | ~2 days |
| **P4 — Budgeting and measurement** | EP-6 US-601 … US-603, EP-5 US-501 and US-502, EP-3 US-305, EP-2 US-205 | ~1.5 weeks |
| **P5 — Tuning and export** | EP-5 US-503, EP-6 US-604, EP-8 US-801 and US-802 | ~1 week |

EP-7 is scheduled first precisely because it depends on nothing: it is the only epic that can absorb slack while EP-1 is being proved against the emulator, and it is the only one whose value — a price on the catalog — is deliverable without any Cosmos work at all.

**Risks & mitigations.**

| Risk | Mitigation |
| --- | --- |
| The cutover destroys every existing transcript, irreversibly | US-207 makes it a gated operator action with a runbook whose first paragraph states the loss, and it is its own phase so it is never bundled with a feature deploy. The old container is retired in a separate later step, so scheduling is reversible even though the data is not |
| Hierarchical partition keys, transactional batches, and ordered cross-document queries are unproven here — the only Cosmos coverage today is `FakeAzureCosmosService`, which explicitly does not interpret patch operations | US-105 stands up the real Linux emulator in Testcontainers before US-207 destroys anything, and its criteria target exactly the three things a fake cannot prove |
| `DeleteAllItemsByPartitionKeyStreamAsync` is a background operation that may require account-level enablement, and it rejects a partial hierarchical key | US-206 requires the complete `(userId, conversationId)` key and refuses a partial one before the request leaves the process. Enablement is an open question in §9; the fallback is a paged id query plus batched deletes, which the US-104 surface already supports |
| US-205 changes the transcript endpoint's default from "every message" to "the newest 100", which is a breaking change for `enterprise-gpt-ui/` | The story names the consumer explicitly and is P1 rather than P0, so it lands after the client's transcript store exists rather than under it |
| The tiktoken estimate undercounts Claude by 15–20% on prose and more on code, so a budget built on it could still overflow on Bedrock and Anthropic | EP-5 measures the error before EP-6 relies on it, and US-503's multiplier is the correction. Until it is tuned, the §6 pass threshold applies to Azure OpenAI only, and the non-OpenAI threshold is an open question rather than an asserted number |
| Cost is an approximation by construction, because tool tokens can only be priced at the driving model's rate | Stated on the computed member itself in US-704 and recorded as an assumption in §9, so no report is built on it believing otherwise |
| `Database.Migrate()` runs at startup and US-701 is the first schema change since migrations began existing | US-701's criteria include applying the migration against a real SQL Server 2025 container, and the compatibility-level failure mode is named in the runbook rather than discovered in production |
| Switching the Cosmos serializer changes the JSON contract for every document type at once | US-102 lands before any document type changes, and its criteria assert the attribute-driven names round-trip and that the query-predicate mismatch recorded at `ConversationService.cs:289-291` does not recur |

**Rollout & rollback.** The cutover is the only irreversible step, and it is gated by configuration: until `CosmosOptions` names the new container, the application refuses to start rather than running against a shape it cannot read, so a half-configured deployment fails loudly at boot instead of quietly at the first turn. Every epic after EP-2 is additive and independently deployable — EP-3, EP-4, EP-5, EP-6, and EP-8 each add fields, metrics, or routes without changing an existing contract, so any one of them can be backed out by reverting its own deployment. EP-7 is additive on the SQL side: nullable columns and an unmapped getter, with a `DROP COLUMN` as the back-out. The back-out for EP-6 specifically is a configuration switch that sets every model's effective budget to unbounded, restoring today's unbounded replay without a deployment.

## 9. Assumptions & open questions

**Assumptions.**

- Prices are stored per **1,000,000** tokens rather than per 1,000, because current provider price sheets are quoted that way; the gap note at `usage-and-favorites.md:714` says "per 1K tokens" and is being deliberately departed from.
- A single currency is assumed, and it is USD. No currency column is added, and a multi-currency deployment would need one.
- Price columns are **nullable**, so "unpriced" stays distinguishable from "free" — the same lesson as "Null is not zero" (`usage-and-favorites.md` §3.6).
- Tool tokens can only be priced at the **driving model's** rate. Nothing in a tool call names the model behind it: `ConversationUsageToolCall.ModelId` is null on every row this application can write (`usage-and-favorites.md:715`). Cost is therefore an approximation by construction, which matches the stated goal.
- `htmlContent` is an **export-fidelity artifact**, not a byte-identical mirror of what the UI shows. The client renders through `ngx-markdown` over marked and Prism; the two will differ in whitespace, syntax-highlighting markup, and extension support.
- `htmlContent` is rendered for **every** persisted message, including user prompts, so an export has one rendering path rather than two.
- Recreating the container **destroys every existing transcript**. US-207 makes that an explicit, gated operator action with a runbook; the SQL side — conversations, usage rows, tool-call trees, favorites, projects — survives untouched.
- `docs/conversations/usage-and-favorites.md` §6.5 is **stale** in stating that `Enterprise.Gpt.Repository/Migrations/` is empty. `20260811024339_InitialCreate.cs` exists, so US-701 adds a real migration and the doc needs correcting when it is next revised.
- The per-turn tool-schema overhead is modeled as a configured cost **per attached tool**, not per server and not by serializing each schema and counting it. A schema-accurate count is more work than the accuracy is worth until EP-5 says otherwise.
- Cosmos continues to authenticate with an account-key connection string. Moving to `DefaultAzureCredential` is the project standard and is deliberately out of scope here, because it is a deployment change with its own rollout.
- `ChatRoles.Tool` remains unused in the transcript. Tool messages are never transcribed today and this project does not start; the enum value stays because the enum is shared.

**Open questions.**

- What accuracy threshold should Amazon Bedrock and Anthropic estimates be held to once US-503's multipliers are tuned? The Azure OpenAI threshold in §6 is derivable because tiktoken is that provider's own tokenizer; the other two need a number chosen from the US-502 data. **Owner**: platform owner, after two weeks of US-502 metrics.
- Is delete-by-partition-key enabled on the target Cosmos accounts? It is a background operation that has historically required account-level opt-in. If it is not, US-206 falls back to a paged id query plus batched deletes. **Owner**: platform operator, before US-206 starts.
- What is the retention policy for a purged conversation's SQL audit rows? Today nothing prunes `Core.ConversationUsage` and purging a conversation does not touch it, deliberately. Now that a conversation's transcript can be destroyed in one call, the mismatch between the two is more visible. **Owner**: platform owner.
- Should prices be versioned with an effective date rather than snapshotted per row? Snapshotting answers "what did this call cost"; a price history would additionally answer "what would last quarter cost at today's rates". **Owner**: platform analyst.
- Should the estimator use the Anthropic `count_tokens` endpoint for an `Exact` accuracy on a background reconciliation pass, rather than only on the write path where it does not belong? **Owner**: backend engineer, after EP-5 quantifies the error.
