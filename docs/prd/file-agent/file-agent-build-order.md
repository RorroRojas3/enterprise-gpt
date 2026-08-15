# Build order: File Agent

A dependency-resolved execution sequence for the 32 stories in [`file-agent.md`](file-agent.md), for tracking progress against.

**The PRD is authoritative.** This file adds no requirements and changes no scope. It derives four waves from §8's phase table plus each story's `Depends on` field, and assigns one global `Order` counter across all of them. Where this file and the PRD disagree, the PRD wins and this file is wrong.

A wave here is a **phase**, not a dependency depth: waves 1 and 2 each contain internal chains, so a story's `Depends on` may name another story in the same wave. What no story does is depend on one scheduled in a **later** wave. Within a wave, the `Parallel with` column names the stories that neither depend on it nor are depended on by it — those can be taken concurrently by however many people are available. Wave 2's column uses the shorthand *all of wave 2 except …*, since thirteen stories carrying four edges between them would otherwise enumerate twelve ids per row.

Status here mirrors the `Status` field on each PRD story. Update both, or update the PRD and re-derive this.

**Progress: 0 / 32 done.**

**Critical path.** `US-001` (Code Interpreter spike) → `US-101` (`AIProjectClient` registration) → `US-102` (per-request container) → `US-103` (files in and out) → `US-203` (create a document) → `US-204` (deterministic verification) → `US-404` (progress rendering), with `US-201` → `US-202` joining ahead of `US-203`. Seven deep counting the join, and every link of it is in EP-1 or the epic that consumes EP-1 — which is why §8 spends its schedule pressure there. EP-4's chip lane (`US-401` → `US-402`, `US-403`) is off the critical path entirely: it depends only on `image-tool`'s US-104 and US-105, so it can be taken from day one by whoever is not on the backend chain. **Maximum useful concurrency is 10**, at the start of wave 2, where thirteen stories carry only four edges between them; wave 1 opens with just two ready stories — `US-101`, which the whole backend chain sits behind, and `US-401`, whose only dependency is external — and never widens beyond six.

## Wave 0 — the gate

Two stories, no dependencies, and nothing else may be estimated in detail until both close. Code Interpreter is not in every region and the .NET Foundry Agents SDK is in preview.

⛔ **This wave does not start until `docs/prd/image-tool/image-tool.md`'s US-103 (*Persist a generated file without ingesting it*) and US-105 (*Carry the generated-file reference on the assistant message*) have shipped**, with US-101, US-102 and US-104 beneath them and US-202/US-203 for anything that needs a picture. Those five are the contract this PRD writes and reads through; starting here first means building an agent whose output has nowhere to go and a chip with nothing to render from.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | 1 | US-001 `[enabler]` Prove Code Interpreter runs from .NET with a per-request container | — | P0 | US-002 | |
| 0 | 2 | US-002 `[enabler]` Confirm the model deployments this feature needs | — | P0 | US-001 | |

## Wave 1 — the sandbox, the agent on top of it, and the chip beside them

The backend is one chain: EP-1 opens the sandbox, EP-2's first three stories compose the agent over it, and EP-3's two enablers follow the agent's first tracked turn. `US-101` is that chain's root. Running clear of it is EP-4's chip lane — `US-401` … `US-403` need only `image-tool`'s US-104 and US-105, are testable against a hand-inserted `Generated` row with no agent in existence, and are the only place a second person is useful this early.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 3 | US-101 `[enabler]` Register `AIProjectClient` with options, flag and startup validation | US-001 | P0 | US-401, US-402, US-403 | |
| 1 | 4 | US-401 Show a generated document on the assistant message | `image-tool.md` US-105 | P0 | US-101, US-102, US-103, US-104, US-105, US-201, US-202, US-203, US-301, US-302 | |
| 1 | 5 | US-102 `[enabler]` Run Python in a per-request isolated container | US-101 | P0 | US-201, US-202, US-301, US-302, US-401, US-402, US-403 | |
| 1 | 6 | US-201 `[enabler]` Compose and name the File Agent behind an abstraction | US-002, US-101 | P0 | US-102, US-103, US-104, US-105, US-401, US-402, US-403 | |
| 1 | 7 | US-103 `[enabler]` Get files into the container and artifacts back out | US-102 | P0 | US-105, US-201, US-202, US-301, US-302, US-401, US-402, US-403 | |
| 1 | 8 | US-202 `[enabler]` Attach the agent as one tracked tool | US-201 | P0 | US-102, US-103, US-104, US-105, US-401, US-402, US-403 | |
| 1 | 9 | US-203 Create a document from a prompt | US-202, US-103, plus `image-tool.md` US-103 | P0 | US-104, US-105, US-301, US-302, US-401, US-402, US-403 | |
| 1 | 10 | US-402 Download a generated document on click | US-401, plus `image-tool.md` US-104 | P0 | US-101, US-102, US-103, US-104, US-105, US-201, US-202, US-203, US-301, US-302, US-403 | |
| 1 | 11 | US-403 A generated document survives a reload | US-401, plus `image-tool.md` US-105 | P1 | US-101, US-102, US-103, US-104, US-105, US-201, US-202, US-203, US-301, US-302, US-402 | |
| 1 | 12 | US-104 A run that produces no file fails as a named error | US-103 | P1 | US-105, US-201, US-202, US-203, US-301, US-302, US-401, US-402, US-403 | |
| 1 | 13 | US-105 Bound a Code Interpreter run's cost and duration | US-102 | P1 | US-103, US-104, US-201, US-202, US-203, US-301, US-302, US-401, US-402, US-403 | |
| 1 | 14 | US-301 `[enabler]` Fill the `ModelId` and `DeploymentName` nulls for agent rows | US-202 | P0 | US-102, US-103, US-104, US-105, US-203, US-401, US-402, US-403 | |
| 1 | 15 | US-302 `[enabler]` Add the deferred `(ModelId, DateCreated)` index | US-301 | P1 | US-102, US-103, US-104, US-105, US-203, US-401, US-402, US-403 | |

