# Summarization

`document_summarize`: a hierarchical map-reduce that turns a whole document — or every document in
a turn's scope — into prose, on a pinned model of its own.

## Why map-reduce with a collapse loop

A **refine** pipeline (a running summary re-compressed against each new chunk) is strictly serial
and lets early content drift as it is recompressed at every step. **Cluster-then-summarize** never
reads the whole document, which is not what "summarize the whole thing" means.

Hierarchical map-reduce reads every chunk exactly once. The **collapse loop** is what plain
map-reduce lacks: an answer for a document whose own partial summaries are still too large to
combine in one call. Without it such a document has no summary at all, rather than a worse one.

## The pipeline

```
chunks -> reassemble -> fits? -> single pass
                          |
                          +-> split into map units -> map (concurrent)
                                                       -> collapse (repeat while oversized)
                                                       -> reduce
```

`DocumentSummarizer` has two entry points: `SummarizeDocumentAsync(documentId, source, ...)`, and
`SummarizeTextAsync(text, ...)`, which runs the same pipeline over arbitrary text. A digest is not a
fourth algorithm — it is this pipeline run over a set of per-document summaries.

### Reassembly

`DocumentTextAssembler` reads the document's chunk rows ordered by `Index` and joins them with
`DocumentRetrievalService.AppendWithoutOverlap`, the same seam-stripping code `document_search` uses.
Chunks are the source rather than the original blob, because re-extracting would re-bill Document
Intelligence for text the database already holds.

- It projects to the **text column alone**; materializing full entities would pull a `vector(1536)`
  embedding per chunk across the wire for a column summarization never reads.
- `DateDeactivated` is filtered on both the chunk and its parent. A document with zero active chunks
  reassembles to an empty string, which becomes a clear validation failure rather than an empty
  prompt.
- **The result is not byte-identical to the original.** The chunker joins its units with `"\n"` and
  its splitting regexes consume the whitespace they split on, so reassembly can collapse blank lines
  and turn inter-sentence spacing into newlines. Every word survives; the original formatting does
  not. The alternatives were re-extracting or persisting a second unchunked copy at ingest.

### The fit decision

`SummarizationBudgetCalculator.Compute` is a pure function — no database, no chat client, no options
object — so the decision most likely to be silently wrong is the most testable in isolation.

```
budget       = (ContextWindowSize - MaxOutputTokens - promptOverheadTokens) * SafetyFraction
MapUnitTokens = PerCallTokens * MapUnitFraction
```

**A `ContextWindowSize <= 0` refuses outright rather than meaning unbounded.** This inverts the
convention `ContextBudgetCalculator` uses for chat turns, deliberately. Treating zero as unbounded
is right for a chat turn — trimming a conversation to nothing over an unset catalog field is worse
than a prompt the provider might reject — and wrong here, where it would mean attempting to send a
multi-million-token document in a single call the moment the row was mis-edited back to zero. It
throws `SummarizerNotConfiguredException`, mapping to 503 `provider-not-configured`.

The check runs on **every call**, not just at startup, because the summarizer row is read fresh each
time. An administrator editing `ContextWindowSize` back to `0` while the app runs is caught on the
next attempt.

Token counting always uses the estimator and tokenizer for the **summarizer's own** provider and
deployment, never the one the requesting conversation selected for chat. A tokenizer fault while
sizing is counted as zero rather than allowed to fail the run: counting low routes an oversized
document down the single-pass path, which the provider rejects loudly instead of the document being
silently mis-summarized.

### Splitting

`MapUnitSplitter.Split` sizes units to `MapUnitTokens` — **explicitly not** the 512-token retrieval
chunk size. Reusing the retrieval geometry would split a document into hundreds of map units where a
handful are needed, multiplying the call count, and the bill, by roughly two orders of magnitude.

In order of preference: paragraphs packed greedily up to the cap; an oversized paragraph split on
sentence boundaries; an oversized sentence sliced by length, snapped off a UTF-16 surrogate pair so
a slice never ends mid-character, with the cut point found by halving rather than a
characters-per-token guess.

**No overlap between units.** Overlap exists in retrieval so a hit near a boundary keeps its
context; a summary reads every unit exactly once, so overlap would only re-send and re-bill text the
neighbour already covers.

## The tool

```
document_summarize(documentName?: string)
```

