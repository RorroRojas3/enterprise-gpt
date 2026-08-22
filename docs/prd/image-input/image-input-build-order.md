# Build order: Image Input in Conversations

A dependency-resolved execution sequence for the 38 stories in [`image-input.md`](image-input.md), for tracking progress against.

**The PRD is authoritative.** This file adds no requirements and changes no scope. It derives five waves from §8's phase table plus each story's `Depends on` field, and assigns one global `Order` counter across all of them. Where this file and the PRD disagree, the PRD wins and this file is wrong.

A wave here is a **phase**, not a dependency depth: several waves contain internal chains, so a story's `Depends on` may name another story in the same wave. What no story does is depend on one scheduled in a **later** wave — every row below was checked against that rule programmatically, not eyeballed. Within a wave, the `Parallel with` column names the stories that neither depend on it nor are depended on by it — those can be taken concurrently by however many people are available. Wave 1 splits into two lanes that never touch each other, so that column uses lane shorthand there rather than enumerating the ids on every row; every other wave enumerates explicitly, because its cross-story edges are not a clean split.

Status here mirrors the `Status` field on each PRD story. Update both, or update the PRD and re-derive this.

**Progress: 0 / 38 done.**

**Critical path.** `US-101` (the discriminator) → `US-103` (accept an image without ingesting it) → `US-401` (widen the stream request) → `US-402` (the message-level reference) → `US-403` (multimodal `ChatMessage` assembly) → `US-405` (the context-budget image term) → `US-406` (bounded re-attachment) → `US-603` **or** `US-604` (the governance ceiling and telemetry that read what `US-406` produces), eight deep. `US-002` → `US-301` → `US-302` → `US-303` joins it just ahead of `US-404`, which is why EP-3's whole understanding pipeline has to close before EP-4's "always-present description" story can, even though it is not on the longest chain by node count. **Maximum useful concurrency is 6**, reached twice — once in wave 1 (`US-104`/`US-105`/`US-106`/`US-107`/`US-108`/`US-201`, none of which has an edge to any other) and once in wave 3 (`US-404`/`US-405`/`US-407`/`US-408`/`US-503`/`US-504`) — against a build that opens wave 0 with only two stories, one of them waiting on the other.

## Wave 0 — the gate

Two stories, no dependencies beyond each other, and nothing below is estimated in detail until both close.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | 1 | US-001 `[enabler]` Confirm multimodal chat completions from .NET | — | P0 | — | |
| 0 | 2 | US-002 `[enabler]` Confirm the ingest-time vision call | US-001 | P0 | — | |

⛔ **This is a gate, not a phase.** `US-001` pins the package versions, the accepted image formats, and the per-image size ceiling actually enforced by the target deployment — every one of those is read by a later story rather than assumed. `US-002` additionally confirms the ingest-time call's response shape is parseable and measures its latency, which `US-305`'s timeout and `US-002`'s own downstream stories depend on directly. Starting `US-101` or `US-301` before this closes risks building the write path or the registration around a format list or a package choice that changes once real numbers are in.

## Wave 1 — storage and catalog, two lanes

**Lane A** = image storage (EP-1, `US-101` … `US-108`). **Lane B** = model catalog (EP-2, `US-201` … `US-203`). Zero edges between them — no story in one lane names a story in the other in its `Depends on`, checked programmatically. Lane A depends only on `US-001` from the gate; lane B depends on nothing outside itself.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 3 | US-101 `[enabler]` Add the document-type discriminator and migrate existing rows | — | P0 | US-102, all of lane B | |
| 1 | 4 | US-102 `[enabler]` Provision the `images` container and its configuration | — | P0 | US-101, all of lane B | |
| 1 | 5 | US-201 `[enabler]` Add `Model.IsVisionEnabled` | — | P0 | all of lane A | |
| 1 | 6 | US-103 `[enabler]` Accept an image upload without ingesting it | US-101, US-102, US-001 | P0 | US-108, all of lane B | |
| 1 | 7 | US-108 Reclaim images under a retention policy | US-102 | P2 | US-103, US-104, US-105, US-106, US-107, all of lane B | |
| 1 | 8 | US-202 `[enabler]` Surface `IsVisionEnabled` on the DTO and mapper | US-201 | P0 | all of lane A | |
| 1 | 9 | US-104 `[enabler]` Widen the advertised upload surface | US-103 | P0 | US-105, US-106, US-107, US-108, all of lane B | |
| 1 | 10 | US-105 Download a stored image in its own format | US-103 | P1 | US-104, US-106, US-107, US-108, all of lane B | |
| 1 | 11 | US-106 `[enabler]` Serve an inline preview without a SAS in the DOM | US-103 | P1 | US-104, US-105, US-107, US-108, all of lane B | |
| 1 | 12 | US-107 An image is never retrieved or cited | US-103 | P0 | US-104, US-105, US-106, US-108, all of lane B | |
| 1 | 13 | US-203 Mark a model as vision-capable | US-202 | P1 | all of lane A | |

