# Conversation Transcript Storage and Tokenization

End-to-end reference for how a conversation is stored in Cosmos DB, what each message costs as context, how a turn's replayed history is trimmed to fit the model, and how a conversation leaves the platform as a file. Covers the transcript container and its two document shapes, the write and read paths, the `TokenEstimation` and `ContextBudget` configuration sections, server-side markdown rendering, and the export route. Audience: engineers maintaining conversations, and operators configuring a deployment.

Companions: [Conversation Streaming Contract](streaming-contract.md) for what a turn writes to the client while it runs, [Conversation Usage and Favourites](usage-and-favorites.md) for what it records in SQL, and [Transcript Cutover Runbook](transcript-cutover-runbook.md) for the one-time operator action that moves a deployment onto this storage shape.

## 1. Overview

A conversation used to be **one Cosmos document** holding every message it ever contained, in an embedded `messages[]` array. Three things followed from that, and all three were getting worse:

1. **A conversation's length was a storage ceiling.** Every turn rewrote or appended to one item, and Cosmos caps an item at 2 MB. A long conversation was on a collision course with that limit, and the collision would land mid-stream, after the answer had already been sent.
2. **Every read was the whole conversation.** Opening a conversation transferred every message ever exchanged, and replaying it to the model sent every message on every turn — with no idea whether the result fitted the model's context window until the provider rejected it.
3. **A message carried no cost and no rendering.** `Tokens = 0` was written literally on every message, so nothing could be budgeted; and there was no stored HTML, so exporting a conversation would have meant re-rendering it.

The transcript is now **one document per message plus a header document**, and each message carries what it costs as context and what it looks like rendered. On top of that:

| Concern | What changed |
|---|---|
| **Storage** | Header + one document per message, in a container partitioned on `(/userId, /conversationId)`. The largest item a conversation produces is bounded by its largest single **message** (§3, §4) |
| **Ordering** | An explicit `sequence`, allocated by incrementing a counter on the header **server-side**, so ordering does not depend on the process-local conversation lock (§4.3) |
| **Writes** | A turn's user message, assistant message and counter updates are **one transactional batch** (§4.4) |
| **Reads** | Prefix-routed to a single subpartition, ordered by `sequence`, and **paged from the newest end** (§5) |
| **Tokens** | Every message carries `tokens` and `tokenAccuracy`, counted by a provider-keyed estimator (§6) |
| **Budget** | Replayed history is trimmed to the model's context window before the turn is sent (§7) |
| **Rendering** | Every message carries `htmlContent`, rendered by Markdig with raw HTML disabled (§8) |
| **Export** | `GET api/conversations/{id}/export?format=html|json` (§9) |
| **Metrics** | Estimator error and what the budget dropped, on the `Enterprise.Gpt.Chat` meter (§10) |

> **This shape cannot read the old container, and the old container's data is not migrated.** Moving a deployment onto it destroys every existing transcript. That is a gated operator action with its own document — [Transcript Cutover Runbook](transcript-cutover-runbook.md) — and the gate is a startup check that **rejects** the retired `CosmosDb:ContainerId` setting (§3.3).

### 1.1 Where each piece lives