- **Omitted** — every document in the turn's scope is reduced to one digest.
- **Supplied** — matched against the scope by `DocumentRetrievalService.MatchByName`, the
  exact-then-prefix-then-substring matcher `document_search` uses, extracted to `internal static` so
  the two tools that take a document name agree on what a name means.

**Ambiguity refuses; it does not widen.** Where `document_search` widens an unmatched name to a
search over everything, `document_summarize` refuses before any model call and returns the available
names. A wider search is cheap; summarizing the wrong document is not, and widening would silently
generate and bill a summary of a document the user never named.

```json
{
  "scope": "document",
  "documentCount": 0,
  "availableDocuments": ["policy.pdf", "handbook.pdf"],
  "note": "'polcy.pdf' matches more than one document. Ask again with a full name."
}
```

When no name is given and the scope holds exactly one document, that document is summarized directly
rather than routed through the digest reduce — a one-document digest would be a second model call
whose only job is to reduce one summary to itself.

The result carries `scope`, `documentName`, `documentCount`, `summary`, and on a naming failure
`availableDocuments` and `note`. **No citations** — a summary is prose, not a set of passages, and
the tool prompt tells the model not to invent page numbers for one.

Progress reports through the ambient chat-progress reporter, reaching the client as ordinary
`ActivityStarted` / `ActivityProgress` / `ActivityCompleted` frames. Progress matters more here than
for search: a first summary of a large document takes a noticeable while, and a silent tool on an
inline turn looks exactly like a stalled answer.

### Deadlines and failure

`ToolTimeoutSeconds` (default 300) bounds the **whole invocation**; `CallTimeoutSeconds` (default
180) bounds one model call inside the engine. Startup validates that the first is at least the
second — a run deadline shorter than one call's timeout would kill every run before its first call
returned, reading as a broken summarizer rather than a misconfigured one.

A breach surfaces to the model as a sanitized sentence rather than failing the turn. The turn's own
cancellation is left to propagate as cancellation. Every other terminal failure is caught and
replaced with a sanitized exception before it reaches the model, with the real one always logged
first — nothing about a provider error, a deployment name or a stack trace reaches the model or the
transcript.

## Persistence and billing

One canonical summary row per document (`ConversationDocumentSummary` / `ProjectDocumentSummary`),
unique behind an index filtered on `[DateDeactivated] IS NULL`, so a second turn asking the same
question reuses it and reports "Reusing an existing summary".

A run writes **one** `ConversationUsage` row of kind `Summarization`, not one per model call, and
never a second one on the same turn.

## Configuration — `Summarization`

```json
{
  "ModelId": "<catalog guid>",
  "SafetyFraction": 0.85,
  "MapUnitFraction": 0.9,
  "MaxConcurrency": 4,
  "CallTimeoutSeconds": 180,
  "ToolTimeoutSeconds": 300,
  "MaxCollapsePasses": 5,
  "MaxCallRetries": 2
}
```

| Key | Default | Meaning |
| --- | --- | --- |
| `SafetyFraction` | 0.85 | Fraction of the raw per-call budget a call may occupy, absorbing the estimate's approximation |
| `MapUnitFraction` | 0.90 | Fraction of the per-call budget one map unit may occupy — below the per-call figure, since a unit is sized before the map prompt's framing is added |
| `MaxConcurrency` | 4 | Calls in flight for one document's map phase, bounded so one document cannot monopolize the gate it shares with ingestion |
| `CallTimeoutSeconds` | 180 | One call |
| `MaxCollapsePasses` | 5 | Re-summarization passes before the run fails naming the limit |
| `MaxCallRetries` | 2 | Retries per call; `0` is a supported choice |

The summarizer runs on a **pinned catalog model** identified by `ModelId`, validated at startup by
`SummarizerBootstrapper` and resolved fresh on every call so an administrator's edit takes effect
without a restart.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Summarization/DocumentSummarizer.cs` | The four-stage pipeline |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Summarization/SummarizationBudget.cs` | The fit decision and its zero-window refusal |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Summarization/MapUnitSplitter.cs` | Splitting strategy |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Summarization/DocumentTextAssembler.cs` | Chunk reassembly |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Summarization/SummarizerModelResolver.cs` | The pinned model, read fresh |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Tool/DocumentSummaryTool.cs` | The model-facing declaration |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Summarization/` | Budget, splitting and collapse tests |

## Related

- [retrieval.md](retrieval.md)
- [../models/catalog.md](../models/catalog.md)
- [../conversations/usage-and-reporting.md](../conversations/usage-and-reporting.md)
