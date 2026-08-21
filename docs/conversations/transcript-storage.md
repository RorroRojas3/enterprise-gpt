# Transcript Storage and Tokenization

End-to-end reference for how a conversation's messages are stored, read, counted, budgeted, rendered and exported. Covers the two Cosmos DB document shapes and the synthetic partition key they share, the transactional write path, the paged read path, the per-message token estimator and its calibration, the context-window budget that decides what is replayed to the model, the accuracy telemetry that measures the estimate, and the HTML rendering that lets three of the five export formats read rather than re-render (§12.1). Audience: engineers maintaining conversations, and anyone deciding what a "token" means in a report.

Implements [PRD: Conversation Storage and Tokenization](../prd/conversation/conversation-storage-and-tokenization.md). Companions: [Transcript Cutover](transcript-cutover.md) for deploying it, [Conversation Usage and Favourites](usage-and-favorites.md) for the SQL audit trail beside it, [Conversation Streaming Contract](streaming-contract.md) for the events the same turn writes while it runs, and [Conversation Export](conversation-export.md) for the Word/PDF renderer stack §12 only summarizes.

## 1. Overview

A transcript used to be **one Cosmos document per conversation** with every message in a `messages[]` array. Each turn appended two array entries and point-read the whole document to replay history. Cosmos caps an item — and a request — at 2 MB, so a conversation that was used long enough became unwritable; the default indexing policy indexed every element of the growing array on the way there; and three things the array shape could not carry were simply absent: a per-message token cost (the field existed and was hardcoded to `0`), a rendered form for export, and any notion of price.

The transcript is now **one document per message**, in a new container, under a synthetic partition key holding `{userId}:{conversationId}`. The largest document a conversation produces is bounded by its largest single message rather than by its message count. On that clean slate the release adds four things that the old shape had no room for:

1. **A context cost on every message**, estimated locally with a provider-resolved tokenizer and rolled up into both the header document and the SQL audit trail (§2, §8).
2. **A context-window budget**, so a turn's replayed history is trimmed to fit the model that will answer it and an impossible prompt fails with a 400 instead of being sent (§9).
3. **Server-rendered HTML per message**, produced once at persist time by Markdig with raw HTML disabled (§11).
4. **Export** — `GET api/conversations/{id}/export?format=html|json|md|docx|pdf` (§12), the last three added by US-1501.

**The cutover destroys every existing transcript.** That is not a side effect of the deployment; it is the deployment. Read [Transcript Cutover](transcript-cutover.md) before shipping this.

## 2. The two quantities the word "tokens" means

This is the concept everything below rests on, and getting it wrong is how a report ends up adding two incomparable numbers together. One rule governs every token field in the project:

> **`Context*` means estimated, recomputable and replayed. Every other token field is provider-billed.**

|  | Context cost | Billed cost |
| --- | --- | --- |
| What it measures | What a message contributes to the prompt on every *future* turn | What the provider actually charged for a turn |
| Grain | Per message, rolled up per turn and per conversation | Per turn only — no provider reports per message |
| Who computes it | This application, from the message's own text | The provider, reported through the tool-tracking middleware |
| Covers tool calls, tool results, reasoning | **No** — none of it is transcribed, so none of it is replayed | **Yes** |
| Where it lives | `tokens` on a message document, `contextTokens` on the header, `ContextTokens` on `Core.Conversation` and `Core.ConversationUsage` | `InputTokens`/`OutputTokens` and `ToolInputTokens`/`ToolOutputTokens` on `Core.ConversationUsage`, and the conversation's counters |

**The two are supposed to disagree, and the gap widens the more tools a turn runs.** Every tool call re-sends the conversation plus the tool's result to the model, and none of that is written to the transcript, so the billed figure climbs above the context figure with each round trip. A divergence is the model working as designed; a report that treats it as a defect is reading the wrong invariant. The single condition under which the two measure the same thing is a turn with **zero** tool calls — which is exactly the condition the accuracy telemetry isolates (§10).

Two consequences worth stating because both look like bugs:

- An assistant message's `tokens` will **not** equal its `usage.outputTokens`. `outputTokens` includes tool-call arguments the model generated and nothing transcribed. A unit test asserts that inequality rather than treating it as a fault.
- `ConversationUsage.ContextTokens` is **nullable** and `Conversation.ContextTokens` is not. A cancelled or failed turn is billed in SQL and deliberately never transcribed, so null says "not transcribed" where `0` would say "transcribed nothing". A conversation that has never completed a turn genuinely weighs nothing, so `0` is honest there.

## 3. Quick start

Read a conversation's transcript. Paging is **opt-in**: with no parameters the whole transcript comes back, exactly as it always did.

```http
GET /api/conversations/9c1f1c8e-6a1e-4c1a-9f7a-2b3c4d5e6f70/messages
Authorization: Bearer <token>
```

```jsonc
{
  "id": "9c1f1c8e-…",
  "name": "Quarterly report wording",
  "totalCount": 27,          // from the header's counter — includes the system message
  "hasMore": false,          // always false on an unpaged read
  "messages": [
    {
      "id": "5b7e…",
      "dateCreated": "2026-08-15T20:27:57.0000000+00:00",
      "text": "Rewrite this paragraph…",
      "role": 3,                       // integer on the wire (§13)
      "htmlContent": "<p>Rewrite this paragraph…</p>",
      "tokens": 412,
      "tokenAccuracy": "Estimated",    // string on the wire
      "usage": null                    // the turn's billed total; null on a user message (§13)
    }
  ]
}
```

