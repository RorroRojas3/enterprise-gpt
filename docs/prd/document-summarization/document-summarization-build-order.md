# Build order: Document Summarization

A dependency-resolved execution sequence for the 39 stories in [`document-summarization.md`](document-summarization.md), for tracking progress against.

**The PRD is authoritative.** This file adds no requirements and changes no scope. It derives five waves from §8's phase table plus each story's `Depends on` field, and assigns one global `Order` counter across all of them. Where this file and the PRD disagree, the PRD wins and this file is wrong.

A wave here is a **phase**, not a dependency depth: several waves contain internal chains, so a story's `Depends on` may name another story in the same wave. What no story does is depend on one scheduled in a **later** wave or a later `Order` position — every row below was checked against that rule **programmatically** (a small script walking the full dependency graph and asserting no edge points forward), not eyeballed; the script's output is reproduced in the notes below each table where it is most load-bearing. Within a wave, the `Parallel with` column names the stories that are **transitively** independent of it — neither it nor they reach one another through any chain of `Depends on` edges — computed the same way, not just checked for a direct edge. Wave 4 splits into two lanes that never touch each other across the whole wave, so that column uses lane shorthand there rather than enumerating every id on every row; every other wave enumerates explicitly, because its cross-story edges are not a clean split.

Status here mirrors the `Status` field on each PRD story. Update both, or update the PRD and re-derive this.

**Progress: 5 / 39 done.**

**Critical path.** `US-101` (add `Model.IsUserSelectable`) → `US-102` (correct the seeded catalog row) → `US-105` (configure and validate the summarizer) → `US-201` (refuse when the context window is unconfigured) → `US-202` (reassemble chunk text) → `US-203` (the fit decision) → `US-205` (split into map units) → `US-206` (the collapse loop) → `US-207` (the final reduce) → `US-301` (the summary tables) → `US-303` (the `Summarizing` job stage) → `US-309` (job-status polling) → `US-503` (the frontend polling store) → `US-501` (request a summary) → `US-502` (view a summary) → `US-507` (bundle-budget guard), sixteen stories deep. This is the longest chain by node count, verified by the same script that checked wave ordering, and it is one story shorter than before, not two: `US-102` still depends on `US-101`, so `US-101` now opens the chain directly rather than joining it partway through. It notably ends **inside EP-5**, not EP-6 — every EP-6 story is shallower in the dependency graph (deepest at 15), but EP-6 is still scheduled last, by policy stated in the PRD's §8, because none of its stories may be skipped before the feature reaches real users. `US-401` → `US-402` → `US-403` joins the graph from EP-4 at a comparable depth but terminates sooner, since nothing in EP-6 or elsewhere chains more than two stories deep off it. **Maximum useful concurrency is 7**, reached once, in wave 4 (`US-404`/`US-405`/`US-406`/`US-501`/`US-504`/`US-505`/`US-506`, none of which has a transitive edge to any other in that set) — unaffected by the earlier removal, since wave 4's own membership and internal edges never referenced it — against a build that now opens with a single story, `US-101`, that both `US-102` and `US-103` wait on before wave 1 broadens out, and closes wave 5 with four fully independent stories.

## Wave 1 — catalog and configuration

Single epic (EP-1, `US-101` … `US-105`); no lane split is needed. `US-101` is the wave's only story with no dependency at all — every other EP-1 story waits on it, directly (`US-102`, `US-103`) or transitively (`US-104`, `US-105`) — so it is the one story that must land before the rest of the epic can proceed.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | US-101 `[enabler]` Add `Model.IsUserSelectable` and migrate existing rows to `true` | — | P0 | — | Done |
| 1 | 2 | US-103 `[enabler]` Surface `IsUserSelectable` and exclude the summarizer from the chat picker | US-101 | P0 | US-102 | Done |
| 1 | 3 | US-102 `[enabler]` Correct the seeded summarizer catalog row | US-101 | P0 | US-103, US-104 | Done |
| 1 | 4 | US-104 Administrator can toggle whether a model is user-selectable | US-103 | P2 | US-102, US-105 | Done |
| 1 | 5 | US-105 `[enabler]` Configure the summarizer model, validated at startup | US-102, US-103 | P0 | US-104 | Done |

## Wave 2 — the summarization engine