## Wave 2 — needs a working agent

The widest wave and the least constrained: thirteen stories carrying four edges between them. EP-4's progress rendering is deliberately here rather than in wave 1 — activity frames only become real once the agent emits them, and a fixture-only implementation would be re-tested against the real stream anyway.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 2 | 16 | US-204 Verify the artifact before returning it | US-203 | P0 | all of wave 2 except US-206 | |
| 2 | 17 | US-209 Stand down or fail cleanly | US-202 | P0 | all of wave 2 except US-405 | |
| 2 | 18 | US-210 Stop a File Agent turn mid-flight | US-203 | P1 | all of wave 2 | |
| 2 | 19 | US-305 Emit File Agent spans and metrics | US-201 | P1 | all of wave 2 | |
| 2 | 20 | US-304 Measure Code Interpreter session cost | US-102 | P1 | all of wave 2 | |
| 2 | 21 | US-303 Nested token attribution is correct end to end | US-301, US-203 | P0 | all of wave 2 | |
| 2 | 22 | US-404 Watch the File Agent work | US-202 | P1 | all of wave 2 except US-405, US-406 | |
| 2 | 23 | US-205 Produce a PDF | US-203 | P1 | all of wave 2 | |
| 2 | 24 | US-207 Compare two documents | US-203 | P1 | all of wave 2 | |
| 2 | 25 | US-208 Embed a generated image in a document | US-203, plus `image-tool.md` US-202 and US-203 | P1 | all of wave 2 | |
| 2 | 26 | US-206 Update an existing document | US-203, US-204 | P1 | all of wave 2 except US-204 | |
| 2 | 27 | US-405 Generated-file failure states | US-404, US-209 | P1 | all of wave 2 except US-404, US-209 | |
| 2 | 28 | US-406 Use the generated-file surface without a mouse | US-401, US-404 | P0 | all of wave 2 except US-404 | |

⛔ **Verification gate** — US-204 is the story the artifact-validity success criterion rests on, and US-206 depends on it deliberately: an update that is not verified is a corrupt file with a familiar name, which is worse than a failed create. Do not start US-206 against an unverified create path.

## Wave 3 — hardening before switch-on

Nothing here has an edge to anything else here. All four can be taken concurrently, and none of them may be skipped before the feature is enabled for real users — the flags default off precisely so that this wave is not optional.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 3 | 29 | US-212 Feature-flag the whole feature with a documented rollback | US-202 | P0 | US-211, US-307, US-306 | |
| 3 | 30 | US-211 Gate file generation on a permission | US-202 | P0 | US-212, US-307, US-306 | |
| 3 | 31 | US-307 Bound what one user can spend | US-304 | P1 | US-212, US-211, US-306 | |
| 3 | 32 | US-306 See what file generation costs | US-303, US-304 | P2 | US-212, US-211, US-307 | |

Two of these four are blocked on an answer rather than on code: US-307 on open question 1 (cost ceiling), and US-211 on open question 2 (which permission) — a question `docs/prd/image-tool/image-tool.md` owns and answers in its US-504 before this PRD starts. Getting the cost-ceiling answer during wave 2 is what keeps this wave one week rather than three.
