# Transcripts and Tokenization

Where conversation messages live, how they are read back, and how the prompt for the next turn is
sized.

## The two quantities "tokens" can mean

One rule governs every token field in the project:

> **`Context*` means estimated, recomputable and replayed. Every other token field is
> provider-billed.**

| | Context cost | Billed cost |
| --- | --- | --- |
| Measures | What a message contributes to the prompt on every *future* turn | What the provider actually charged for a turn |
| Grain | Per message, rolled up per turn and per conversation | Per turn only |
| Computed by | This application, from the message's own text | The provider, via the tool-tracking middleware |
| Covers tool calls, results, reasoning | No — none of it is transcribed, so none is replayed | Yes |

**The two are supposed to disagree, and the gap widens the more tools a turn runs**, because every
tool call re-sends the conversation plus the result and none of that is transcribed. A divergence is
the design working; a report that treats it as a defect is reading the wrong invariant. They measure
the same thing only on a turn with zero tool calls — which is exactly what the accuracy telemetry
isolates.

Two consequences that look like bugs and are not:

- An assistant message's `tokens` will **not** equal its `usage.outputTokens`, which includes
  tool-call arguments the model generated and nothing transcribed. A test asserts that inequality.
- `ConversationUsage.ContextTokens` is nullable; `Conversation.ContextTokens` is not. A cancelled or
  failed turn is billed in SQL and never transcribed, so null says "not transcribed" where `0` would
  say "transcribed nothing".

## Storage

One document per message plus one `type`-discriminated header per conversation, under a synthetic
partition key `/pk` of `{userId}:{conversationId}`. Shapes are in
[../architecture/data-model.md](../architecture/data-model.md).

## The access layer

`ITranscriptStore` is the only way in.

| Member | Does | Notes |
| --- | --- | --- |
| `ReadItemAsync<T>` | Point-reads one document | Returns `null` on 404 rather than throwing |
| `QueryAsync<T>` | Runs **one page** against one partition | Takes page size and continuation token |
| `PatchItemAsync` | Partial update | Capped at 10 operations; returns `false` when absent |
| `ExecuteBatchAsync` | One transaction on one partition | Capped at 100 operations; reports which operation failed |
| `DeletePartitionAsync` | Empties a partition | Server-side delete, with a batched fallback |

**Every member takes a partition key and none accepts a cross-partition query.** A transcript read
that fanned out would be both a cost surprise and a tenancy hole, so it is unreachable through this
surface rather than merely discouraged — an unset key throws before the request leaves the process.

Two details a caller meets:

- `QueryAsync` drops the continuation token when the page was the last. The SDK reports a token on
  the final page too when the backend cannot yet prove it was final, and returning it costs every
  caller an empty round trip.
- `TranscriptBatch` records operations rather than exposing the Cosmos `TransactionalBatch`, which
  is abstract and obtainable only from a live `Container` — exposing it would make the interface
  impossible to substitute in tests. Each recorded operation carries the delegate that applies it,
  which is what preserves the document's *static* type through `CreateItem<T>`; boxing to `object`
  would serialize an empty document.

`TranscriptBatchResult.Describe()` names the failing operation index. Cosmos marks every operation
that did not itself fail as `424 Failed Dependency`, so the one entry that is neither successful nor
424 identifies the cause.

## Token estimation

Every persisted message carries `tokens` and `tokenAccuracy`. The estimate is local and cheap rather
than a provider round trip, because counting over the network on the write path would add a call per
message to every turn.

- **Resolution mirrors chat clients.** `TokenEstimatorResolver` resolves a keyed `ITokenEstimator`
  from `Providers.ServiceKeys` exactly as `ChatClientResolver` resolves a keyed `IChatClient`, with
  one difference: it **never throws**. A missing chat client means the turn cannot run; a missing
  estimator only means the count is uncalibrated.
- **Vocabularies are shared.** `TokenizerProvider` is one singleton behind all estimator
  registrations, because building a tokenizer loads and indexes a large vocabulary and must not
  happen per request. It asks the library for the deployment name's own encoding first and falls
  back to `Tokenization:FallbackEncoding` — which is the usual path, since Azure OpenAI addresses
  models by a deployment name chosen by whoever created it.
