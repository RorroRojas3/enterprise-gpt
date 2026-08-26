# Build order: Document Summarization

A dependency-resolved execution sequence for the 29 stories in [`document-summarization.md`](document-summarization.md), for tracking progress against.

**The PRD is authoritative.** This file adds no requirements and changes no scope. It derives five waves from §8's phase table plus each story's `Depends on` field, and assigns one global `Order` counter across all of them. Where this file and the PRD disagree, the PRD wins and this file is wrong.

**Re-derived from scratch for the tool pivot, then updated once the implementation caught up.** The previous build order tracked 39 stories across a background-job-and-routes design. That design is gone. This file's dependency graph — every `Depends on` edge, every wave boundary, the critical path, and every "Parallel with" cell — was checked by hand against the revised PRD's story set; no automated script verified it.

**What "done" means here now.** Every story but `US-602` is implemented, tested, and part of a green suite — **1,631 unit tests + 377 integration tests, 0 failures** — but **nothing in this feature is committed**. Treat "Done" in this file as "implemented, tested, and ready for code review and commit," not as "shipped." `US-602` (the per-user/per-conversation request-rate bound) is the one story with no code behind it at all.

**Progress: 28 / 29 done** (28 done: EP-1 through EP-5 in full, plus `US-601`/`US-603` from EP-6 · 1 not started: `US-602`).

**What changed since the last version of this file.** Every EP-3/EP-4/EP-5 story that was "In progress" (implemented, untested) is now "Done" (implemented and tested): 41 new unit tests and 4 new integration tests were added. The §6/wave-4 `ChatUsageScope` risk this file previously called the single highest-priority open item is **resolved** — it was real (a nested tracked call put 900,000 tokens on the wrong usage row), it is fixed (`DocumentSummaryService.RunDetachedAsync`), and it is regression-tested (`ConversationServiceTests.StreamConversationAsync_SummaryToolMakesItsOwnModelCall_DoesNotBillItToTheTurn`). The `PermissionEndpointFilter`/`UserGrantReader` duplication this file flagged as a low-priority follow-up is also resolved — the filter's own loader is deleted. Neither risk gates anything below anymore.

**Critical path.** `US-101 → US-102 → US-105 → US-201 → US-202 → US-203 → US-205 → US-206 → US-207 → US-402 → US-502 → US-503 → US-601`, thirteen stories deep, unchanged in shape from the previous version of this file — the story statuses moved, the graph did not. `US-602` ties `US-601` for the deepest point in the graph (depth 13) and is the only story left with no implementation. **Maximum useful concurrency is 4**, reached in wave 3 — now moot for scheduling purposes, since wave 3 is entirely done, but left in for anyone auditing the graph.

## Wave 1 — catalog and configuration — Done

Unchanged from before the pivot. All five stories are shipped, committed, and tested.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | US-101 `[enabler]` Add `Model.IsUserSelectable` and migrate existing rows to `true` | — | P0 | — | Done |
| 1 | 2 | US-103 `[enabler]` Surface `IsUserSelectable` and exclude the summarizer from the chat picker | US-101 | P0 | US-102 | Done |
| 1 | 3 | US-102 `[enabler]` Correct the seeded summarizer catalog row | US-101 | P0 | US-103, US-104 | Done |
| 1 | 4 | US-104 Administrator can toggle whether a model is user-selectable | US-103 | P2 | US-102, US-105 | Done |
| 1 | 5 | US-105 `[enabler]` Configure the summarizer model, validated at startup | US-102, US-103 | P0 | US-104 | Done |

## Wave 2 — the summarization engine — Done

Unchanged from before the pivot. All eight stories are shipped, committed, and tested, including the map-unit ceiling (`_options.MaxMapUnits`), now covered by two further `DocumentSummarizerTests` cases.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 2 | 6 | US-201 `[enabler]` Refuse to summarize when the summarizer's context window is unconfigured | US-105 | P0 | — | Done |
| 2 | 7 | US-202 `[enabler]` Reassemble a document's text from its chunk rows | US-201 | P0 | US-208 | Done |
| 2 | 8 | US-208 `[enabler]` Prompts that delimit untrusted document text | US-201 | P0 | US-202, US-203 | Done |
| 2 | 9 | US-203 `[enabler]` Decide whether a document fits a single pass | US-202 | P0 | US-208 | Done |
| 2 | 10 | US-204 Summarize a document that fits in a single call | US-203, US-208 | P0 | US-205, US-206, US-207 | Done |
| 2 | 11 | US-205 `[enabler]` Split an oversized document into window-sized map units and summarize them under bounded concurrency | US-203, US-208 | P0 | US-204 | Done |
| 2 | 12 | US-206 Collapse loop reduces partial summaries that still overflow the budget | US-205 | P0 | US-204 | Done |
| 2 | 13 | US-207 `[enabler]` Final reduce produces one canonical summary | US-206, US-208 | P0 | US-204 | Done |

## Wave 3 — persistence and accounting — Done