Single epic (EP-2, `US-201` … `US-208`); the longest single-epic stretch of new algorithmic code in the document.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 2 | 6 | US-201 `[enabler]` Refuse to summarize when the summarizer's context window is unconfigured | US-105 | P0 | — | |
| 2 | 7 | US-202 `[enabler]` Reassemble a document's text from its chunk rows | US-201 | P0 | US-208 | |
| 2 | 8 | US-208 `[enabler]` Prompts that delimit untrusted document text | US-201 | P0 | US-202, US-203 | |
| 2 | 9 | US-203 `[enabler]` Decide whether a document fits a single pass | US-202 | P0 | US-208 | |
| 2 | 10 | US-204 Summarize a document that fits in a single call | US-203, US-208 | P0 | US-205, US-206, US-207 | |
| 2 | 11 | US-205 `[enabler]` Split an oversized document into window-sized map units and summarize them under bounded concurrency | US-203, US-208 | P0 | US-204 | |
| 2 | 12 | US-206 Collapse loop reduces partial summaries that still overflow the budget | US-205 | P0 | US-204 | |
| 2 | 13 | US-207 `[enabler]` Final reduce produces one canonical summary | US-206, US-208 | P0 | US-204 | |

⛔ **`US-201` is the single most important story in this document, and everything else in this wave sits behind it.** It inverts `ContextBudgetCalculator.Apply`'s own `<= 0`-means-unbounded convention into `<= 0`-means-refuse for this feature specifically (PRD §6, invariant 1). Nothing that reads a token budget in this wave — the fit decision, the map split, the collapse loop — is safe to build against a summarizer row whose `ContextWindowSize` this story has not yet guarded.

⚠ **Open questions 1 and 2 (§9) gate the exact numbers `US-203` and `US-205` ship with, not their shape.** The safety fraction (proposed `0.85`), the map-unit sizing fraction (proposed `90%` of the per-call budget), the bounded concurrency degree (proposed `4`), and the per-call timeout (proposed `3 minutes` as a placeholder) are all vetoable — none is set from a measured baseline, since this cycle scopes no pre-production latency measurement against the real deployment (PRD §9 assumptions). Starting either story on the proposed defaults risks a constant-value change, not a redesign, once the product owner and backend engineer confirm them — the PRD recommends resolving these during wave 1 so wave 2 opens with real numbers rather than placeholders.

## Wave 3 — persistence, job and API

Single epic (EP-3, `US-301` … `US-309`).

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 3 | 14 | US-301 `[enabler]` Add the document-summary tables and their migration | US-207 | P0 | — | |
| 3 | 15 | US-302 `[enabler]` Join summaries to the four soft-delete cascades | US-301 | P0 | US-303, US-304, US-305, US-306, US-307, US-308, US-309 | |
| 3 | 16 | US-303 `[enabler]` Add the `Summarizing` job stage and wire the background job | US-207, US-301 | P0 | US-302 | |
| 3 | 17 | US-304 Request a conversation document's summary | US-303, US-208 | P0 | US-302, US-309 | |
| 3 | 18 | US-305 Request a project document's summary | US-304 | P0 | US-302, US-306, US-309 | |
| 3 | 19 | US-306 Read a stored conversation document summary | US-304 | P1 | US-302, US-305, US-308, US-309 | |
| 3 | 20 | US-307 Read a stored project document summary | US-305, US-306 | P1 | US-302, US-308, US-309 | |
| 3 | 21 | US-308 Request the conversation-level digest | US-304, US-305, US-207 | P1 | US-302, US-306, US-307, US-309 | |
| 3 | 22 | US-309 `[enabler]` Job status polling surfaces the `Summarizing` stage with no client change | US-303 | P1 | US-302, US-304, US-305, US-306, US-307, US-308 | |

⚠ **`DocumentEndpoints.cs` and `ConversationEndpoints.cs` are a soft collision, split across two files by design.** `US-304`, `US-305`, `US-306`, and `US-307` all add routes to `DocumentEndpoints.cs`'s `api/documents` group, while `US-308` adds one route to `ConversationEndpoints.cs`'s `api/conversations` group. None of the five structurally depends on more than one of the others, so several people can pick these up together — but the four landing in the same file the same day is a real merge-conflict risk worth a five-minute heads-up in standup, not a scheduling problem this file can solve for you.