- **Calibration is configuration, not code.** tiktoken is OpenAI's tokenizer and undercounts other
  vendors. `Tokenization:ProviderCalibration` maps a provider id to a multiplier, defaulting to
  `1.0`. The result is rounded **up**, because the budget it feeds is a ceiling and rounding down is
  the direction that overflows a context window.
- **Tokens nobody owns are counted.** `PromptOverheadCalculator` adds a per-message framing
  constant, a flat cost per attached tool, and the estimate of project instructions that ride
  `ChatOptions.Instructions` rather than the message list. Without those terms the sum of a
  conversation's messages would always under-report the prompt.
- **Estimation never loses a turn.** `EstimateTokens` catches everything the tokenizer can throw and
  records `0`, which is honest and recoverable — the count is a pure function of the stored text.

`tokenAccuracy` is `Estimated` on everything written today. `Exact` exists so the field can
distinguish the two the day a provider token-count endpoint is used, with no schema change.

### Configuration — `Tokenization`

No section is present in `appsettings.json`, so each of these is a default until a deployment sets
one.

| Setting | Default | Purpose |
| --- | --- | --- |
| `FallbackEncoding` | `cl100k_base` | Used when the deployment name is not a model the library recognizes. A deployment on modern families should set `o200k_base`. |
| `PerMessageFramingTokens` | `4` | Role markers and delimiters no message owns |
| `PerToolSchemaTokens` | `120` | Flat cost per attached tool's JSON schema |
| `MaxCalibrationMultiplier` | `3` | Ceiling on a calibration value |
| `ProviderCalibration` | empty | `{ "<provider-guid>": 1.18 }`; values must be above 0 and at or below the ceiling |

## The context budget

`ContextBudgetCalculator` decides which messages fit the model about to answer. It is deliberately
pure — no database, no chat client, no Cosmos — because trimming is the decision most likely to be
wrong in a way nobody notices, and therefore the one that most needs to be testable without a
running turn.

```text
budget = truncate(ContextWindowSize - MaxOutputTokens) - turnOverhead
```

- **Oldest first.** A dropped user message takes the assistant message that answered it, so the
  model is never shown a reply to a question it can no longer see.
- **The system message and the current prompt are never dropped.**
- **An impossible prompt fails with a 400 rather than being sent.** When what cannot be dropped
  exceeds the budget alone, the turn throws `ValidationException` before contacting the provider,
  keyed on `Prompt` and naming the model, its window and the reserve.
- **A `ContextWindowSize` of `0` means unbounded**, not a budget of zero. Zero is the seeded default
  for a catalog row nobody has filled in, and trimming every conversation to nothing over an unset
  field would be far worse than sending a prompt that might be rejected.
- **A message stored with `tokens = 0` is re-estimated on the fly** rather than counted as free.
- **Trimming decides what is sent, never what is stored.**

Switching models mid-conversation recomputes the budget, because it is computed per turn from the
model that will answer.

## Accuracy telemetry

`ChatMetrics` records `enterprise_gpt.chat.estimator.relative_error` as a histogram, on completed
turns that reported usage and invoked **zero** tools — the only condition under which the estimated
and billed figures measure the same thing.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Transcripts/ITranscriptStore.cs` | The access surface and its partition-key rule |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Transcripts/TranscriptBatch.cs` | Recorded batch operations |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Serialization/CosmosSerialization.cs` | Fixed-width UTC dates |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/ContextBudgetCalculator.cs` | Trimming |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/TokenizerProvider.cs` | Shared vocabularies |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Tokenization/PromptOverhead.cs` | Framing and tool-schema cost |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/` | Transcript tests against the Cosmos emulator |

## Related

- [../architecture/data-model.md](../architecture/data-model.md)
- [usage-and-reporting.md](usage-and-reporting.md)
- [../operations/runbooks.md](../operations/runbooks.md) — the destructive container cutover