Two epics (EP-3: `US-301`, `US-302`; EP-4: `US-401`–`US-406`), interleaved rather than sequential phases. **All eight stories are now implemented and tested** — `Enterprise.Gpt.Service/Summarization/DocumentSummaryService.cs`, the two summary entities/configurations, the migration, the four extended soft-delete cascades (each with its own integration test in `Persistence/DocumentSummaryCascadeIntegrationTests`), and `ChatMetrics.RecordSummaryRun` — covered by `Summarization/DocumentSummaryServiceTests` (12 tests) plus the cascade integration tests. Still uncommitted.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 3 | 14 | US-301 `[enabler]` Add the document-summary tables and their migration | US-207 | P0 | US-401, US-402, US-403, US-405, US-406 | Done |
| 3 | 15 | US-401 `[enabler]` Append `ConversationUsageKinds.Summarization` | — | P0 | US-301, US-302 | Done |
| 3 | 16 | US-402 `[enabler]` Write one usage row per summarization run, billed to the requesting conversation, and only that row | US-401, US-207 | P0 | US-301, US-302 | Done |
| 3 | 17 | US-302 `[enabler]` Join summaries to the four soft-delete cascades | US-301 | P0 | US-401, US-402, US-403, US-404, US-405, US-406 | Done |
| 3 | 18 | US-403 `[enabler]` Increment conversation token counters in the same transaction | US-402 | P1 | US-301, US-302, US-404, US-405 | Done |
| 3 | 19 | US-404 Persist run totals on the summary row | US-301, US-402 | P1 | US-302, US-403, US-405, US-406 | Done |
| 3 | 20 | US-405 `[enabler]` Emit summarization run-level spans and metrics | US-402 | P1 | US-301, US-302, US-403, US-404, US-406 | Done |
| 3 | 21 | US-406 A cache hit costs nothing, provably | US-402, US-403 | P1 | US-301, US-302, US-404, US-405 | Done |

`US-402` additionally required a fix this build order did not originally anticipate: `RunDetachedAsync`, isolating the run's own model calls from the ambient usage tracker so they cannot land on an enclosing turn's own `ConversationUsage` row (PRD §6, invariant 4). This was discovered, proven, and fixed inside this wave's scope rather than as a separate story, since it is precisely what "one usage row per run, billed to the requesting conversation" (`US-402`'s own title) requires.

## Wave 4 — tool integration — Done

Single epic (EP-5, `US-501`–`US-505`). **All five stories are implemented and tested** — `Enterprise.Gpt.Service/Tool/DocumentSummaryTool.cs` (14 tests in `Tool/DocumentSummaryToolTests`), `Enterprise.Gpt.Service/Caching/UserGrantReader.cs` (covered by `Filters/PermissionEndpointFilterTests`, now the sole grant-loading path), and the `ConversationService.CreateChatOptionsAsync` wiring (five new `ConversationServiceTests` cases). Still uncommitted.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 4 | 22 | US-501 `[enabler]` Extract a shared permission-grant reader | — | P0 | US-502, US-504, US-505 | Done |
| 4 | 23 | US-502 `[enabler]` Build the `document_summarize` tool | US-208, US-402 | P0 | US-501 | Done |
| 4 | 24 | US-503 Attach the tool to a turn | US-501, US-502 | P0 | US-504, US-505 | Done |
| 4 | 25 | US-504 Digest across every document when no name is given | US-502 | P0 | US-501, US-503, US-505 | Done |
| 4 | 26 | US-505 `[enabler]` Bound the whole tool invocation with its own deadline | US-502 | P0 | US-501, US-503, US-504 | Done |

⛔ **The `ChatUsageScope` risk this file previously flagged ahead of this wave (PRD §6, invariant 4) is resolved.** `document_summarize`'s own model calls run on an execution context detached from the outer turn's tracked pipeline via `ExecutionContext.SuppressFlow()` (with `Activity.Current` carried across by hand, so tracing is unaffected), and `ConversationServiceTests.StreamConversationAsync_SummaryToolMakesItsOwnModelCall_DoesNotBillItToTheTurn` fails without that isolation in place. This was the single highest-priority test in this build; it now exists and passes.

## Wave 5 — governance and rollout

Single epic (EP-6, `US-601`–`US-603`; `US-604` dropped — see the PRD's EP-6 closing note). Two of the three stories are implemented and tested; the third (`US-602`) has no code at all.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 5 | 27 | US-601 Feature-flag document summarization with a no-redeploy rollback | US-105, US-503 | P0 | US-602, US-603 | Done |
| 5 | 28 | US-602 Bound how often one user or conversation may invoke summarization | US-503 | P1 | US-601, US-603 | **Not started** |
| 5 | 29 | US-603 Cap the digest's maximum document count | US-504 | P1 | US-601, US-602 | Done |

⚠ **`US-602` is now the only story in this entire document with no code behind it.** Every other story is implemented and tested; only committing and code review stand between this feature and being shippable, plus this one story. It has no proposed default in the PRD's open questions, unlike every other numeric knob this feature ships — that is a product decision to make before anyone starts writing it, not an implementation detail to guess at.

## Before this ships: not a story, but not optional either

Nothing in this feature — not one of the twenty-eight "Done" stories above, not their tests, not the migration — is committed. The full suite is green (1,631 unit + 377 integration, 0 failures) against the *working tree*, which is not the same claim as "this is in `master`." Code review and commit are the actual next actions here, ahead of or alongside `US-602`.