| Concern | Where |
|---|---|
| Container provisioning and verification | [`CosmosContainerBootstrap.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/CosmosContainerBootstrap.cs) |
| Cosmos settings and the cutover gate | [`Settings/CosmosOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/CosmosOptions.cs), validated in [`Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) |
| Storage operations (batches, paged queries, purge) | [`AzureCosmosService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/AzureCosmosService.cs), [`CosmosBatchOperation.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/CosmosBatchOperation.cs), [`CosmosPage.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/CosmosPage.cs) |
| Document types | [`CosmosTranscript.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/CosmosTranscript.cs) |
| Turn persistence, replay, transcript reads | [`ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs) |
| Token estimation | [`Tokenization/`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization) — `TokenEstimator`, `TokenEstimatorResolver`, `TiktokenTokenizerCache`, `PromptEstimator` |
| Context budget | [`Tokenization/ContextBudgetCalculator.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/ContextBudgetCalculator.cs) |
| Markdown rendering | [`Rendering/MarkdownRenderer.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Rendering/MarkdownRenderer.cs) |
| Export | [`Export/ConversationExportService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ConversationExportService.cs) |
| Metrics | [`Observability/ChatMetrics.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Observability/ChatMetrics.cs) — see [Request Logging §7.3](../observability/request-logging.md#73-llm-spans-and-token-metrics) |

## 2. Quick start

**Configure the transcript container.** `CosmosDb:ContainerId` is retired and now fails startup; `CosmosDb:TranscriptContainerId` replaces it:

```json
"CosmosDb": {
  "ConnectionString": "AccountEndpoint=https://…;AccountKey=…;",
  "DatabaseId": "enterprise-gpt",
  "TranscriptContainerId": "enterprise-gpt-transcripts"
}
```

The container is created on first start if it does not exist, and verified if it does (§3.3).

**Read the newest page of a conversation**, then walk backwards:

```http
GET /api/conversations/9c1f1c8e-6a1e-4c1a-9f7a-2b3c4d5e6f70/messages
Authorization: Bearer <token>
```

```json
{
  "id": "9c1f1c8e-6a1e-4c1a-9f7a-2b3c4d5e6f70",
  "name": "Q3 pricing questions",
  "dateCreated": "2026-08-01T09:14:23.117+00:00",
  "dateModified": "2026-08-01T10:02:11.884+00:00",
  "totalMessageCount": 41,
  "hasMore": true,
  "messages": [
    {
      "sequence": 21,
      "text": "What did we charge in Q3?",
      "role": 3,
      "htmlContent": "<p>What did we charge in Q3?</p>\n",
      "tokens": 7,
      "tokenAccuracy": "Estimated"
    }
  ]
}
```

```http
GET /api/conversations/{id}/messages?before=21&take=50
```

**Take a conversation out of the platform:**

```http
GET /api/conversations/{id}/export?format=html
→ 200 text/html; charset=utf-8, Content-Disposition: attachment; filename="q3-pricing-questions.html"
```

**Turn context budgeting off** without a deployment, if the estimator ever misbehaves:

```json
"ContextBudget": { "Enabled": false }
```

## 3. The transcript container

### 3.1 A hierarchical partition key

The container is partitioned on **`/userId` then `/conversationId`**, in that order, at `PartitionKeyDefinitionVersion.V2`. The retired container used `/userId` alone. The hierarchy earns its place three ways:

- **Reads are prefix-routed.** Supplying both levels addresses exactly one subpartition, so reading a transcript never fans out across the container however many conversations it holds — and never filters a whole user's documents to find one conversation's.
- **The 20 GB logical-partition ceiling applies per conversation** rather than per user. A heavy user can no longer exhaust a partition on behalf of every conversation they own.
- **A purge is addressable.** Deleting one conversation's documents is a complete-partition-key operation rather than a query over a user's whole history.

That is why **every member of `IAzureCosmosService` takes `userId` and `conversationId`** rather than a pre-built `PartitionKey`: a cross-partition query and a partial-key purge become impossible to express, instead of mistakes caught at run time.

A conversation belonging to another user resolves to a **different partition key**, so a read for it cannot reach it at all. That is what keeps a foreign conversation a `404` rather than a `403` on the transcript and export routes, with no extra ownership check in the read path.

### 3.2 Indexing policy

Everything is indexed under the `/*` wildcard **except** `/content/*` and `/htmlContent/*`.

Those two properties are the bulk of a document's bytes and are never filtered on — queries touch `type`, `conversationId`, `sequence` and `role`, all of which stay indexed. Indexing message bodies is pure write RU and storage.

### 3.3 Startup bootstrap, and the two ways it fails

[`CosmosContainerBootstrap.EnsureTranscriptContainerAsync`](../../enterprise-gpt-api/Enterprise.Gpt.Service/CosmosContainerBootstrap.cs) runs once at startup (skipped in the `Testing` environment) and is shared with the integration fixture, so tests provision an identical container rather than one that merely resembles production.

It reads the container first and creates it only on a `404`, rather than calling `CreateContainerIfNotExistsAsync` — which returns an existing container **without checking that its key paths match**, and that check is the whole point.

| Situation | Outcome |
|---|---|
| Container absent | Created with the partition key paths and indexing policy above, logged at Information |
| Container present, partition key paths match | Verified; the indexing policy is converged if it has drifted |
| Container present, partition key paths **differ** | **Startup fails** with an `InvalidOperationException` naming both path sets |
| Indexing policy cannot be updated | **Logged as a Warning; startup continues** |

The asymmetry between those last two rows is deliberate. A wrong partition key means this application cannot address its own documents — reads silently match nothing — so it must not start. Indexing drift costs request units, not correctness; and some backends, the Linux emulator among them, accept a custom indexing policy without applying it, so failing on it would make every local run unstartable.

The bootstrap creates the container **without specifying throughput**, so it takes the database's shared throughput. On a database with none — or where you want dedicated RU/s — create the container yourself with exactly `/userId` then `/conversationId`, and startup will verify it instead of creating it.

### 3.4 Serialization

The `CosmosClient` is configured with `UseSystemTextJsonSerializerWithOptions` ([`CosmosSerialization.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/CosmosSerialization.cs)) rather than the SDK's Newtonsoft default. The `[JsonPropertyName]` attributes on the document types are therefore the **naming contract**, instead of coinciding with a camel-casing policy that happens to agree with them.

That matters for queries as much as for documents: a query predicate names a JSON path, and a path that agrees with the serializer only by coincidence is one package upgrade away from matching nothing.

### 3.5 The configuration section

`CosmosDb` binds to [`CosmosOptions`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/CosmosOptions.cs) and is validated with `ValidateOnStart`, so a misconfigured deployment fails at boot rather than on a user's first conversation.

| Key | Required | Meaning |
|---|---|---|
| `ConnectionString` | yes | The account connection string. An **account key** — the one place this application does not follow its `DefaultAzureCredential` preference. Moving to managed identity is a deployment change with its own rollout; this property is the seam it would replace |
| `DatabaseId` | yes | The database holding the transcript container |
| `TranscriptContainerId` | yes | The transcript container |
| `ContainerId` | **must be absent** | The retired single-document container. Bound **only so validation can reject it** (§3.6) |

### 3.6 `CosmosDb:ContainerId` is the cutover gate

Configuring `CosmosDb:ContainerId` fails startup with:

```text
CosmosDb:ContainerId is retired and names a transcript shape this application cannot read. Remove it and configure CosmosDb:TranscriptContainerId with the new container.
```

It is rejected rather than ignored because it names the retired one-document-per-conversation container, whose shape this application can no longer read. A deployment that still configures it **has not performed the cutover**, and starting it would run a live system against data it would misinterpret. There is no separate operator switch: the config-key rename *is* the gate. See the [Transcript Cutover Runbook](transcript-cutover-runbook.md).

## 4. The documents

Two shapes share the container, told apart by a `type` discriminator, and every document carries `schemaVersion` (currently `1`) so a future shape change has something to read rather than having to infer a version from which properties happen to be present.

### 4.1 The header — `type: "conversation"`

One per conversation, with the conversation's own id as its `id`, so it is reachable by **point read** rather than by query.

| Property | Type | Notes |
|---|---|---|
| `id` | guid | The conversation id |
| `userId`, `conversationId` | guid | The two partition key levels |
| `type` | string | `conversation` |
| `name` | string | Patched by rename and by first-turn naming |
| `totalTokens` | number | Incremented by each turn |
| `messageCount` | number | How many positions have been **allocated** — also the sequence allocator (§4.3) |
| `schemaVersion` | number | `1` |
| `dateCreated`, `dateModified` | ISO 8601 | |
| `dateDeactivated` | ISO 8601 or null | Soft delete. Set on the header only; message documents stay in place, which is what keeps a soft delete reversible |

### 4.2 A message — `type: "message"`

| Property | Type | Notes |
|---|---|---|
| `id` | guid | The message id. On an assistant message this is the `AssistantMessageId` on the SQL usage row |
| `userId`, `conversationId` | guid | The two partition key levels |
| `type` | string | `message` |
| `sequence` | number | Zero-based position. **Gaps are legal** (§4.5), so it is an ordering key, not an index |
| `role` | string | `system`, `user` or `assistant` — a **lowercase string**, not the enum's number, so an exported document reads on its own and matches what the stream already serializes |
| `content` | string | The message text. Not indexed (§3.2) |
| `htmlContent` | string or null | The server-rendered form (§8). Null on a message written before rendering existed. Not indexed |
| `tokens` | number | What this message costs as **context** on every future turn (§6.1). Zero for a message written before estimation existed |
| `tokenAccuracy` | string | `Estimated` or `Exact`, serialized by name |
| `model` | string or null | The **deployment name** that served the turn, snapshotted. Null on the seeded system message |
| `providerId` | guid or null | The provider that served the turn |
| `usage` | object or null | `{ inputTokens, outputTokens }`, on assistant messages only — what the **turn** was billed, tools included (§4.6) |
| `dateCreated` | ISO 8601 | A turn's user and assistant messages share one timestamp, which is why `sequence` exists |
| `schemaVersion` | number | `1` |

A conversation is created with its header and its seeded system message (position `0`) in **one batch**, so a header whose counter says a message exists is never readable before the message it counts.

### 4.3 Sequences are allocated server-side

A turn claims its two positions by patch-incrementing `/messageCount` on the header by 2 and **reading the new value back**:

```text
PatchItemAsync(headerId, [Increment("/messageCount", 2)])  →  header.MessageCount
  assistantSequence = MessageCount - 1
  userSequence      = MessageCount - 2
```

The increment happens at the server and the response carries the value *this* caller was allocated, which makes ordering independent of the conversation lock. That matters because the lock is **process-local**: two replicas answering the same conversation get no mutual exclusion from it, and would otherwise be free to claim the same position.

`PatchItemAsync` returns the patched document for exactly this reason — a counter is only usable as an allocator if the caller can read back what it was given.

### 4.4 A turn is one transactional batch

Persisting a completed turn submits three operations against the conversation's partition key:

```text
ExecuteBatchAsync(userId, conversationId,
  ├─ CreateItem(user message,      sequence = n)
  ├─ CreateItem(assistant message, sequence = n + 1)
  └─ PatchItem(header, [Increment("/totalTokens", …), Set("/dateModified", …)]))
```

Both messages and the header's totals move together, so a header whose totals moved always has the messages that moved them. The header is **patched, not replaced**, so a rename or a soft delete that landed mid-stream is not reverted — the failure a full-document replace once caused here was overwriting the entire transcript with the relational entity's shape.

A batch is described as a list of records ([`CosmosBatchOperation`](../../enterprise-gpt-api/Enterprise.Gpt.Service/CosmosBatchOperation.cs)) rather than built through the SDK's fluent `TransactionalBatch`, which is abstract and cannot be meaningfully observed by a test double. The service translates the records onto the real builder; callers assert on the shape they submitted.

Limits are enforced before the request leaves the process: at most **100 operations** per batch and **10 operations** per patch, both of which are Cosmos's own ceilings.

When a batch is rejected, every sibling of the offending operation comes back as `FailedDependency`, so the first status that is *not* that one names the operation that actually broke it. [`CosmosBatchFailedException`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Exceptions/CosmosBatchFailedException.cs) carries that index and status, and states plainly that **nothing in the batch was written**.

### 4.5 What a failed write leaves behind

Persistence runs **after the answer has streamed**, so nothing here can be reported to the caller. Two failures are handled by logging rather than throwing, both with the same reasoning: SQL has already committed the turn's counters and its usage row, so the pair of identifiers in the log is the only way to reconcile a usage row against a transcript that never received its message.

| Failure | Effect |
|---|---|
| The header is missing when sequences are allocated | Nothing is written; logged at Error with the conversation id, the usage row id and the assistant message id |
| The batch is rejected (`CosmosBatchFailedException` or `CosmosException`) | Nothing is written; logged at Error with the same three identifiers |

A turn that allocated positions and then failed to write leaves a **gap** in the sequence. That is why ordering is by ascending `sequence` rather than by contiguity, and why nothing derives a message count from the highest sequence.

### 4.6 `usage` on a message is not the SQL split

`CosmosConversationUsage` carries `inputTokens` and `outputTokens` meaning **the whole turn** — the assistant's own model turns plus every tool, MCP call and agent underneath them. The identically named columns on `Core.ConversationUsage` hold the assistant's share alone, with the tool share in `ToolInputTokens`/`ToolOutputTokens` ([usage §3.5](usage-and-favorites.md#35-totaltokens-is-computed-and-now-spans-tools)).

The difference is deliberate: what belongs beside a message in a transcript is what the turn cost, and the breakdown belongs in the relational audit trail. Both figures derive from the same numbers, so they cannot disagree about the total.

## 5. Reading a transcript

### 5.1 `GET api/conversations/{id}/messages`

> **Breaking change.** This route used to return **every** message. It now returns **the newest 100** by default. A client that rendered the whole array will silently show only the tail of a long conversation until it implements paging.

| Parameter | Type | Default | Behaviour |
|---|---|---|---|
| `take` | int | `100` | **Clamped** server-side to 1–100, matching every other paginated list route here — an out-of-range value on a query string is a client mistake to absorb, not a request to reject |
| `before` | long | — | Return the page immediately preceding this `sequence`. Omit it for the newest page |

A non-numeric `take` or `before` fails **parameter binding**, which produces a `400` `/problems/validation-error` naming the parameter before the handler runs.

The response is `ChatConversationDto`:

| Field | Meaning |
|---|---|
| `messages` | One page, **oldest first** within the page — the query runs newest-first off the tail and the page is reversed for display |
| `totalMessageCount` | The header's allocated position count. It counts **every** message ever written, including the system message this route never returns, so it is a progress indicator rather than the length of a filtered list |
| `hasMore` | Whether older messages remain, taken from the query's continuation token |
| `id`, `name`, `dateCreated`, `dateModified` | Read from the **header document**, not from SQL |

`projectId`, `modelId` and `isFavorite` are inherited from `ConversationDto` and are **not populated by this route** — they come from `GET api/conversations/{id}` ([usage §5.1](usage-and-favorites.md#51-the-resume-shape--conversationdetaildto)).

Each message now carries `sequence`, `htmlContent`, `tokens` and `tokenAccuracy` alongside `text` and `role`. Three notes for a client:

- **`sequence` is the cursor.** Pass the lowest `sequence` in a page back as `before` to fetch the page before it.
- **`htmlContent: null` does not mean empty.** It means the message was stored before rendering existed, and the client must render `text` itself.
- **`tokens` on an assistant message is not what the turn was billed.** It counts the transcribed answer only, excluding tool-call arguments the model generated and which are never replayed.

System messages are filtered out **server-side**: the system message is the largest fixed message in a conversation and no client renders it.

Paging by `before` re-runs the query rather than following a continuation token, so a page is reproducible and a client can jump backwards without holding an opaque cursor.

### 5.2 Replay reads the whole transcript

The streaming path reads **every** message, oldest first, over `TranscriptPageSize` (200) documents per round trip, because the budget in §7 has to see every candidate before deciding which of them fit. That read is prefix-routed to one subpartition, so its cost scales with the conversation rather than with the container.

Trimming decides what is **sent**. It never decides what is **stored**: the transcript keeps every message whatever the budget drops.

## 6. Tokenization

### 6.1 `tokens` is a context cost, not a billed cost

The number stored on a message is a **pure function of its own text** — deterministic, recomputable, and equal to what the message will contribute to the prompt on every future turn. What a turn was actually *charged* is only knowable from the provider and lives on the SQL audit trail.

The two answer different questions, and conflating them is the mistake this split exists to prevent: an assistant message's `tokens` deliberately excludes tool-call arguments the model generated, because those are never replayed.

### 6.2 Estimator selection

`ITokenEstimator` is registered **keyed by provider**, from the same registry the chat clients use, plus one unkeyed default. [`TokenEstimatorResolver`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/TokenEstimatorResolver.cs) resolves the model's provider to its estimator.

It **falls back rather than throwing**, which is where it parts company with `ChatClientResolver`: a turn without a chat client cannot run, but a turn without an estimator can — it just records a less exact number. Failing the turn would trade a small inaccuracy for a total outage. A provider that falls back is logged once, not per turn.

Counting is `tiktoken` through `Microsoft.ML.Tokenizers` 2.0.0, with both the `Cl100kBase` and (new in this release) `O200kBase` vocabularies referenced, because the modern OpenAI chat families use `o200k_base` and the previously installed vocabulary could not count them.

[`TiktokenTokenizerCache`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/TiktokenTokenizerCache.cs) resolves a **deployment name** to a tokenizer and caches it, because building one loads and indexes a large vocabulary. Deployments are cached separately from encodings, so several deployments resolving to the same encoding share one loaded vocabulary. A deployment name that the library does not recognize as a model identifier falls back to `TokenEstimation:FallbackEncoding` and is logged **once** — deployment names are operator-chosen aliases, so this is the ordinary case rather than a fault.

A tokenizer fault while persisting a message records **zero** and logs, for the same reason §4.5 swallows a write failure: the answer has already streamed, and losing a turn over a token count trades something valuable for something cosmetic.

### 6.3 The tokens no message owns

Three things cost input tokens and belong to no message, so [`PromptEstimator`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/PromptEstimator.cs) counts them as a separate **overhead** term:

```text
overhead = toolCount    × TokenEstimation:PerToolSchemaTokens
         + messageCount × TokenEstimation:PerMessageFramingTokens
         + count(request instructions)
```

Leaving them out would make the sum of the messages permanently **under-report** the prompt, which is the one error a context budget cannot absorb.

The tool term is charged per attached tool rather than measured by serializing each schema. With several MCP servers attached that is easily thousands of tokens; a configured constant is less accurate than counting the real schemas and far cheaper, and §10's reconciliation metric is what says whether the difference matters.

A message with a stored `tokens` greater than zero is counted at its stored value. A stored zero on non-empty content means the message **predates estimation**, so it is counted on the fly rather than treated as free.

### 6.4 The `TokenEstimation` section

Binds to [`TokenEstimationOptions`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/TokenEstimationOptions.cs). Absent from `appsettings.json`, so a deployment that configures nothing runs on the defaults below. Validated with `ValidateOnStart`.

```json
"TokenEstimation": {
  "FallbackEncoding": "cl100k_base",
  "PerToolSchemaTokens": 350,
  "PerMessageFramingTokens": 4,
  "MaxCalibrationMultiplier": 3.0,
  "CalibrationMultipliers": {
    "8c4b1f27-6a3d-4d59-9e21-b0f4a7c6d812": 1.18,
    "6f93ec17-e981-409f-a523-700584f1e7d6": 1.18
  }
}
```

| Key | Default | Meaning |
|---|---|---|
| `FallbackEncoding` | `cl100k_base` | The encoding used when a deployment name is not a model identifier the tokenizer library recognizes |
| `PerToolSchemaTokens` | `350` | Prompt tokens attributed to each **attached tool** for the schema injected on its behalf |
| `PerMessageFramingTokens` | `4` | Prompt tokens attributed to each message for the chat framing around it |
| `CalibrationMultipliers` | *(empty)* | Per-provider correction applied to the raw count, **keyed by provider id**. A provider with no entry is estimated at `1.0` |
| `MaxCalibrationMultiplier` | `3.0` | The largest multiplier accepted at startup |

Validation rules, each failing the app at boot with a message naming the setting:

| Rule | Message |
|---|---|
| `FallbackEncoding` non-empty | `TokenEstimation:FallbackEncoding must be configured.` |
| `PerToolSchemaTokens` ≥ 0 | `TokenEstimation:PerToolSchemaTokens must not be negative.` |
| `PerMessageFramingTokens` ≥ 0 | `TokenEstimation:PerMessageFramingTokens must not be negative.` |
| `MaxCalibrationMultiplier` > 0 | `TokenEstimation:MaxCalibrationMultiplier must be greater than zero.` |
| every multiplier in `(0, MaxCalibrationMultiplier]` | the generic options-validation failure for the predicate |

The provider ids to key `CalibrationMultipliers` by are the seeded `Core.Ref.Provider` values in [`Providers.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/Providers.cs): Azure OpenAI `3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0`, Amazon Bedrock `8c4b1f27-6a3d-4d59-9e21-b0f4a7c6d812`, Anthropic `6f93ec17-e981-409f-a523-700584f1e7d6`.

### 6.5 Why calibration is configuration

`tiktoken` is **OpenAI's** tokenizer. It is exact for Azure OpenAI and an approximation everywhere else — it undercounts Claude by roughly 15–20% on prose and more on code. Correcting that belongs to measured traffic rather than to a constant someone guessed, which is why the multipliers are configuration and why §10's reconciliation metric closes the loop: changing a multiplier moves the number that metric records.

A multiplied count is rounded **up**, not to nearest: the estimate feeds a budget, and erring high leaves headroom where erring low sends a prompt the model rejects.

The ceiling exists so a mistyped multiplier — `18` for `1.8` — fails at boot rather than silently tripling every budget and every stored count.

## 7. The context budget

### 7.1 What fits

[`ContextBudgetCalculator`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/ContextBudgetCalculator.cs) is a pure function of a model, a candidate list and the overhead term, so the trimming decision is testable without a chat client.

```text
budget = model.ContextWindowSize − model.MaxOutputTokens − overhead
```

`MaxOutputTokens` is subtracted because the answer has to fit in the window alongside the prompt.

If the candidates already fit, nothing is dropped. Otherwise the **oldest droppable** messages leave first, until the remainder fits.

### 7.2 What never leaves, and what leaves in pairs

- **The system message stays**, when the conversation has one. It is the standing instructions.
- **The current prompt stays.** It is the question being asked; dropping it answers a different question.
- **A user message leaves with the assistant reply to it.** A reply left behind shows the model an answer to a question it can no longer see.

Kept messages stay in their original order.

### 7.3 A model with no context window is unbounded

`ContextWindowSize` of `0` is the seeded default for a catalog row nobody has filled in. Treating it as a budget would reject **every** turn on that model, so it means "unknown" rather than "none": the turn is sent untrimmed, and the model is logged once at Warning naming the deployment and the catalog field to set.

### 7.4 When even the undroppable messages overflow

When the system message and the current prompt alone exceed the budget, no amount of trimming makes the turn sendable. It fails **before the stream starts**, with a `400` `/problems/validation-error` keyed on `Prompt`:

```text
This prompt does not fit the selected model's context window of 200000 tokens. Shorten it or choose a model with a larger window.
```

This is a genuine pre-stream failure and arrives as ordinary problem JSON — see [streaming contract §3.3](streaming-contract.md#33-errors-before-and-after-the-first-frame). Failing here is deliberate: the alternative is sending a prompt the model will refuse, which costs a round trip and surfaces as a provider error the user cannot act on.

### 7.5 `ContextBudget:Enabled` is the back-out

```json
"ContextBudget": { "Enabled": false }
```

Setting it to `false` restores **unbounded replay** — every message sent on every turn, exactly as the system behaved before budgeting existed — without a deployment. That is what makes a mis-tuned estimator a configuration change rather than an incident.

Two consequences of turning it off, both worth knowing before reaching for it:

- The `400` in §7.4 can no longer occur; an over-long conversation goes back to being rejected by the provider.
- **No prompt estimate is produced**, so §10's estimator-accuracy histogram records nothing. Turning budgeting off also turns off the signal you would use to decide whether to turn it back on.

Options are injected as `IOptions<T>`, so a change needs a restart.

## 8. Server-rendered HTML

### 8.1 What is being rendered is untrusted

Model output is attacker-influenceable — through uploaded documents and through MCP tool results — so what [`MarkdownRenderer`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Rendering/MarkdownRenderer.cs) renders is untrusted input, not the platform's own copy. Two defences apply:

1. **Raw HTML passthrough is disabled** (`DisableHtml()`), so `<script>` in model output is **escaped into text** rather than emitted as an element.
2. **Link URLs are restricted to `http`, `https` and `mailto`.** Markdig does not filter schemes on its own, so a `javascript:` href would otherwise survive into a stored artifact. The allowlist is applied to the parsed document before rendering, and a rejected URL is emptied rather than the link being removed.

Scheme matching decodes HTML entities first — an entity-encoded scheme still executes once a browser parses the `href` — and treats a colon appearing after a `/`, `?` or `#` as part of a path or query rather than a scheme, so relative links pass.

The pipeline is `UseAdvancedExtensions()` plus `DisableHtml()`, built once as a singleton. The per-render `HtmlRenderer` is constructed per call, because it holds per-render state and is not thread-safe.

### 8.2 Where it is stored, and what it is for

`htmlContent` is rendered for **every** persisted message, user prompts included, so an export has one rendering path rather than two. It is stored so an export is a **read rather than a re-render**.

It is an export-fidelity artifact, **not** a byte-identical mirror of what the web client shows: the client renders through `ngx-markdown` over marked and Prism, and the two differ in whitespace, syntax-highlighting markup and extension support.

A renderer fault stores the **escaped plain text** instead of propagating — same reasoning as §4.5 and §6.2, since this runs after the answer has streamed.

## 9. Export

```http
GET /api/conversations/{id}/export?format=html|json
```

Served as an **attachment**, since a browser rendering it in place would defeat a "take my conversation with me" affordance.

| Aspect | Behaviour |
|---|---|
| Ownership | The SQL row must be the caller's and active; a foreign or unknown id is a `404` `/problems/resource-not-found`, never a `403` |
| `format` | Required, matched case-insensitively after trimming. Anything else is a `400` `/problems/validation-error` keyed on `format`, naming both supported values |
| Contents | Every **non-system** message, in `sequence` order, read 200 documents per round trip |
| Empty conversation | A valid document with an empty body, not a `404` |
| File name | The conversation name slugified to `[A-Za-z0-9-_. ]`, clipped to 60 characters, falling back to `conversation-{id}.{format}` |

**HTML** (`text/html; charset=utf-8`) is a self-contained document with the conversation name as its title, a small embedded stylesheet, and one `<section>` per message carrying the role and timestamp. The stored `htmlContent` is embedded **verbatim** — safe precisely because §8.1 produced it with raw HTML disabled. A message with no stored HTML is **escaped into the output rather than omitted**, since dropping it would silently shorten the export.

**JSON** (`application/json; charset=utf-8`) uses the **stored document's** property names rather than the HTTP DTOs', so the export is the storage shape rather than a third representation to keep in step:

```json
{
  "name": "Q3 pricing questions",
  "dateCreated": "2026-08-01T09:14:23.117+00:00",
  "dateModified": "2026-08-01T10:02:11.884+00:00",
  "messages": [
    {
      "sequence": 1,
      "role": "user",
      "content": "What did we charge in Q3?",
      "htmlContent": "<p>What did we charge in Q3?</p>\n",
      "tokens": 7,
      "tokenAccuracy": "Estimated",
      "model": "rr-gpt-5.6-luna",
      "dateCreated": "2026-08-01T09:15:02.441+00:00"
    },
    {
      "sequence": 2,
      "role": "assistant",
      "content": "In Q3 …",
      "htmlContent": "<p>In Q3 …</p>\n",
      "tokens": 412,
      "tokenAccuracy": "Estimated",
      "model": "rr-gpt-5.6-luna",
      "dateCreated": "2026-08-01T09:15:02.441+00:00",
      "usage": { "inputTokens": 812, "outputTokens": 146 }
    }
  ]
}
```

`usage` appears on assistant messages only and is omitted when null. It carries the turn's totals and **no per-tool breakdown**: that detail lives in the relational audit trail and is exposed over HTTP nowhere.

## 10. Metrics

Three instruments publish on the `Enterprise.Gpt.Chat` meter — the same meter the chat pipeline's OpenTelemetry instrumentation already uses, so they export with no second registration. Full tag conventions are in [Request Logging §7.3](../observability/request-logging.md#73-llm-spans-and-token-metrics).

| Instrument | Kind | Records |
|---|---|---|
| `enterprisegpt.chat.token_estimate.relative_error` | histogram | `(estimate − reported) / reported`, **signed** |
| `enterprisegpt.chat.context_budget.dropped_messages` | counter | Messages the budget dropped |
| `enterprisegpt.chat.context_budget.dropped_tokens` | counter | What those messages held |

**Reconciliation runs only on turns where no tool ran.** The provider's reported input tokens then cover the same prompt the estimator saw; once a tool runs, that figure also includes the follow-up prompts carrying tool results, which the estimator never counted and never should — comparing them would measure a difference that is correct by design. It is arithmetic over numbers already in hand, so it adds no round trip.

The error is recorded **signed**, because a systematic undercount and noise around zero are the same number once you take the absolute value, and only the first is worth acting on.

## 11. Configuration reference

Everything this feature adds or changes, in one place.

| Section | Key | Default | Validated at startup |
|---|---|---|---|
| `CosmosDb` | `ConnectionString` | — | non-empty |
| `CosmosDb` | `DatabaseId` | — | non-empty |
| `CosmosDb` | `TranscriptContainerId` | — | non-empty |
| `CosmosDb` | `ContainerId` | — | **must be absent** (§3.6) |
| `TokenEstimation` | `FallbackEncoding` | `cl100k_base` | non-empty |
| `TokenEstimation` | `PerToolSchemaTokens` | `350` | ≥ 0 |
| `TokenEstimation` | `PerMessageFramingTokens` | `4` | ≥ 0 |
| `TokenEstimation` | `CalibrationMultipliers` | *(empty)* | each in `(0, MaxCalibrationMultiplier]` |
| `TokenEstimation` | `MaxCalibrationMultiplier` | `3.0` | > 0 |
| `ContextBudget` | `Enabled` | `true` | — |

Only `CosmosDb` appears in [`appsettings.json`](../../enterprise-gpt-api/Enterprise.Gpt.Api/appsettings.json). The other two sections run on their defaults until a deployment overrides them. All three are read once at startup, so every change needs a restart.

## 12. Testing

```bash
# from enterprise-gpt-api/
dotnet test --filter "Category!=Integration"   # unit only — 1,016 passing
dotnet test                                    # everything; Docker must be running
```

Unit coverage, xUnit v3 with NSubstitute:

| File | Asserted |
|---|---|
| [`Tokenization/TokenEstimatorTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Tokenization/TokenEstimatorTests.cs) | Deployment-to-tokenizer resolution and the fallback encoding; the calibration multiplier and its rounding; per-message and overhead estimation; resolver fallback for an unregistered provider |
| [`Tokenization/ContextBudgetCalculatorTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Tokenization/ContextBudgetCalculatorTests.cs) | The budget arithmetic; a prompt that already fits; the system message kept first and the prompt kept last; a user message dropped with its assistant reply; what the drop reports; a zero context window treated as unbounded; undroppable messages that overflow reported rather than trimmed away; kept order preserved |
| [`Rendering/MarkdownRendererTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Rendering/MarkdownRendererTests.cs) | A `<script>` escaped rather than emitted; an image event handler left with no element to hang off; a dangerous link scheme stripped and a safe one kept; a relative link containing a colon not mistaken for a scheme; fenced code, tables and inline formatting |
| [`Export/ConversationExportServiceTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Export/ConversationExportServiceTests.cs) | Unsupported format naming the supported ones; another user's conversation as `NotFound`; HTML embedding the stored rendering; a message with no stored HTML escaped instead; an empty conversation as a valid document; JSON using the stored property names; the download file name |
| [`Services/CosmosBatchOperationTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Services/CosmosBatchOperationTests.cs) | The batch-operation records and their translation shape |
| [`Observability/ChatMetricsTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Observability/ChatMetricsTests.cs) | Instrument names, units and tags, over a `DummyMeterFactory` |
| [`Services/ConversationServiceTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Services/ConversationServiceTests.cs) | Sequence allocation and the turn batch; the paged read, its clamp and its cursor; replay trimming and the pre-stream `400`; estimation and rendering on persisted messages |

Integration coverage runs against a **Linux Cosmos DB emulator in Testcontainers** ([`CosmosEmulatorContainer.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/TestInfrastructure/CosmosEmulatorContainer.cs)), alongside the existing SQL Server 2025 container. `FakeAzureCosmosService` is **deleted**: it could not apply patch operations, so it could not prove any of the behaviour this layer depends on — and hierarchical partition keys, transactional batches and ordered cross-document queries are exactly what an in-memory fake cannot show.

[`Cosmos/CosmosTranscriptStorageTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/Cosmos/CosmosTranscriptStorageTests.cs) asserts: a mixed batch committing together; a conflicting operation writing nothing and naming the failing index; paging in query order; **5,000 messages with every document bounded by its own content** rather than by the conversation's length; a purge leaving the user's other conversations intact; an annotated property stored under its annotated name and queryable by it; and the partition-key mismatch failing startup with both path sets named.

> **The integration suite was not executed for this release** — the development machine had no Docker daemon. The unit suite passes in full.

## 13. Known limits

- **Purge is fallback-only, and no route exposes it.** `PurgeConversationAsync` pages ids and deletes them in batches rather than calling `DeleteAllItemsByPartitionKeyStreamAsync`, which is a preview API that needs an account-level capability this deployment does not assume and is unavailable on the emulator the storage tests run against. It is a page of round trips instead of one, which a purge can afford. **`DELETE api/conversations/{id}` remains a soft delete** and does not call it; nothing in the HTTP surface does.
- **Purging leaves the SQL audit trail behind.** `Core.ConversationUsage` and its tool-call rows are never touched by a purge, deliberately — but now that a transcript can be destroyed in one call, the mismatch is more visible than it was.
- **Cosmos still authenticates with an account key.** `CosmosDb:ConnectionString` is the one place this application does not follow its `DefaultAzureCredential` preference.
- **Estimates are estimates.** Every `tokenAccuracy` this application writes today is `Estimated`; nothing produces `Exact`. Calibration multipliers for Amazon Bedrock and Anthropic are unset until §10's metric has enough traffic to tune them from.
- **The tool-schema overhead is a constant per tool**, not a count of the real schemas, so a deployment with unusually large tool schemas will under-report its prompt.
- **Turning budgeting off also turns off its own signal** (§7.5).
- **`htmlContent` is not what the client renders** (§8.2), and a message written before rendering existed has none at all.
- **Export has no size limit and no streaming.** A conversation is read in full and assembled in memory before the response starts.
- **Pre-cutover conversations are unreadable, not empty.** Their SQL rows survive, but every transcript read for them returns `404` — see the [Transcript Cutover Runbook §7](transcript-cutover-runbook.md#7-what-users-see-afterwards).

## 14. Key files

| Concern | File |
|---|---|
| Document types | [`Enterprise.Gpt.Entity/CosmosTranscript.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/CosmosTranscript.cs) |
| Storage operations | [`Enterprise.Gpt.Service/AzureCosmosService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/AzureCosmosService.cs) |
| Batch operations, paging, batch failure | [`CosmosBatchOperation.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/CosmosBatchOperation.cs), [`CosmosPage.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/CosmosPage.cs), [`Exceptions/CosmosBatchFailedException.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Exceptions/CosmosBatchFailedException.cs) |
| Container provisioning | [`Enterprise.Gpt.Service/CosmosContainerBootstrap.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/CosmosContainerBootstrap.cs) |
| Serializer options | [`Enterprise.Gpt.Service/CosmosSerialization.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/CosmosSerialization.cs) |
| Settings | [`Settings/CosmosOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/CosmosOptions.cs), [`Settings/TokenEstimationOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/TokenEstimationOptions.cs), [`Settings/ContextBudgetOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/ContextBudgetOptions.cs) |
| Registration and validation | [`Enterprise.Gpt.Api/Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) |
| Turn persistence, replay, reads | [`Enterprise.Gpt.Service/ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs) |
| Estimation | [`Tokenization/TokenEstimator.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/TokenEstimator.cs), [`TokenEstimatorResolver.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/TokenEstimatorResolver.cs), [`TiktokenTokenizerCache.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/TiktokenTokenizerCache.cs), [`PromptEstimator.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/PromptEstimator.cs) |
| Budgeting | [`Tokenization/ContextBudgetCalculator.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/ContextBudgetCalculator.cs) |
| Rendering | [`Rendering/MarkdownRenderer.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Rendering/MarkdownRenderer.cs) |
| Export | [`Export/ConversationExportService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ConversationExportService.cs), [`Enterprise.Gpt.Dto/ConversationExportDto.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/ConversationExportDto.cs) |
| Metrics | [`Observability/ChatMetrics.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Observability/ChatMetrics.cs) |
| Routes | [`Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs) |
| Transcript DTOs | [`Enterprise.Gpt.Dto/ConversationDto.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/ConversationDto.cs), [`ConversationMessageDto.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/ConversationMessageDto.cs) |
| Related reference | [Cutover Runbook](transcript-cutover-runbook.md), [Streaming Contract](streaming-contract.md), [Usage and Favourites](usage-and-favorites.md), [Request Logging](../observability/request-logging.md), [Model Management](../models/model-management.md) |