Walk a long transcript backwards, newest page first:

```http
GET /api/conversations/{id}/messages?take=50
GET /api/conversations/{id}/messages?take=50&before=2026-08-15T20:27:57.0000000%2B00:00
```

Take a conversation out of the platform:

```http
GET /api/conversations/{id}/export?format=html   → 200 text/html                     (attachment)
GET /api/conversations/{id}/export?format=json   → 200 application/json
GET /api/conversations/{id}/export?format=md     → 200 text/markdown
GET /api/conversations/{id}/export?format=docx   → 200 application/vnd.openxmlformats-officedocument.wordprocessingml.document
GET /api/conversations/{id}/export?format=pdf    → 200 application/pdf, or 503 export-renderer-not-configured if this deployment has no usable font
GET /api/conversations/{id}/export?format=rtf    → 400 validation-error naming the supported formats
```

## 4. Document shapes

Two `type`-discriminated document kinds share one container, both partitioned on `/pk`.

```jsonc
// type: "conversation" — one per conversation, id = conversationId
{
  "id": "9c1f1c8e-…", "pk": "{userId}:{conversationId}",
  "userId": "…", "conversationId": "…", "type": "conversation",
  "name": "Quarterly report wording",
  "contextTokens": 8412,      // sum of the messages' own tokens
  "messageCount": 27,         // exact, not advisory — see §6
  "schemaVersion": 1,
  "dateCreated": "…", "dateModified": "…", "dateDeactivated": null
}

// type: "message" — one per message, id = messageId
{
  "id": "5b7e…", "pk": "{userId}:{conversationId}",
  "userId": "…", "conversationId": "…", "type": "message",
  "role": "assistant",        // string, not the ChatRoles integer
  "content": "…final answer…",
  "htmlContent": "<p>…</p>",
  "tokens": 412, "tokenAccuracy": "Estimated",
  "model": "gpt-4o-2026-05", "usage": { "inputTokens": 18240, "outputTokens": 1109 },
  "dateCreated": "2026-08-15T20:27:57.0000000Z"
}
```

Notes that are not obvious from the shape:

- **`userId` and `conversationId` stay on the document** even though the partition key concatenates them. A document readable only by parsing its partition key is a document no query and no export can project cleanly.
- **`model` is the deployment name, not the catalog display label.** The transcript is append-only, so the recorded value has to keep its meaning after a model is renamed, and it is the deployment that identifies what actually served and billed the turn.
- **`usage` on a message is the *turn's* total, tools included** — deliberately not the assistant-only/tool split the identically named SQL columns hold. What belongs beside a message in a transcript is what the turn cost; the breakdown belongs in the relational trail.
- **There is no sequence field of any kind.** Ordering is `dateCreated` alone (§6.2).
- **`schemaVersion`** is stamped on every header so a future shape change has a discriminator to read, rather than inferring a document's vintage from which properties happen to be absent.
- **The `documents[]` array is gone.** Nothing ever added to it; conversation documents live in SQL.

### 4.1 The synthetic partition key, and why it is not hierarchical

[`PartitionKeys.For(userId, conversationId)`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/Transcripts/PartitionKeys.cs) is the **only** place in the solution that composes the value `"{userId}:{conversationId}"`, and the two document types build their own keys through it in their `Create` factories. A format assembled at three call sites is a format that drifts at one of them, which would strand a conversation's documents in a partition nothing queries — silently, with no error.

Composing both identifiers into the key is what keeps the **tenancy guarantee in storage**. The read path always builds the key from the *caller's* user id, so another user's conversation id addresses a partition holding nothing: a foreign transcript is unreadable even if a code-level ownership check is ever missed, and the endpoint answers 404 rather than 403 without needing a filter.

