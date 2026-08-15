# Build order: File Agent

A dependency-resolved execution sequence for the 44 stories in [`file-agent.md`](file-agent.md), for tracking progress against.

**The PRD is authoritative.** This file adds no requirements and changes no scope. It derives five waves from §8's phase table plus each story's `Depends on` field, and assigns one global `Order` counter across all of them. Where this file and the PRD disagree, the PRD wins and this file is wrong.

A wave here is a **phase**, not a dependency depth: waves 1 through 3 each contain internal chains, so a story's `Depends on` may name another story in the same wave. What no story does is depend on one scheduled in a **later** wave. Within a wave, the `Parallel with` column names the stories that neither depend on it nor are depended on by it — those can be taken concurrently by however many people are available. Because wave 1 splits into three lanes that never touch each other, that column uses lane shorthand there rather than enumerating thirteen ids per row.

Status here mirrors the `Status` field on each PRD story. Update both, or update the PRD and re-derive this.

**Progress: 0 / 44 done.**

**Critical path.** `US-001` (Code Interpreter spike) → `US-301` (`AIProjectClient` registration) → `US-302` (per-request container) → `US-303` (files in and out) → `US-403` (create a document) → `US-404` (deterministic verification) → `US-604` (progress rendering), with `US-401` → `US-402` joining ahead of `US-403`. Seven waves-deep counting the join, and every link of it is in lane C or the epic that consumes lane C — which is why §8 spends schedule pressure there and treats lane A as the place to put anyone idle. **Maximum useful concurrency is 10**, at the start of wave 3, where thirteen stories carry only three edges between them; wave 1 by contrast opens with only four ready stories (one per lane root, plus lane A's second root) despite also holding thirteen.

## Wave 0 — the gate

Two stories, no dependencies, and nothing else may be estimated in detail until both close. Code Interpreter is not in every region, the .NET Foundry Agents SDK is in preview, and the `gpt-image-1` series is limited-access while `gpt-image-2` is generally available.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | 1 | US-001 `[enabler]` Prove Code Interpreter runs from .NET with a per-request container | — | P0 | US-002 | |
| 0 | 2 | US-002 `[enabler]` Confirm the model deployments this feature needs | — | P0 | US-001 | |

## Wave 1 — three parallel lanes

Three lanes with no edge between them: **A** = storage (EP-2), **B** = image (EP-1), **C** = Code Interpreter (EP-3). Lane A depends on no Azure capability and can start during wave 0. Lanes B and C both add registrations to `Program.cs`, which is the plan's one real merge collision — see the note under this table.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 3 | US-201 `[enabler]` Add the document-type discriminator and migrate existing rows | — | P0 | US-202, all of lanes B and C | |
| 1 | 4 | US-202 `[enabler]` Provision the `generated` container and its configuration | — | P0 | US-201, all of lanes B and C | |
| 1 | 5 | US-101 `[enabler]` Register the image-generation client, options and flag | US-002 | P0 | all of lanes A and C | |
| 1 | 6 | US-301 `[enabler]` Register `AIProjectClient` with options, flag and startup validation | US-001 | P0 | all of lanes A and B | |
| 1 | 7 | US-203 `[enabler]` Persist a generated file without ingesting it | US-201, US-202 | P0 | all of lanes B and C | |
| 1 | 8 | US-102 `[enabler]` Build the `generate_image` function with real usage reporting | US-101 | P0 | all of lanes A and C | |
| 1 | 9 | US-302 `[enabler]` Run Python in a per-request isolated container | US-301 | P0 | all of lanes A and B | |
| 1 | 10 | US-204 Download a generated file in its own format | US-203 | P0 | US-205, US-206, all of lanes B and C | |
| 1 | 11 | US-205 `[enabler]` Carry the generated-file reference on the assistant message | US-203 | P0 | US-204, US-206, all of lanes B and C | |
| 1 | 12 | US-206 A generated document is never retrieved or cited | US-203 | P0 | US-204, US-205, all of lanes B and C | |
| 1 | 13 | US-303 `[enabler]` Get files into the container and artifacts back out | US-302 | P0 | US-305, all of lanes A and B | |
| 1 | 14 | US-305 Bound a Code Interpreter run's cost and duration | US-302 | P1 | US-303, US-304, all of lanes A and B | |
| 1 | 15 | US-304 A run that produces no file fails as a named error | US-303 | P1 | US-305, all of lanes A and B | |

⚠ **`Program.cs` and the tool funnel are the collision.** US-101 (lane B) and US-301 (lane C) both add a registration block to `Program.cs`, and US-103 in wave 2 adds the first new tool to `CreateChatOptionsAsync` — the single funnel that assigns `chatOptions.Tools` exactly once at the end. The PRD's resolution is to sequence US-103 **after** US-301 so the registration block is edited by one lane first and the funnel gains its first new tool only once; that is why US-103 sits in wave 2 rather than beside US-102. Whoever takes US-101 and US-301 should still coordinate directly — they are two edits to the same region of one file, in the same wave, by different people.

## Wave 2 — the join, plus parallel work

EP-4's first three stories are the join and need all three lanes. Beside them, not behind them: EP-1's attachment stories, EP-5's two enablers, and EP-6's chip and download work, which needs only lane A's contract and is testable against a hand-inserted `Generated` row with no agent in existence.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 2 | 16 | US-401 `[enabler]` Compose and name the File Agent behind an abstraction | US-002, US-301 | P0 | US-103, US-104, US-105, US-601, US-602, US-603 | |
| 2 | 17 | US-103 Ask the assistant for an image | US-102, US-301 | P0 | US-401, US-402, US-403, US-501, US-502, US-601, US-602, US-603 | |
| 2 | 18 | US-601 Show a generated file on the assistant message | US-205 | P0 | US-401, US-402, US-403, US-501, US-502, US-103, US-104, US-105 | |
| 2 | 19 | US-402 `[enabler]` Attach the agent as one tracked tool | US-401 | P0 | US-103, US-104, US-105, US-601, US-602, US-603 | |
| 2 | 20 | US-104 Keep a generated image after the turn ends | US-103, US-203 | P1 | US-105, US-401, US-402, US-403, US-501, US-502, US-601, US-602, US-603 | |
| 2 | 21 | US-105 Image generation fails without taking the turn with it | US-103 | P1 | US-104, US-401, US-402, US-403, US-501, US-502, US-601, US-602, US-603 | |
| 2 | 22 | US-602 Download a generated file on click | US-601, US-204 | P0 | US-603, US-401, US-402, US-403, US-501, US-502, US-103, US-104, US-105 | |
| 2 | 23 | US-603 A generated file survives a reload | US-601, US-205 | P1 | US-602, US-401, US-402, US-403, US-501, US-502, US-103, US-104, US-105 | |
| 2 | 24 | US-403 Create a document from a prompt | US-402, US-203, US-303 | P0 | US-501, US-502, US-103, US-104, US-105, US-601, US-602, US-603 | |
| 2 | 25 | US-501 `[enabler]` Fill the `ModelId` and `DeploymentName` nulls for agent rows | US-402 | P0 | US-403, US-103, US-104, US-105, US-601, US-602, US-603 | |
| 2 | 26 | US-502 `[enabler]` Add the deferred `(ModelId, DateCreated)` index | US-501 | P1 | US-403, US-103, US-104, US-105, US-601, US-602, US-603 | |

US-501 and US-502 are the split §8 calls for: the enabler ships here, in the wave the agent first reports a model, while the assertion that the arithmetic is right (US-503) waits for wave 3, because it cannot be verified until a real agent turn with real nested tool calls exists to measure.

## Wave 3 — needs a working agent

The widest wave and the least constrained: thirteen stories carrying three edges between them. EP-6's progress rendering is deliberately here rather than in wave 2 — activity frames only become real once the agent emits them, and a fixture-only implementation would be re-tested against the real stream anyway.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 3 | 27 | US-404 Verify the artifact before returning it | US-403 | P0 | all of wave 3 except US-406 | |
| 3 | 28 | US-409 Stand down or fail cleanly | US-402 | P0 | all of wave 3 except US-605 | |
| 3 | 29 | US-410 Stop a File Agent turn mid-flight | US-403 | P1 | all of wave 3 | |
| 3 | 30 | US-505 Emit File Agent spans and metrics | US-401 | P1 | all of wave 3 | |
| 3 | 31 | US-504 Measure Code Interpreter session cost | US-302 | P1 | all of wave 3 | |
| 3 | 32 | US-503 Nested token attribution is correct end to end | US-501, US-403 | P0 | all of wave 3 | |
| 3 | 33 | US-604 Watch the File Agent work | US-402 | P1 | all of wave 3 except US-605, US-606 | |
| 3 | 34 | US-405 Produce a PDF | US-403 | P1 | all of wave 3 | |
| 3 | 35 | US-407 Compare two documents | US-403 | P1 | all of wave 3 | |
| 3 | 36 | US-408 Embed a generated image in a document | US-403, US-103 | P1 | all of wave 3 | |
| 3 | 37 | US-406 Update an existing document | US-403, US-404 | P1 | all of wave 3 except US-404 | |
| 3 | 38 | US-605 Generated-file failure states | US-604, US-409 | P1 | all of wave 3 except US-604, US-409 | |
| 3 | 39 | US-606 Use the generated-file surface without a mouse | US-601, US-604 | P0 | all of wave 3 except US-604 | |

⛔ **Verification gate** — US-404 is the story the artifact-validity success criterion rests on, and US-406 depends on it deliberately: an update that is not verified is a corrupt file with a familiar name, which is worse than a failed create. Do not start US-406 against an unverified create path.

## Wave 4 — hardening before switch-on

Nothing here has an edge to anything else here. All five can be taken concurrently, and none of them may be skipped before the feature is enabled for real users — the flags default off precisely so that this wave is not optional.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 4 | 40 | US-412 Feature-flag the whole feature with a documented rollback | US-402 | P0 | US-411, US-207, US-507, US-506 | |
| 4 | 41 | US-411 Gate file generation on a permission | US-402 | P0 | US-412, US-207, US-507, US-506 | |
| 4 | 42 | US-207 Reclaim generated blobs under a retention policy | US-202 | P2 | US-412, US-411, US-507, US-506 | |
| 4 | 43 | US-507 Bound what one user can spend | US-504 | P1 | US-412, US-411, US-207, US-506 | |
| 4 | 44 | US-506 See what file generation costs | US-503, US-504 | P2 | US-412, US-411, US-207, US-507 | |

Three of these five are blocked on an answer rather than on code: US-507 on open question 1 (cost ceiling), US-411 on open question 2 (which permission), US-207 on open question 3 (retention window). Getting those three answers during wave 3 is what keeps this wave one week rather than three.
