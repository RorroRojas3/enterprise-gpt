# Summarization

`document_summarize`: a hierarchical map-reduce that turns a whole document — or every document in
a turn's scope — into a detailed, cached record, on a pinned model of its own.

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

Five templates back this, one per call shape: single-pass, map, collapse, reduce, and digest — see
[What a summary contains](#what-a-summary-contains) for what each is told to produce.

`DocumentSummarizer` has two entry points. `SummarizeDocumentAsync(documentId, source, ...)` runs a
document through this pipeline from its chunk rows. `SummarizeDigestAsync(summaries, ...)` runs the
same pipeline over a set of already-summarized documents instead. A private `SummaryStage` enum
(`Document` or `Digest`) selects only which framing the run's first, single-call attempt uses —
`BuildSinglePass` or `BuildDigest` — everything downstream of that decision, including the
map/collapse/reduce path, is identical either way. A digest large enough to overflow one call still
falls back to the *document's own* map-reduce framing rather than a digest-shaped map-reduce of its
own: a scope of already-condensed per-document summaries rarely reaches that path, and it was not
worth two more templates to special-case it.

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

The single-call budget (the decision above) is computed against whichever framing the run's stage
picked — `BuildSinglePass` for a document, `BuildDigest` for a digest — because each template's
instructions carry a different token cost, and the decision this budget serves is whether the whole
text rides that one prompt.

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

### The collapse loop's two budgets

The collapse loop tests one budget to decide whether to keep looping and packs against a different
one while it does:

- The **reduce budget** (`SummarizationPrompts.BuildReduce`'s instructions) is the loop's exit
  condition — it is the call that follows the loop, so "do the partials fit now" means "do they fit
  the reduce call".
- The **collapse budget** (`BuildCollapse`'s instructions) is what `GroupToFit` packs partials
  against on a pass inside the loop — it is the call being made on that pass.

The two templates want opposite things. `document-summary-reduce-prompt.md` is now the **final call
only**: it assembles the parts it is given rather than distilling them further, because by the time
reduce runs the parts have already been through however many collapse passes were needed and nothing
should be lost a second time. `document-summary-collapse-prompt.md` is new and runs only on the
loop's intermediate passes: it is explicitly compressive — about half its input, plain prose, no
headings or bullets, keep every specific — because a pass that returns roughly what it was given
never shrinks the set, and the loop fails naming `MaxCollapsePasses` rather than looping forever.

## What a summary contains

The cached summary is a **detailed record**, not a short gist: read every part of the source it
covers back out of it, not a synopsis of it. This applies to the single-pass, map and reduce
templates — the ones whose output is cached or carried into a later call within the same run.

- **Coverage.** Each template states its own coverage rule: the single-pass and reduce templates say
  a section named but not summarized has been dropped, not covered; the map template says its unit
  is the only pass that reads that text, so anything it drops is gone for good.
- **Length scales with the source**, not a fixed target: the single-pass template aims for roughly a
  tenth of the document (more where it is dense with specifics, less where it is boilerplate), never
  shorter than a substantial paragraph and never longer than about 3,000 words. The map template aims
  for about a third of the unit it was given, with the same 3,000-word ceiling. The reduce template
  aims for roughly the combined length of the parts once duplication is merged out, same ceiling.
- **Light markdown structure is permitted** — short headings that follow the document's own sections,
  and bullets where the source is itself a list — with prose everywhere else. The collapse template
  is the one exception: it is read only by the next pass, not cached or shown to anyone, so it stays
  plain prose with no structure for that pass to unpick.

The earlier templates banned all structure on the premise that the summary "is rendered as plain
text". That premise was false: the summary is serialized into the `summary` field of
`DocumentSummaryToolResult`, which only the assistant reads back — no UI surface renders it. The tool
prompt (`document-summary-tool-prompt.md`) now tells the model the summary "can run to several pages"
for a long document, and to relay the part that answers what the user asked rather than pasting the
whole thing back, unless the user asked for the full summary.

**The digest is the deliberate exception to "detailed."** `document-summary-digest-prompt.md` is new
and frames a digest as a brief per-document overview, not a record: a short paragraph per document
naming it and saying what it covers, plus an optional closing paragraph on what the set has in
common. It exists to point the user at the right document, not to reproduce what that document's own
summary already holds. Its labels — the file name each summary came from — ride inside the delimited
fence rather than the framing, for the same injection reason no document name reaches any template's
instructions: a file name is attacker-chosen text a project member could have set.

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

## Cache invalidation — `PromptVersion`

`SummarizationPrompts.PromptVersion` is the entire cache-invalidation mechanism. A document's text
never changes after ingestion, so the only thing that can make a stored summary stale is a change to
the prompts that produced it — there is no other signal to invalidate on. `ReadCachedTextAsync` reads
a live summary row only when its stored `PromptVersion` matches the running one; a row from an older
generation is treated as a cache miss, not served, and the tool regenerates it on that request.

Bump the constant whenever a template change would change what the model returns. It went from `"1"`
to `"2"` for the rewrite described above, so every summary already cached is regenerated once — at
the cost of one more run — the next time each document is asked for; nothing is bulk-regenerated up
front. The old row is not deleted or versioned: `PersistAsync` looks up the live row by document
alone (`DateDeactivated IS NULL`, no `PromptVersion` filter) and overwrites it in place with the new
text and the new `PromptVersion`, so there is no summary version history to reconcile.

## Truncation

`DocumentSummarizer` reads `ChatResponse.FinishReason` on every call. A completion cut off at the
deployment's output cap (`FinishReason == Length`) is **kept, not discarded** — it is still the best
answer the run has, and retrying against the same cap would reproduce the same cut-off text — but the
call is logged as a warning, and `SummarizationRun.Truncated` is set true if *any* call in the
run — a map call, a collapse pass, the reduce, or a single-pass call — was cut off.
`DocumentSummaryService.MeasureAsync` reports that as metric outcome `"truncated"` (the
`summary.outcome` dimension on `enterprise_gpt.document_summary.run.duration`) instead of
`"success"`, so a spike in truncation is visible without treating the run as a failure — it did
produce a summary, it did get cached, and it did get billed.

A different failure mode does still fail the call: a reasoning deployment that spends its whole
output cap on reasoning tokens before writing any visible text produces both `Truncated` and an empty
completion. That case is no longer retried as a transient `EmptyCompletionException` — a retry against
the same cap reliably reproduces the same empty result, and paying for that retry once per remaining
map unit is pure waste. It fails immediately with a named validation reason instead.

**Known limitation:** `Truncated` is not persisted on the summary row (`BaseDocumentSummary` has no
column for it) — it exists only on the `SummarizationRun` the generating request sees and the metric
that request emits. A summary cached while truncated carries no durable marker; reading it back later
gives no way to tell it apart from a summary that completed cleanly.

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
  "MaxCallRetries": 2,
  "MaxDigestDocuments": 25
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
| `MaxDigestDocuments` | 25 | Documents one digest may reduce over before it refuses, naming the limit, rather than turning one request into an unbounded number of runs |

The summarizer runs on a **pinned catalog model** identified by `ModelId`, validated at startup by
`SummarizerBootstrapper` and resolved fresh on every call so an administrator's edit takes effect
without a restart.

That pinned row's `MaxOutputTokens` was raised from 16,384 to 32,768 (migration
`RaiseSummarizerOutputCap`), giving the longer, more detailed summary contract above headroom that
was tight against the previous cap. On this deployment `MaxOutputTokens` is shared with reasoning
tokens, so the raise widens the model's thinking budget along with its visible-output ceiling. The
engine reserves the whole cap out of the context window before it decides how to split a document
(see [the fit-decision formula](#the-fit-decision)), so on the pinned row's million-token window the
raised cap now reserves about 3% of the input budget before a single token of document text is
counted.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Summarization/DocumentSummarizer.cs` | The single-pass, map, collapse, reduce and digest pipeline |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Summarization/SummarizationBudget.cs` | The fit decision and its zero-window refusal |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Summarization/MapUnitSplitter.cs` | Splitting strategy |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Summarization/DocumentTextAssembler.cs` | Chunk reassembly |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Summarization/SummarizerModelResolver.cs` | The pinned model, read fresh |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Prompts/SummarizationPrompts.cs` | Builds the five templates' prompts; holds `PromptVersion` |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Prompts/document-summary-*.md` | The five call-shaped templates (single-pass, map, collapse, reduce, digest) plus the tool-facing prompt |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Tool/DocumentSummaryTool.cs` | The model-facing declaration |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Summarization/` | Budget, splitting, collapse and truncation tests |

## Related

- [retrieval.md](retrieval.md)
- [../models/catalog.md](../models/catalog.md)
- [../conversations/usage-and-reporting.md](../conversations/usage-and-reporting.md)