⛔ **Two stories here are gated on an open question rather than on code.** `US-106`'s route shape depends on open question 5 (§9) — the proposed authenticated byte-streaming resolution — and `US-108`'s retention window depends on open question 4. Both are written and estimated against the PRD's proposed defaults; starting either before the product owner confirms risks a rework of the route contract or the deletion schedule, not of the underlying write path.

⚠ **`DocumentEndpoints.cs` and `DocumentService.cs` are a soft collision.** `US-104` (the `file-extensions` route), `US-105` (`ResolveContentType`), and `US-106` (a new preview route) all touch one or the other of these two files, and all three are unblocked by `US-103` at the same moment. None of the three structurally depends on either of the others, so three people can pick them up together — but two of them landing in the same file the same day is a real merge-conflict risk worth a five-minute heads-up in standup, not a scheduling problem this file can solve for you.

## Wave 2 — understanding, plus the frontend work that does not need it yet

**Lane A** = image understanding (EP-3, `US-301` … `US-305`). **Lane B** = the part of the frontend surface that only needs wave 1's shapes (`US-501`, `US-507`) — with one exception: `US-502` needs lane A's `US-302` to exist, because its job-status polling has nothing to poll against until the `Describing` stage is real.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 2 | 14 | US-301 `[enabler]` Configure the ingest-time vision deployment | US-002, US-201 | P0 | US-501, US-507 | |
| 2 | 15 | US-501 Attach an image in the composer | US-104 | P0 | US-301, US-302, US-303, US-304, US-305, US-507 | |
| 2 | 16 | US-507 An unusable image is refused clearly | US-103 | P1 | US-301, US-302, US-303, US-304, US-305, US-501 | |
| 2 | 17 | US-302 `[enabler]` Add the `Describing` job stage and call the vision deployment | US-103, US-301 | P0 | US-501, US-507 | |
| 2 | 18 | US-303 `[enabler]` Persist the extracted description and OCR text | US-302 | P0 | US-304, US-305, US-501, US-502, US-507 | |
| 2 | 19 | US-304 Image understanding fails without losing the attachment | US-302 | P1 | US-303, US-305, US-501, US-502, US-507 | |
| 2 | 20 | US-305 `[enabler]` Bound the vision call's wall-clock time | US-302 | P1 | US-303, US-304, US-501, US-502, US-507 | |
| 2 | 21 | US-502 See an image being processed | US-501, US-302 | P0 | US-303, US-304, US-305, US-507 | |

⛔ **Open question 1 gates `US-301`, and everything in lane A after it.** Which deployment performs the ingest-time extraction is unresolved: the proposed default is a separately configured vision deployment, reused through the existing keyed `IChatClient` resolver with a distinct deployment name. Starting `US-301` on a guess risks a registration rewrite once the product owner picks a provider that is not the one assumed.

## Wave 3 — the turn, plus the frontend work that needs it

