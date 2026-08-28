# Build order: File Agent

A dependency-resolved execution sequence for the 44 stories in [`file-agent.md`](file-agent.md), for tracking progress against.

**The PRD is authoritative.** This file adds no requirements and changes no scope. It derives four waves from §8's phase table plus each story's `Depends on` field, and assigns one global `Order` counter across all of them. Where this file and the PRD disagree, the PRD wins and this file is wrong.

A wave here is a **phase**, not a dependency depth: every wave below contains internal chains, so a story's `Depends on` may name another story in the same wave. What no story does is depend on one scheduled in a **later** wave — every row was checked against that rule by hand against the full dependency list. Within a wave, the `Parallel with` column names the stories that are neither an ancestor nor a descendant of it in the dependency graph — those can be taken concurrently by however many people are available. Dense waves use the shorthand *all of wave N except …* rather than enumerating every id.

Status here mirrors the `Status` field on each PRD story. Update both, or update the PRD and re-derive this.

**Progress: 22 / 44 done.**

**Critical path.** `US-001` (Code Interpreter proven on the Responses route) feeds three chains: `US-001 → US-002 → US-003` (the conversion matrix, needed by `US-406`/`US-407`), `US-001 → US-201 → US-202` (the sandbox execution mechanism, also needed by `US-204`), and `US-001 → US-301` (the agent's pinned model). `US-302` needs both `US-301` and `US-202` — the first join — and `US-401` needs both `US-303` and `US-103` (EP-1's storage chain) — the second, and the true bottleneck of the whole backend. From there, `US-402` is the gate every other EP-4 verb (`US-403`-`US-407`) and EP-5's totals test (`US-503`) sit behind. Nine deep along its longest branch: `US-001 → US-201 → US-202 → US-302 → US-303 → US-401 → US-402 → US-406 → US-407` — the conversion-refusal story is the single longest tail because it alone needs both the create/verify chain and US-003's matrix. EP-1's schema/storage chain (`US-101`/`US-102 → US-103 → US-104`/`US-106`) and EP-6's chip lane (`US-105 → US-601 → US-602`/`US-603`) run clear of that whole chain from day one, testable against a hand-inserted `Generated` row with no agent in existence. **Useful concurrency reaches at least 7** in wave 1 — for example `US-104`, `US-106`, `US-204`, `US-305`, `US-306`, `US-307` and `US-603` share no dependency edge with one another — against a build that opens wave 0 with a strict three-story chain.

## Wave 0 — the gate

Three stories, a strict chain, and nothing below is estimated in detail until all three close.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | 1 | US-001 `[enabler]` Prove Code Interpreter runs over the Responses-route Azure OpenAI client | — | P0 | — | ✅ Done (2026-08-27) |
| 0 | 2 | US-002 `[enabler]` Inventory the sandbox's actual Python libraries | US-001 | P0 | — | ✅ Done (2026-08-27) |
| 0 | 3 | US-003 `[enabler]` Establish and publish the supported conversion matrix | US-002 | P0 | — | ✅ Done (2026-08-27) |

⛔ **This is a gate, not a phase.** `US-001` pins whether `HostedCodeInterpreterTool` actually works on this tenant's existing Azure OpenAI registration; `US-002` and `US-003` both read that result directly. Starting `US-201` or `US-301` before `US-001` closes risks building the whole sandbox-execution and agent-composition chains around an assumption the tenant does not actually support.