A hierarchical key on `/userId` then `/conversationId` is the obvious alternative and was deliberately rejected ([PRD §5](../prd/conversation/conversation-storage-and-tokenization.md#5-technical-considerations)):

- The three benefits a hierarchical key normally earns are measured against a `/userId`-only container, not against a key that already contains the conversation id. Against the synthetic key each is a wash. Prefix routing is a wash, because one key value is already one logical partition and one target. The 20 GB logical-partition ceiling is a wash, because with a hierarchical key the logical partition *is* the full key path — 20 GB per `(userId, conversationId)` either way. Delete-by-partition-key is a wash and simpler, because a single key value is trivially a complete key with no partial-key case to guard.
- The hierarchical key is **not neutral**: it co-locates every document sharing its first level on one physical partition. That is the trade it makes to buy cheap `WHERE userId = @x` prefix queries — and this application never issues one. Every by-user question is answered from `Core.Conversation`; Cosmos is only ever asked about one conversation at a time. A heavy user's entire history would land on a single physical partition capped at 10,000 RU/s, in exchange for a query nobody writes.
- Microsoft's own guidance points the same way: when the first level has low cardinality and does not appear in queries, prefer a synthetic key.

### 4.2 A heterogeneous container, and its indexing policy

`type` is stamped on both kinds and **stays in every read predicate**. The container is heterogeneous by design, and the predicate is what stops a third document kind, added later, from silently entering queries written before it existed.

The container is provisioned at startup by [`CosmosBootstrapper`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Startup/CosmosBootstrapper.cs) with an explicit indexing policy: everything indexed, minus `/content/*`, `/htmlContent/*`, `/usage/*` and `_etag`. Those first two are the bulk of a message document's bytes and are never filtered, ordered or aggregated on, so indexing them is pure write cost. The `_etag` exclusion restores behaviour the default policy has and a custom policy otherwise drops.

The bootstrap also reads the container's partition key path back and **fails startup** when it is not `/pk`, naming both paths. A key path is fixed at creation, so continuing would write documents no read path can address — which surfaces as an empty transcript rather than an error, the one failure mode this must not have. The policy itself is asserted by a unit test over `CreateTranscriptContainerProperties` rather than against the emulator, because the Linux emulator accepts a custom indexing policy and then ignores it.

## 5. Serialization, and why the date format is load-bearing

The `CosmosClient` is now built with `UseSystemTextJsonSerializerWithOptions` from [`CosmosSerialization.CreateOptions()`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Serialization/CosmosSerialization.cs): camel-case property naming, `JsonStringEnumConverter` with camel-case values, and a `DateTimeOffset` converter. Previously the SDK's Newtonsoft default was active while the document types carried `[JsonPropertyName]` attributes it never read — they agreed only by coincidence, and this repository already carries a bug from that mismatch, where a cross-partition query's camelCase predicates matched nothing.

Two knock-on effects:

- **`role` serializes as `"assistant"`, not `2`.** The converter is scoped to the Cosmos client only. The HTTP API registers no string-enum converter, so its DTOs still serialize `role` as an integer and clients already depend on that (§13).
- **Every stored `DateTimeOffset` is written UTC and fixed-width**, `yyyy-MM-ddTHH:mm:ss.fffffffZ`, by [`CosmosUtcDateTimeOffsetConverter`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Serialization/CosmosUtcDateTimeOffsetConverter.cs).

That second one is a **correctness requirement of the transcript, not a formatting preference**. Cosmos has no date type: `ORDER BY c.dateCreated` is a lexicographic comparison of whatever strings were stored, and lexicographic order equals chronological order only while every value shares one offset and one width. The default round-trip format satisfies neither — it preserves the original offset (`+02:00` sorts after `Z` for an earlier instant) and trims trailing zeros from the fractional part (`…:00.5Z` sorts after `…:00.4999999Z`, but also after `…:00.45Z`, which is earlier). Normalizing to UTC and padding to seven fractional digits removes both, and seven digits is `DateTime`'s full tick resolution, so two instants one tick apart stay distinguishable — which the write path's monotonic clamp (§6.2) depends on. Reading is deliberately permissive: any offset and any precision parses. Only the write side is pinned.

## 6. The access layer

[`ITranscriptStore`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Transcripts/ITranscriptStore.cs) replaces the deleted `IAzureCosmosService`. It is a separate type rather than an extension of it because that type bound one container in its constructor, and the transcript container is a different container with a different key path.

| Member | Does | Notes |
| --- | --- | --- |
| `ReadItemAsync<T>` | Point-reads one document | Returns `null` on 404 rather than throwing |
| `QueryAsync<T>` | Runs **one page** of a query against one partition | Takes a page size and a continuation token; returns `TranscriptPage<T>` |
| `PatchItemAsync` | Partial update | Capped at `MaxPatchOperations = 10`, which Cosmos enforces anyway; returns `false` when the document is absent |
| `ExecuteBatchAsync` | Runs operations as one transaction on one partition | Capped at `TranscriptBatch.MaxOperations = 100`; returns which operation failed |
| `DeletePartitionAsync` | Empties a partition | Server-side delete or batched fallback (§6.4) |

**Every member takes a partition key, and none accepts a cross-partition query.** A transcript read that fanned out would be both a cost surprise and a tenancy hole, so it is unreachable through this surface rather than merely discouraged: an unset key throws before the request leaves the process.

Two details a caller will meet:

- `QueryAsync` drops the continuation token when `HasMoreResults` says the page was the last. The SDK reports a token on the final page too when the backend cannot yet prove it was final, and returning it costs every caller one empty round trip.
- `PatchItemAsync` sets `EnableContentResponseOnWrite = false`. The patched document is a header carrying counters and a name; nothing reads it back.

[`TranscriptBatch`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Transcripts/TranscriptBatch.cs) records operations rather than exposing the Cosmos `TransactionalBatch`, which is abstract and obtainable only from a live `Container` — exposing it would make the interface impossible to substitute in tests. Each recorded operation carries both a description and the delegate that applies it; the delegate is what preserves the document's *static* type through `CreateItem<T>`, since System.Text.Json writes the declared type's properties and boxing an item to `object` would serialize an empty document.

`TranscriptBatchResult.Describe()` names the failing operation index. Cosmos marks every operation that did not itself fail as `424 Failed Dependency`, so the one entry that is neither successful nor 424 identifies the cause.

### 6.1 Writing a turn

A completed turn is **one transactional batch** on the conversation's partition:

```csharp
await store.ExecuteBatchAsync(partition, batch => batch
    .Create(userMessage.Id, userMessage)
    .Create(assistantMessage.Id, assistantMessage)
    .Patch(conversationId.ToString(),
    [
        PatchOperation.Increment("/messageCount", 2),
        PatchOperation.Increment("/contextTokens", contextDelta),
        PatchOperation.Set("/dateModified", answeredAt),
    ]), cancellationToken);
```

Because the batch is atomic within the partition, **`messageCount` cannot diverge from the documents backing it** — either all three operations land or none do. That is what makes the header's count exact rather than advisory, and it is why the paged read can take `totalCount` from the header instead of issuing a count query.

**The caller learns the assistant message's id from this write, and nothing upstream of it can know it sooner.** `ConversationService.PersistTurnAsync` returns it — `Guid?`, `null` on every path that appends nothing (an abandoned turn, or a batch that failed after the SQL usage row already committed). Since US-1101 that id is also what the stream's closing `Finished` frame carries, under `metadata.assistantMessageId`: the service holds that frame back until this call returns, and stamps it only when the return value is non-null. See [Streaming Contract §4.1.1](streaming-contract.md#411-finished-carries-the-persisted-messages-identity-us-1101) for the framing this ordering exists to support — this section is the write it is ordered against, not a restatement of it.

Three rules the write path preserves or adds:

- **Cancelled and failed turns are billed in SQL and never transcribed.** Appending a truncated answer would replay it to the model forever as something the assistant said. Such a turn still moves the conversation's billed counters and writes its usage row, with `ContextTokens` null.
- **A batch failure is logged, not swallowed silently.** The SQL side has already committed by then and the answer has already been streamed, so nothing can be returned to the caller. The log names both the usage row id and the assistant message id, because the usage row now asserts an `AssistantMessageId` that no transcript contains and reconciling needs the pair.
- **A conversation with no header creates one.** Conversations that predate the cutover have a SQL row and no transcript, so the first turn taken in one creates the header *and* re-seeds the system message, stamped one tick before the prompt. A conversation that silently ran without its standing instructions would be a far worse outcome than an empty history, and nothing reports a missing system prompt.

Conversation creation uses the same mechanism: the header and the seeded system message are created in one batch, inside the SQL transaction that creates the relational row, so a Cosmos failure rolls the row back rather than leaving a conversation the user can open but never stream. The one gap it cannot close is a *commit* failure after the documents were written — Cosmos is another store and cannot be rolled back — so that path logs the orphaned partition key, because no query reaches a conversation SQL has no record of.

### 6.2 Ordering, without a sequence field

Ordering is `dateCreated` alone, which is legitimate only because the two timestamps of a turn are genuinely different moments: the user message is stamped **when the prompt was received**, the assistant message **when the answer completed**. Previously both were stamped with one `DateTimeOffset.UtcNow` and array position was the only thing hiding the tie.

A strict-monotonic clamp makes distinctness an invariant of the write rather than an assumption about how long a turn takes: the assistant timestamp is clamped to at least the user timestamp plus one tick. Ordering is by `dateCreated` only, not by a `(dateCreated, id)` pair — a multi-property `ORDER BY` needs a composite index in Cosmos, and the tie it would break cannot occur, since a turn's own pair is a tick apart and `IConversationLockService` serializes a conversation's turns. The materialized page is still sorted by id within a timestamp, so the result is total whatever the store returns.

This rests on the deployment being **single-replica**: one process, one clock, and a process-local conversation lock. A scale-out reopens message ordering as a design question — the answers available then are a Cosmos-side monotonic value, a distributed lock, or a tiebreak column, none of which this design forecloses, since a tiebreak is an added indexed property rather than a re-partition.

### 6.3 Rename, deactivate

Rename and soft delete patch the **header only** and touch no message document: `/name` and `/dateModified` for a rename, `/dateDeactivated` for a delete. Patching rather than read-modify-replacing matters more than it looks — a full-document replace here once overwrote the transcript with the relational shape, from inside a live stream, on a conversation's first turn. Bulk deactivation issues one header patch per id and reads no message documents.

### 6.4 Purge

`DeletePartitionAsync` empties a conversation's partition. Which mechanism it uses is a configuration choice, and the default is the conservative one:

- `CosmosDb:UsePartitionKeyDelete = false` (default) — page the ids and delete them in batches of ten. The loop re-reads from the start on each pass rather than following a continuation token, because the documents the token described no longer exist.
- `true` — one `DeleteAllItemsByPartitionKeyStreamAsync`. A refused call logs a warning and **falls through** to the batched path rather than throwing, so a deployment that turned the setting on optimistically still purges.

The capability is Azure public preview, must be enabled per account with the Azure CLI (it cannot be set through ARM or Bicep), and is absent from the Linux emulator. See [cutover §6](transcript-cutover.md#6-enabling-delete-by-partition-key) before turning it on. No HTTP route reaches this member today — deleting a conversation is a soft delete — so it exists for the operator-side purge and is proved by the emulator tests.

## 7. The read path

Every read is a single-partition query with the `type` predicate in place:

```sql
SELECT * FROM c WHERE c.type = @type AND c.conversationId = @conversationId ORDER BY c.dateCreated
```

The partition key is built from the **caller's** user id, so a foreign conversation id addresses a partition holding nothing and the service throws `NotFoundException` — a 404, never a 403, matching every other conversation route.

`GET api/conversations/{id}/messages` takes two optional parameters:

| Parameter | Behaviour |
| --- | --- |
| *(none)* | **Returns every message**, ascending. Unchanged from before this release |
| `?take=` | Clamped server-side to 1–100, matching every other paginated route here. Returns the **newest** `take` messages, still in ascending order |
| `?before=` | A `DateTimeOffset` cursor; returns the page immediately preceding it. An unparseable value is a 400 |

The response carries `totalCount` (the header's `messageCount`, which includes the system message the payload excludes) and `hasMore`. `hasMore` is computed by asking the store for one message more than requested rather than by comparing against `totalCount` — walking backwards, every page would otherwise be compared against the whole transcript and the last page would always claim another follows.

Two deliberate deviations from the PRD, both recorded here because they are the kind of thing a future reader will otherwise assume is a bug:

- **The no-parameter default still returns every message.** US-205 specified "the newest 100 by default". Changing the default would break the Angular client's transcript store, which reads this route and expects the whole history, so paging is opt-in and the breaking change is deferred to the client story that can absorb it.
- **A conversation with no header falls back to SQL** and answers with an empty message list rather than a 404. That is the pre-cutover case: the row survived, the transcript did not, and a row the user can see in their sidebar has to open. It is the only path in this service that reads conversation metadata from SQL, and it logs one informational line each time.

History replay for a turn uses the same read, unpaged, and then applies the context budget to decide what is actually sent (§9).

Drain semantics are worth knowing if you touch `ReadMessagesAsync`: a page size is a **ceiling** Cosmos may return fewer than, and a page can come back empty with more results behind it, so the loop keeps draining until the requested count is in hand. Stopping at the first page would silently truncate a transcript — and truncating the history a model replays is not a visible failure.

## 8. Token estimation

Every persisted message carries `tokens` and `tokenAccuracy`. The estimate is deliberately local and cheap rather than a provider round trip: counting tokens over the network on the write path would add a call per message to every turn.

**Resolution mirrors chat clients.** [`TokenEstimatorResolver`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/TokenEstimatorResolver.cs) reads `Providers.ServiceKeys` and resolves a keyed `ITokenEstimator`, exactly as `ChatClientResolver` resolves a keyed `IChatClient` — with one deliberate difference: it **never throws**. A missing chat client means the turn cannot run; a missing estimator only means the count is uncalibrated, and failing a turn that could otherwise be answered would trade a real capability for an approximation. An unregistered provider falls back to the uncalibrated default and is logged once per provider.

**Vocabularies are shared.** [`TokenizerProvider`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/TokenizerProvider.cs) is one singleton behind all four estimator registrations, so four registrations do not mean four loaded vocabularies. Building a tokenizer loads and indexes a large vocabulary and must not happen per request. Selection mirrors the document chunker: ask the library for the deployment name's own encoding first, and fall back to `Tokenization:FallbackEncoding` when it does not recognize the name — which is usual, because Azure OpenAI addresses models by deployment name and a deployment name is chosen by whoever created it. `Microsoft.ML.Tokenizers.Data.O200kBase` 2.0.0 is now referenced beside `Cl100kBase`, so the modern OpenAI families can be counted at all.

**Calibration is configuration, not code.** tiktoken is OpenAI's tokenizer and undercounts other vendors — Claude by roughly 15–20% on prose and more on code and non-English text. `Tokenization:ProviderCalibration` maps a provider id to a multiplier applied to the raw count, defaulting to `1.0`. The calibrated result is rounded **up**, because the budget it feeds is a ceiling and rounding down is the direction that overflows a context window. A multiplier at or below zero, or above `MaxCalibrationMultiplier`, fails startup naming the setting.

**Tokens nobody owns are counted too.** [`PromptOverheadCalculator`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/PromptOverhead.cs) adds a per-message framing constant, a flat cost per attached tool, and the estimate of the project instructions that ride `ChatOptions.Instructions` rather than the message list. Without those terms the sum of a conversation's messages would always under-report the prompt, and a budget built on that sum would authorise a turn the provider then rejects. Per-tool schema cost is modelled as a flat figure rather than by serializing each schema, which is more work than the accuracy is worth until the reconciliation metric says otherwise.

**Estimation never loses a turn.** `EstimateTokens` catches everything the tokenizer can throw and records `0`, logging the failure. Zero is honest — it is what the recorded cost is — and it is recoverable, because the count is a pure function of the stored text and can be recomputed later. A tokenizer fault must not lose a turn whose answer has already been streamed to the user.

`tokenAccuracy` is `Estimated` on everything this application writes today. `Exact` exists so the field can distinguish the two the day a provider token-count endpoint is used, without a schema change or a re-reading of every row already written.

### 8.1 Configuration

Bound from `Tokenization`, validated with `ValidateOnStart()`. No section is present in `appsettings.json`, so every value below is a default until a deployment sets one.

| Setting | Default | Purpose |
| --- | --- | --- |
| `FallbackEncoding` | `cl100k_base` | Used when the deployment name is not a model the library recognizes. A deployment that knows its models are modern should set `o200k_base` and get a closer estimate |
| `PerMessageFramingTokens` | `4` | Role markers and delimiters the wire format charges to the prompt and no message owns |
| `PerToolSchemaTokens` | `120` | Flat cost per attached tool's JSON schema |
| `MaxCalibrationMultiplier` | `3` | Ceiling on a calibration value — a multiplier far above one is a mistake that would silently shrink every conversation's replayed history |
| `ProviderCalibration` | empty | `{ "<provider-guid>": 1.18 }`. Keys must parse as GUIDs; values must be > 0 and ≤ the ceiling |

## 9. The context budget

[`ContextBudgetCalculator`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/ContextBudgetCalculator.cs) decides which messages fit the model that is about to answer. It is deliberately pure — no database, no chat client, no Cosmos — because trimming is the decision most likely to be wrong in a way nobody notices, and therefore the one that most needs to be testable without a running turn.

```text
budget = truncate(ContextWindowSize - MaxOutputTokens) - turnOverhead
```

- **Oldest first.** What is lost is always the least recent context. A dropped user message takes the assistant message that answered it, so the model is never shown a reply to a question it can no longer see.
- **The system message and the current prompt are never dropped.** The first carries the assistant's standing instructions; the second is the question the turn exists to answer.
- **An impossible prompt fails with a 400 rather than being sent.** When what cannot be dropped exceeds the budget on its own, the turn throws a `ValidationException` before contacting the provider, keyed on `Prompt` and naming the model, its window and the reserve. The alternative is making the user wait for a rejection.
- **A `ContextWindowSize` of `0` means unbounded**, not a budget of zero. Zero is the seeded default for a catalog row nobody has filled in, and trimming every conversation to nothing over an unset field would be a far worse failure than sending a prompt that might be rejected. It is logged once per model.
- **A message stored with `tokens = 0` is re-estimated on the fly** rather than counted as free, so a pre-release message cannot make the budget under-measure the prompt.
- **Trimming decides what is *sent*, never what is *stored*.** The transcript keeps every message either way.

Switching models mid-conversation recomputes the budget against the newly selected model, because it is computed per turn from the model that will answer.

## 10. Estimator accuracy telemetry

[`ChatMetrics`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Observability/ChatMetrics.cs) is the solution's first `Meter`. It is named with `TelemetryNames.ChatSource` (`Enterprise.Gpt.Chat`), the name `TelemetryRegistration` already registers as both an `ActivitySource` and a `Meter`, so the instruments export with no wiring change. With no Application Insights connection string configured the distro is skipped and nothing is exported; the instruments are still recorded to, which costs nothing and keeps the code path identical between environments.

| Instrument | Kind | Recorded when |
| --- | --- | --- |
| `enterprise_gpt.chat.estimator.relative_error` | histogram (`1`) | A completed turn reported usage and invoked **zero tools** |
| `enterprise_gpt.chat.budget.dropped_messages` | counter (`{message}`) | A turn's history was trimmed |
| `enterprise_gpt.chat.budget.dropped_tokens` | counter (`{token}`) | As above |

The error is signed and relative: `(estimated − reported) / reported`, so `0.05` is a five percent overestimate. **Zero tool calls is the gate, not zero tool tokens** — an MCP server can run and report no usage at all, and a turn that ran tools was billed for prompts the estimator never saw, so comparing them would record an error that is not one.

Dimensions are the provider id and the deployment name. **No conversation id, user id or message content appears on any dimension**: metric dimensions are high-cardinality storage retained and queried far more widely than logs, and neither identifier answers a question the deployment name does not.

The metric covers a rolling window. The durable counterpart is the `ContextTokens`/`InputTokens` pair now sitting on the same `Core.ConversationUsage` row, which answers the same question over any window still in the table — see [usage §7](usage-and-favorites.md#7-reporting).

## 11. Server-rendered HTML

Every persisted message carries `htmlContent`, rendered once at persist time by [`MarkdownRenderer`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Rendering/MarkdownRenderer.cs) — Markdig's first use in this solution. User prompts go through the same pipeline as assistant text, so an export needs one rendering path rather than two.

**The input is attacker-influenceable.** Model output is shaped by uploaded documents and MCP tool results, and a user prompt is rendered through the same path, so this is a trust boundary rather than a formatting convenience. Two layers stand behind it:

1. **`DisableHtml()`** — raw HTML in the source is emitted as escaped text rather than markup, so `<script>alert(1)</script>` never becomes an element and an `<img onerror=…>` never becomes an attribute.
2. **A link-scheme allowlist** — `DisableHtml` does not touch link targets, so a `javascript:` href written as ordinary markdown survives it. A `DocumentProcessed` pass blanks the URL of any link or autolink whose scheme is not `http`, `https` or `mailto`. It is an allowlist rather than a blocklist because `javascript:` is only the best-known of the dangerous schemes, and a blocklist is wrong the moment a browser accepts something not on it. Control characters are stripped before the scheme is read — browsers ignore them inside a scheme, so `java\0script:` reaches the same handler as the plain form while a naive comparison sees a scheme on no list — and a colon after a path separator is treated as part of a relative path, not as a scheme.

Rendering **never throws**: a failure degrades to the HTML-escaped plain text of the input and is logged by length only, because the content is a user prompt or a model answer and neither belongs in a log. A rendering fault must not lose a turn whose answer has already been streamed.

`htmlContent` is an **export-fidelity artefact, not a mirror of what the UI shows.** The client renders through `ngx-markdown` over marked and Prism; the two differ in whitespace, syntax-highlighting markup and extension support. A client reading `htmlContent` over HTTP should treat `null` as "render `text` yourself".

## 12. Export

`GET api/conversations/{id}/export?format=html|json|md|docx|pdf`, served by [`ConversationExportService`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ConversationExportService.cs). US-1501 grew the route from two formats to five by turning a format into a **registered renderer** — one `IConversationExportRenderer` per `ConversationExportFormats` member — rather than a branch inside the service. The service itself now owns exactly three things: who may export, what an export may contain, and which renderer answers; everything specific to a format lives in that format's renderer. See [Conversation Export](conversation-export.md) for the renderer registry, the block model the binary formats share, and the font dependency that makes PDF the one format a deployment can genuinely not have — this section covers only what carried over and what changed about the route itself.

| Aspect | Behaviour |
| --- | --- |
| Default format | `html` when `?format=` is absent or empty |
| Unsupported format | 400 `validation-error`, keyed `format`, naming the supported five |
| Supported format, unavailable here | 503 `export-renderer-not-configured`, carrying a `format` extension — a deployment withdrew the format (`Export:DisabledFormats`) or PDF found no usable font. See [Conversation Export §3](conversation-export.md#3-the-renderer-registry) |
| Ownership | Checked twice: the partition key answers for the caller's own conversations, and a SQL `AnyAsync` additionally rejects a **deactivated** conversation, which still has a transcript. Another user's conversation is a 404 |
| Content | Non-system messages in `dateCreated` order. Activity cards, reasoning text and an unsaved partial answer are never included — that is the whole of what the transcript persists, so it is the whole of what an export can honestly contain |
| File name | Derived from the conversation name, non-alphanumerics replaced, clipped to 60 characters, falling back to `conversation` |
| Response | `Cache-Control: no-store` on every format — a transcript is the most sensitive thing this API serves, and the response is a file a shared browser would otherwise keep on disk after the reader signs out |

### 12.1 Three formats read, two re-render

Whether a format **reads** what persist time already computed or **re-renders** the message text now depends on which one it is, and it is worth being precise about which because the distinction used to be simple — every export read `htmlContent` — and is not anymore:

- **`html`, `json` and `md` all read.** `html` assembles the `Files/conversation-history.html` template from each message's stored `htmlContent`, exactly as before (§11) — a message with none is HTML-escaped into the output rather than omitted. `json` serializes the header fields and the message documents with the **Cosmos** serializer options, so the exported property names are the stored ones and the export is the storage shape rather than a third representation. `md` emits each message's own markdown text unmodified: a round trip through Markdig would renumber lists, rewrite fences and normalize whitespace, which is a worse export of what the model actually wrote than the text itself.
- **`docx` and `pdf` re-render.** Neither format has anywhere to put HTML markup — a word processor and a PDF layout engine both need *structure*, not markup — so both walk each message's markdown once, through the same hardened pipeline that produces `htmlContent` (§11's `DisableHtml` and link-scheme allowlist apply to an export exactly as they apply to the transcript), into a renderer-neutral block model that the Word and PDF renderers each turn into their own document. This is why a `javascript:` link written in a prompt is exactly as dead in an exported Word document as it is in the rendered transcript, and why the two binary formats cannot disagree with each other about what a message's markdown meant.

## 13. The HTTP surface, and the two role contracts

`ConversationMessageDto` gained five fields at this release — `id`, `dateCreated`, `htmlContent`, `tokens`, `tokenAccuracy` — beside the existing `text` and `role`, which are unchanged in name, type and meaning. `ChatConversationDto` gained `totalCount` and `hasMore`. US-1101 added a sixth, nullable `usage` (`ConversationMessageUsageDto`: `inputTokens`, `outputTokens`), projected straight off the message document's own `usage` — the shape §4 already showed being written, now also read back over HTTP. It is the **turn's** total, tools included, which is why it deliberately does not equal the sibling `tokens` field (§2); it is `null` on every user and system message and on an assistant message written before the field existed, because there is nothing to project from a document that never carried it.

**`role` is an integer on the wire and a string in storage, and that is deliberate.** The API registers no global `JsonStringEnumConverter`, so `ChatRoles` serializes as `1` System, `2` Assistant, `3` User, `4` Tool — which the Angular client's `CHAT_ROLE` mirror depends on. The Cosmos documents serialize `role` as a camel-cased **string**, because that keeps an exported document readable and matches the SSE contract. Two contracts, two shapes, one enum. `tokenAccuracy` is the exception on the wire: it carries a **property-level** `[JsonConverter(typeof(JsonStringEnumConverter<TokenAccuracies>))]` so it serializes as `"Estimated"`, applied per property precisely because a global converter would change `role` and break every client reading it.

## 14. Testing

Unit tests (`dotnet test --filter "Category!=Integration"`, no Docker):

| Area | Where |
| --- | --- |
| Fixed-width UTC dates, and that string sorting equals chronological sorting | [`Serialization/CosmosUtcDateTimeOffsetConverterTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Serialization/CosmosUtcDateTimeOffsetConverterTests.cs) |
| Document shapes, string roles, the single partition-key construction site | [`Transcripts/TranscriptDocumentTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Transcripts/TranscriptDocumentTests.cs), [`TranscriptBatchTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Transcripts/TranscriptBatchTests.cs) |
| The indexing policy and `/pk` path | [`Startup/CosmosBootstrapperTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Startup/CosmosBootstrapperTests.cs) |
| Estimator resolution, fallback encoding, calibration, overhead | [`Tokenization/`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Tokenization/) |
| Budget trimming, protections, unbounded models | [`Tokenization/ContextBudgetCalculatorTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Tokenization/ContextBudgetCalculatorTests.cs) |
| Raw HTML, `javascript:` links, code fences, the escaping fallback | [`Rendering/MarkdownRendererTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Rendering/MarkdownRendererTests.cs) |
| `EstimatedCost`, including the null-when-unpriced rule | [`Entities/ConversationUsageCostTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Entities/ConversationUsageCostTests.cs) |
| Turn persistence, transcript reads, export | [`Services/ConversationServiceTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Services/ConversationServiceTests.cs), [`Services/ConversationExportServiceTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Services/ConversationExportServiceTests.cs) |

Integration tests need Docker. [`CosmosEmulatorFixture`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/TestInfrastructure/CosmosEmulatorFixture.cs) runs the **vNext Linux emulator** in Testcontainers — it starts in seconds rather than minutes and runs on any processor architecture — in its own collection, so it starts only when [`Persistence/TranscriptStoreIntegrationTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/Persistence/TranscriptStoreIntegrationTests.cs) actually runs. Transactional batches and ordered cross-document queries are precisely what a fake cannot prove: a fake that interpreted JSON-patch semantics would be asserting its own arrangement. The endpoint-level integration suite still substitutes [`FakeTranscriptStore`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/TestInfrastructure/FakeTranscriptStore.cs), because every conversation write would otherwise block for ~25 seconds waiting on an emulator address that is not there.

## 15. Known limits

- **The estimate is an estimate.** `ContextTokens` inherits every limitation of the tokenizer behind it, including the per-provider undercount the accuracy metric quantifies. It is comparable across conversations because it is computed the same way for all of them; it is not a figure to bill against, and `EstimatedCost` deliberately does not use it.
- **Accuracy thresholds exist for Azure OpenAI only.** 5% at p50 and 15% at p95 over a rolling 7-day window at 200+ samples. Amazon Bedrock and Anthropic record the same metric with no threshold until their multipliers are tuned from it.
- **`tokenAccuracy` is always `Estimated`.** Nothing writes `Exact` yet.
- **Tool calls, arguments, results and reasoning are never transcribed**, deliberately — persisting them would change what the provider receives on the next turn, which is a behaviour change rather than a storage change.
- **Ordering assumes one clock.** See §6.2.
- **Cosmos still authenticates with an account-key connection string**, the one place this API departs from its `DefaultAzureCredential` preference. `CosmosOptions` is shaped so an endpoint-plus-credential pair can be added beside it.
- **The default transcript read is still unpaged** (§7), so a very long conversation transfers in full until the client story that can absorb the change lands.
- **Nothing exposes any of this as a report.** Context tokens and cost are queryable in SQL only — see [usage §7](usage-and-favorites.md#7-reporting) and its "no reporting API" gap.

## 16. Key files

| Concern | File |
| --- | --- |
| Document types and the partition key | [`Entity/Transcripts/TranscriptDocuments.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/Transcripts/TranscriptDocuments.cs), [`Entity/Transcripts/PartitionKeys.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/Transcripts/PartitionKeys.cs) |
| Access layer | [`Service/Transcripts/ITranscriptStore.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Transcripts/ITranscriptStore.cs), [`TranscriptStore.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Transcripts/TranscriptStore.cs), [`TranscriptBatch.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Transcripts/TranscriptBatch.cs) |
| Serialization | [`Service/Serialization/CosmosSerialization.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Serialization/CosmosSerialization.cs), [`CosmosUtcDateTimeOffsetConverter.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Serialization/CosmosUtcDateTimeOffsetConverter.cs) |
| Provisioning and options | [`Api/Startup/CosmosBootstrapper.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Startup/CosmosBootstrapper.cs), [`Service/Settings/CosmosOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/CosmosOptions.cs) |
| Tokenization | [`Service/Tokenization/`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/), [`Service/Settings/TokenEstimationOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/TokenEstimationOptions.cs), [`Common/Enums/TokenAccuracies.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Common/Enums/TokenAccuracies.cs) |
| Rendering | [`Service/Rendering/MarkdownRenderer.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Rendering/MarkdownRenderer.cs) |
| Metrics | [`Service/Observability/ChatMetrics.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Observability/ChatMetrics.cs), [`Common/Observability/TelemetryNames.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Common/Observability/TelemetryNames.cs) |
| Export routing and ownership | [`Service/Export/ConversationExportService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Export/ConversationExportService.cs) — the renderers themselves, the block model and the font resolver are catalogued in [Conversation Export §11](conversation-export.md#11-key-files) |
| Write, read and budget wiring | [`Service/ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs) |
| HTTP surface | [`Api/Endpoints/ConversationEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs), [`Dto/ConversationMessageDto.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/ConversationMessageDto.cs), [`Dto/ConversationDto.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/ConversationDto.cs) |
| Related reference | [Transcript Cutover](transcript-cutover.md), [Usage and Favourites](usage-and-favorites.md), [Streaming Contract](streaming-contract.md), [Turn Lifecycle](turn-lifecycle.md), [Conversation Export](conversation-export.md) |