EP-4 in full (`US-401` … `US-408`) alongside the four frontend stories that specifically need pieces of it (`US-503` needs `US-402`'s transcript reference; `US-504` needs wave 2's `US-502`; `US-505` needs `US-407`'s stand-down behaviour; `US-506` needs `US-503`).

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 3 | 22 | US-401 `[enabler]` Widen the stream request to carry attachment ids | US-103 | P0 | US-504 | |
| 3 | 23 | US-402 `[enabler]` Carry the attachment reference on the transcript message | US-103, US-401 | P0 | US-504 | |
| 3 | 24 | US-403 `[enabler]` Widen `ChatMessage` assembly to multimodal content | US-001, US-402 | P0 | US-408, US-503, US-504, US-506 | |
| 3 | 25 | US-405 `[enabler]` Add the image-token term to the context budget | US-403 | P0 | US-404, US-407, US-408, US-503, US-504, US-505, US-506 | |
| 3 | 26 | US-404 Every attached image's description is present on every future turn | US-403, US-303 | P0 | US-405, US-406, US-407, US-408, US-503, US-504, US-505, US-506 | |
| 3 | 27 | US-406 Re-attach the most recent images, within a bound | US-403, US-405, US-201 | P1 | US-404, US-407, US-408, US-503, US-504, US-505, US-506 | |
| 3 | 28 | US-407 A non-vision model still knows what an image showed | US-403 | P0 | US-404, US-405, US-406, US-408, US-503, US-504, US-506 | |
| 3 | 29 | US-408 Export renders an image attachment sensibly | US-402, US-303 | P2 | US-403, US-404, US-405, US-406, US-407, US-503, US-504, US-505, US-506 | |
| 3 | 30 | US-503 See a thumbnail, not a file glyph | US-106, US-402 | P1 | US-403, US-404, US-405, US-406, US-407, US-408, US-504, US-505 | |
| 3 | 31 | US-504 Loading and failure states are distinguishable | US-502 | P1 | US-401, US-402, US-403, US-404, US-405, US-406, US-407, US-408, US-503, US-505, US-506 | |
| 3 | 32 | US-505 The composer explains a non-vision model's limitation | US-407 | P1 | US-404, US-405, US-406, US-408, US-503, US-504, US-506 | |
| 3 | 33 | US-506 Use the image surface without a mouse | US-503 | P0 | US-403, US-404, US-405, US-406, US-407, US-408, US-504, US-505 | |

⛔ **Open question 2 gates `US-406`.** The default re-attachment ceiling (proposed `N = 2`) and the per-turn image-token budget derived from `Model.ContextWindowSize` are both vetoable. `US-405`'s budget term is written against whatever number lands, but `US-406`'s own bounding logic cannot be finished until the value is confirmed — starting it on the proposed default risks a rewrite of the "drop the oldest of the recent set" logic if the ceiling shape changes (a hard cap vs. a percentage of the window, for instance).

⚠ **`ConversationService.cs` is the collision this wave exists to warn about.** `US-403` (message assembly, ~line 855), `US-405` (the context budget, ~line 1503), `US-406` (re-attachment policy, built on both), `US-407` (the stand-down path, ~line 2173), and `US-408` (nothing here, but reads what `US-403` produces) all touch one file that already carries over two thousand lines. `US-403` has to land first — every other story in this list depends on it structurally — but once it has, `US-405` and `US-407` are both unblocked by it alone and have no edge to each other, so two people picking them up on the same day is exactly the scenario `image-tool`'s own build order flagged for `Program.cs`: coordinate directly, or have one person take both.

## Wave 4 — governance and hardening

Nothing here has an edge to anything else here — all five stories are mutually independent, checked programmatically — and none may be skipped before the feature is enabled for real users, which is what makes this wave non-optional despite its low story count.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 4 | 34 | US-601 Gate image understanding on a permission | US-203 | P0 | US-602, US-603, US-604, US-605 | |
| 4 | 35 | US-602 Feature-flag image input with a documented rollback | US-301, US-403 | P0 | US-601, US-603, US-604, US-605 | |
| 4 | 36 | US-603 Bound what one conversation and one user can upload | US-406 | P1 | US-601, US-602, US-604, US-605 | |
| 4 | 37 | US-604 Emit image spans and metrics | US-302, US-406 | P1 | US-601, US-602, US-603, US-605 | |
| 4 | 38 | US-605 `[enabler]` Attribute the ingest-time vision call's cost | US-302 | P2 | US-601, US-602, US-603, US-604 | |

⛔ **Open question 3 gates `US-601`.** Whether image understanding is gated by the existing `Upload File` permission or a new `Understand Image` grant is unresolved, inherited from `image-tool`'s own open question 2. Getting this answer during wave 3 — it needs no code to resolve, only a decision — is what keeps this wave a few days rather than a blocked week; the same is true of open questions 2 and 4, both of which this wave's stories (`US-603`, `US-108` back in wave 1) depend on for their exact numbers rather than their shape.