✅ **The gate closed on 2026-08-27**, and it moved three things the rest of this PRD was written against. `DataContent` on `HostedCodeInterpreterTool.Inputs` is silently dropped, so inputs go through the Files API as `HostedFileContent` (FR-10/FR-11 and US-202 corrected). `markdown` is absent from the image and `fpdf` is 2.8.3 rather than 1.x (§6's tooling list corrected). `pptx` → `pdf` was demoted from structural to refused, and the confirmed matrix now lives in `Enterprise.Gpt.Service/Agents/Documents/conversion-matrix.json`.

## Wave 1 — storage, catalog, agent composition, and the chip beside them

The backend splits into three chains that only meet once, at `US-302`: EP-1's storage/download chain (needs nothing from Azure and can start immediately), EP-2's sandbox-execution enablers (needs only `US-001`), and EP-3's agent-composition chain (needs `US-001` directly and `US-202` for its second story). EP-6's chip lane runs beside all three, needing only `US-105`.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 4 | US-101 `[enabler]` Add the `Generated` discriminator and migrate existing rows | — | P0 | US-102, US-105, US-201, US-202, US-204, US-301, US-302, US-303, US-304, US-305, US-306, US-307, US-601, US-603 | ✅ Done (2026-08-28) |
| 1 | 5 | US-102 `[enabler]` Provision the `generated-documents` blob container | — | P0 | US-101, US-105, US-201, US-202, US-204, US-301, US-302, US-303, US-304, US-305, US-306, US-307, US-601, US-603 | ✅ Done (2026-08-28) |
| 1 | 6 | US-105 `[enabler]` Carry the generated-file reference on the transcript message | — | P0 | US-101, US-102, US-103, US-104, US-106, US-201, US-202, US-204, US-301, US-302, US-303, US-304, US-305, US-306, US-307 | ✅ Done (2026-08-28) |
| 1 | 7 | US-201 `[enabler]` Attach `HostedCodeInterpreterTool` only on the Responses-route client | US-001 | P0 | US-101, US-102, US-103, US-104, US-105, US-106, US-301, US-601, US-602, US-603 | ✅ Done (2026-08-28) |
| 1 | 8 | US-301 `[enabler]` Configure the File Agent's pinned model and validate it at startup | US-001 | P0 | US-101, US-102, US-103, US-104, US-105, US-106, US-201, US-202, US-204, US-601, US-602, US-603 | ✅ Done (2026-08-28) |
| 1 | 9 | US-103 `[enabler]` Persist a generated file without ingesting it | US-101, US-102 | P0 | US-105, US-201, US-202, US-204, US-301, US-302, US-303, US-304, US-305, US-306, US-307, US-601, US-603 | ✅ Done (2026-08-28) |
| 1 | 10 | US-202 `[enabler]` Upload turn inputs into the sandbox and read artifacts back | US-201 | P0 | US-101, US-102, US-103, US-104, US-105, US-106, US-301, US-601, US-602, US-603 | ✅ Done (2026-08-28) |
| 1 | 11 | US-601 Show a generated document on the assistant message | US-105 | P0 | US-101, US-102, US-103, US-104, US-106, US-201, US-202, US-204, US-301, US-302, US-303, US-304, US-305, US-306, US-307 | ✅ Done (2026-08-28) |
| 1 | 12 | US-104 Download a generated document in its own format | US-103 | P1 | US-105, US-106, US-201, US-202, US-204, US-301, US-302, US-303, US-304, US-305, US-306, US-307, US-601, US-603 | ✅ Done (2026-08-28) |
| 1 | 13 | US-106 A generated document is never retrieved or cited | US-103 | P0 | US-104, US-105, US-201, US-202, US-204, US-301, US-302, US-303, US-304, US-305, US-306, US-307, US-601, US-602, US-603 | ✅ Done (2026-08-28) |
| 1 | 14 | US-302 `[enabler]` Compose and name the File Agent behind a Service abstraction | US-301, US-202 | P0 | US-101, US-102, US-103, US-104, US-105, US-106, US-204, US-601, US-602, US-603 | ✅ Done (2026-08-28) |
| 1 | 15 | US-204 `[enabler]` Bound a sandbox run's time and artifact count | US-202 | P0 | US-101, US-102, US-103, US-104, US-105, US-106, US-301, US-302, US-303, US-304, US-305, US-306, US-307, US-601, US-602, US-603 | ✅ Done (2026-08-28) |
| 1 | 16 | US-602 Download a generated document on click | US-601, US-104 | P0 | US-106, US-201, US-202, US-204, US-301, US-302, US-303, US-304, US-305, US-306, US-307, US-603 | ✅ Done (2026-08-28) |
| 1 | 17 | US-603 A generated document survives a reload | US-601, US-105 | P1 | US-101, US-102, US-103, US-104, US-106, US-201, US-202, US-204, US-301, US-302, US-303, US-304, US-305, US-306, US-307, US-602 | ✅ Done (2026-08-28) |
| 1 | 18 | US-303 `[enabler]` Attach the agent as one tracked tool via `WithTracking` | US-302 | P0 | US-101, US-102, US-103, US-104, US-105, US-106, US-204, US-304, US-305, US-306, US-307, US-601, US-602, US-603 | ✅ Done (2026-08-28) |
| 1 | 19 | US-304 `[enabler]` Load format-targeted Agent Skills, filtered before advertisement | US-302 | P0 | US-101, US-102, US-103, US-104, US-105, US-106, US-204, US-303, US-601, US-602, US-603 | ✅ Done (2026-08-28) |
| 1 | 20 | US-305 `[enabler]` Auto-approve read-only skill tools | US-304 | P0 | US-101, US-102, US-103, US-104, US-105, US-106, US-204, US-303, US-306, US-307, US-601, US-602, US-603 | ✅ Done (2026-08-28) |
| 1 | 21 | US-306 `[enabler]` Exclude host-side script execution from the skill runner | US-304 | P0 | US-101, US-102, US-103, US-104, US-105, US-106, US-204, US-303, US-305, US-307, US-601, US-602, US-603 | ✅ Done (2026-08-28) |
| 1 | 22 | US-307 Ship skill content as deployed files | US-304 | P1 | US-101, US-102, US-103, US-104, US-105, US-106, US-204, US-303, US-305, US-306, US-601, US-602, US-603 | ✅ Done (2026-08-28) |

✅ **Wave 1 closed on 2026-08-28**, and it moved three things the PRD had settled differently. The agent runs on a keyed client of its own **without** tool tracking, so `US-303`'s `trackUsage` is `true` rather than `false` — a nested tracker opens its own writer-less root scope and would swallow the run's progress. `US-305` disables approval on the two read-only skill tools through `AgentSkillsProviderOptions` rather than an auto-approval rule. And `US-306`'s “zero `UseFileScriptRunner` call sites” is unreachable — the package refuses to build a file-skill provider without a runner — so the guard is one call site passing a runner that throws.

⛔ **`US-302` is the join point.** It is the only story in this wave with edges into both the EP-2 chain (`US-202`) and the EP-3 chain (`US-301`); everything after it in EP-3 (`US-303`-`US-307`) is blocked on it. Whoever takes `US-302` should not also be assigned `US-202` or `US-301` concurrently — the join needs both finished, not raced.

## Wave 2 — the four verbs, usage, and the rest of the frontend

Twenty stories, opening wide (four — `US-203`, `US-205`, `US-505`, `US-607` — with no wave-2-internal dependency at all) and converging hard at `US-401 → US-402`, which every other EP-4 verb and EP-5's totals test sit behind.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 2 | 23 | US-203 A run that produces no file fails as a named error | US-202 | P1 | all of wave 2 | |
| 2 | 24 | US-205 Cancel a File Agent run with the turn | US-202 | P1 | all of wave 2 | |
| 2 | 25 | US-504 Measure Code Interpreter session cost | US-202 | P1 | all of wave 2 except US-506 | |
| 2 | 26 | US-505 Emit File Agent spans and metrics | US-302 | P1 | all of wave 2 | |
| 2 | 27 | US-408 Stand down or fail cleanly | US-303 | P0 | all of wave 2 except US-605 | |
| 2 | 28 | US-501 `[enabler]` Fill the `ModelId` and `DeploymentName` nulls for agent rows | US-303 | P0 | all of wave 2 except US-502, US-503 | |
| 2 | 29 | US-604 Watch the File Agent work | US-303 | P1 | all of wave 2 except US-605, US-606 | |
| 2 | 30 | US-607 `[enabler]` Re-baseline the initial bundle | US-601 | P1 | all of wave 2 | |
| 2 | 31 | US-401 Create a document from a prompt | US-303, US-103 | P0 | US-203, US-205, US-408, US-501, US-502, US-504, US-505, US-506, US-604, US-605, US-606, US-607 | |
| 2 | 32 | US-502 `[enabler]` Add the deferred `(ModelId, DateCreated)` index | US-501 | P1 | all of wave 2 except US-501 | |
| 2 | 33 | US-506 Bound what one user or conversation can spend | US-504 | P1 | all of wave 2 except US-504 | |
| 2 | 34 | US-605 Generated-file failure states | US-604, US-408 | P1 | all of wave 2 except US-604, US-408 | |
| 2 | 35 | US-606 Use the generated-file surface without a mouse | US-604 | P0 | all of wave 2 except US-604 | |
| 2 | 36 | US-402 Verify the artifact before returning it | US-401 | P0 | all of wave 2 except US-401, US-403, US-404, US-405, US-406, US-407 | |
| 2 | 37 | US-503 Nested token attribution is correct end to end | US-501, US-401 | P0 | all of wave 2 except US-501, US-401 | |
| 2 | 38 | US-403 Produce a PDF within the sandbox's fidelity limits | US-402 | P1 | all of wave 2 except US-401, US-402 | |
| 2 | 39 | US-404 Edit an existing conversation document | US-402 | P1 | all of wave 2 except US-401, US-402 | |
| 2 | 40 | US-405 Compare two documents | US-402 | P1 | all of wave 2 except US-401, US-402 | |
| 2 | 41 | US-406 Convert a document between supported formats | US-402, US-003 | P1 | all of wave 2 except US-401, US-402, US-407 | |
| 2 | 42 | US-407 Refuse an unsupported conversion or an unauthorized/ambiguous source cleanly | US-406 | P0 | all of wave 2 except US-401, US-402, US-406 | |

⛔ **`US-401` and `US-402` are the two narrowest points in the whole PRD.** `US-401` needs both wave-1 chains (`US-303` and `US-103`) closed, and every one of `US-403`-`US-407` and `US-503` is blocked on `US-402` specifically, not merely on `US-401` — an edit, a compare, or a convert against an unverified create path would be building on a foundation nobody has proven solid, which is exactly why the PRD orders EP-4 create → verify → the remaining verbs. Do not start `US-404`-`US-407` before `US-402` closes.

⚠ **`US-401`'s `Parallel with` list is deliberately not "all of wave 2."** Everything downstream of it — `US-402` and, through `US-402`, `US-403`-`US-407` — is excluded, because pairing someone on `US-401` with someone already past it on `US-402` is not useful parallelism; `US-503` is excluded too, since it depends on `US-401` directly. Every other wave-2 story shares no dependency edge with `US-401` at all — including `US-501`/`US-504` and their own descendants `US-502`/`US-506`, which `US-401` never depends on — so they remain listed.

## Wave 3 — hardening before switch-on

Two stories, no edge between them, and neither may be skipped before the feature is enabled for real users — the flag defaults off precisely so that this wave is not optional.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 3 | 43 | US-701 Gate file generation on a new permission | US-303 | P0 | US-702 | |
| 3 | 44 | US-702 Feature-flag the whole feature with a documented rollback | US-303 | P0 | US-701 | |

Both are blocked on nothing but a working tracked tool (`US-303`), so both could in principle start as soon as wave 1 closes — they are scheduled last because a flag and a permission are only worth exercising against the feature they gate, and testing a rollback before the feature exists to roll back would prove nothing.