## Wave 4 — accounting and frontend, two lanes

**Lane A** = token accounting and telemetry (EP-4, `US-401` … `US-406`). **Lane B** = the frontend surface (EP-5, `US-501` … `US-507`). Zero edges between them in either direction — no story in one lane names a story in the other in its `Depends on`, and no story in one lane is reachable from the other through any chain, checked programmatically. Lane A depends only on `US-401`/`US-207`/`US-301` from earlier waves; lane B depends only on `US-304`/`US-308`/`US-309` from wave 3.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 4 | 23 | US-401 `[enabler]` Append `ConversationUsageKinds.Summarization` | — | P0 | all of lane B | |
| 4 | 24 | US-503 `[enabler]` Store: poll a summarization job | US-309 | P0 | all of lane A | |
| 4 | 25 | US-402 `[enabler]` Write one usage row per summarization model call, billed to the requesting conversation | US-401, US-207 | P0 | all of lane B | |
| 4 | 26 | US-403 `[enabler]` Increment conversation token counters in the same transaction | US-402 | P1 | US-404, US-405, all of lane B | |
| 4 | 27 | US-404 Persist run totals on the summary row | US-301, US-402 | P1 | US-403, US-405, US-406, all of lane B | |
| 4 | 28 | US-405 `[enabler]` Emit summarization spans and metrics | US-402 | P1 | US-403, US-404, US-406, all of lane B | |
| 4 | 29 | US-501 Request a document's summary | US-304, US-503 | P0 | all of lane A, US-504, US-505, US-506 | |
| 4 | 30 | US-504 Request the conversation digest | US-308, US-503 | P1 | all of lane A, US-501, US-502, US-505, US-506 | |
| 4 | 31 | US-505 Loading, failure, absent and stale states are distinguishable | US-503 | P1 | all of lane A, US-501, US-502, US-504, US-506, US-507 | |
| 4 | 32 | US-506 Use the summarize surface without a mouse | US-503 | P0 | all of lane A, US-501, US-502, US-504, US-505, US-507 | |
| 4 | 33 | US-406 A cache hit costs nothing, provably | US-402, US-403 | P1 | US-404, US-405, all of lane B | |
| 4 | 34 | US-502 View a document's summary | US-501 | P0 | all of lane A, US-504, US-505, US-506 | |
| 4 | 35 | US-507 `[enabler]` Keep the summarize surface off the initial bundle | US-501, US-502, US-504 | P0 | all of lane A, US-505, US-506 | |

⛔ **The two lanes never touch, which is why this is the widest wave in the build.** Every one of lane A's six stories is transitively unrelated to every one of lane B's seven, verified the same way as every other independence claim in this file — a full backend and a full frontend engineer can work this wave in parallel from the first day, coordinating only at the wave's close when `US-507` confirms lane B's bundle cost.

## Wave 5 — governance and rollout

Single epic (EP-6, `US-601` … `US-604`). Nothing here has an edge to anything else here — all four stories are mutually independent, checked programmatically — and none may be skipped before the feature is enabled for real users, which is what makes this wave non-optional despite its low story count.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 5 | 36 | US-601 Feature-flag document summarization with a no-redeploy rollback | US-105, US-304, US-308 | P0 | US-602, US-603, US-604 | |
| 5 | 37 | US-602 Bound what one conversation, one user, and one document can request | US-205, US-304 | P1 | US-601, US-603, US-604 | |
| 5 | 38 | US-603 Cap the digest's maximum document count | US-308, US-405 | P1 | US-601, US-602, US-604 | |
| 5 | 39 | US-604 `[enabler]` Persist per-map-stage partial summaries so a restart resumes rather than re-pays | US-303, US-402 | P1 | US-601, US-602, US-603 | |

⛔ **Open questions 3, 4, and 5 (§9) gate this wave's exact numbers and one scope decision, not its shape.** `US-602`'s map-unit ceiling (proposed `200`) and per-user request bound (proposed `50`/day), and `US-603`'s digest document-count ceiling (proposed `25`), are all vetoable constants that need no redesign to change. `US-604` is the one story in this wave that can be dropped outright rather than merely retuned: the PRD carries the decision to ship it or accept the gap as documented as an explicit open question, and — verified against every `Depends on` field in this document — nothing else names `US-604`, so dropping it changes no other story's schedule.
