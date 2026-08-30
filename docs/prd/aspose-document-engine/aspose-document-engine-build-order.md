# Build order: Aspose Document Engine

A dependency-resolved execution sequence for the 31 stories in [`aspose-document-engine.md`](aspose-document-engine.md), for tracking progress against.

**The PRD is authoritative.** This file adds no requirements and changes no scope. It derives five waves from §7's phase table plus each story's `Depends on` field, and assigns one global `Order` counter across all of them. Where this file and the PRD disagree, the PRD wins and this file is wrong.

A wave here is a **phase**, not a dependency depth: several waves contain internal chains, so a story's `Depends on` may name another story in the same wave. What no story does is depend on one scheduled in a **later** wave — every row was checked against that rule by hand against the full dependency list. Within a wave, the `Parallel with` column names the stories that are neither an ancestor nor a descendant of it in the dependency graph — those can be taken concurrently by however many people are available.

**Progress: 0 / 31 done.**

**Critical path.** `US-001 → US-102 → US-103 → US-201 → US-204`, five stories deep before the graph forks into its three longest tails: `→ US-206` (the CSV parity question), `→ US-303` (which also needs `US-301`, so it is the join of the spreadsheet and presentation parity chains), and `→ US-503` (which also needs `US-404`, joining the extraction and export chains at the rollback rehearsal). All three tails land at depth six, which is the depth of the whole graph. EP-4's export chain (`US-401 → US-402 → US-404`) and EP-1's registration chain (`US-001/US-002 → US-102 → US-103`) meet only at `US-404`, and EP-6 (`US-103 → {US-601, US-602, US-603} → US-604`) hangs off `US-103` with no further downstream consumer, which is why it is the epic scheduled last with the least urgency. Useful concurrency peaks in Wave 3, where the spreadsheet chain (`US-201 → {US-203, US-205, US-204}`), the presentation chain (`US-301 → US-302`), and the export chain (`US-404 → {US-405, US-406}`) share no dependency edge with one another until `US-204`/`US-301` join at `US-303` and `US-204`/`US-404` join at `US-503`.

## Wave 1 — prove it's safe to build on

Five stories. `US-001`/`US-002` form the gate everything else sits behind; `US-003`, `US-004`, and `US-005` each depend on only one of the two and can run beside the other two.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | US-001 `[enabler]` Prove the three-product licence bootstrap | — | P0 | US-002, US-005 | Not started |
| 1 | 2 | US-002 `[enabler]` Keep the licence out of git and materialize it in CI only | — | P0 | US-001, US-005 | Not started |
| 1 | 3 | US-005 `[enabler]` Inventory the Windows App Service target's font faces | — | P1 | US-001, US-002, US-003, US-004 | Not started |
| 1 | 4 | US-003 `[enabler]` Aspose-dependent tests skip cleanly when unlicensed, and CI proves they still ran | US-002 | P0 | US-004, US-005 | Not started |
| 1 | 5 | US-004 `[enabler]` Measure the packaging and cold-start cost against the Windows target | US-001 | P1 | US-003, US-005 | Not started |

⛔ **`US-001` and `US-002` are the gate.** Nothing that constructs an Aspose object, in any epic, may start until the licence mechanism is proven working (`US-001`) and proven safe to keep out of git and out of fork pull requests (`US-002`). `US-003` in particular cannot be written against an unproven licensing mechanism — it needs `US-001`'s finding that an absent licence fails in a caught, logged way, not an unhandled exception.

## Wave 2 — the engine seam and the export template's foundation

Seven stories across two chains that meet only at `US-404` in Wave 3: EP-1's registration chain (`US-101 → {US-102 → US-103 → US-104}`) and EP-4's template chain (`US-401 → US-402`, plus `US-403`, which needs only `US-102`).

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 2 | 6 | US-101 `[enabler]` Bind and validate the per-extension engine map | — | P0 | US-401, US-402, US-403 | Not started |
| 2 | 7 | US-401 `[enabler]` The print-oriented export template and its composer | — | P0 | US-101, US-102, US-103, US-104, US-403 | Not started |
| 2 | 8 | US-102 `[enabler]` Apply the licence bootstrap at composition | US-001, US-002 | P0 | US-401 | Not started |
| 2 | 9 | US-402 `[enabler]` Refuse every external resource during HTML import | US-401 | P0 | US-101, US-102, US-103, US-104, US-403 | Not started |
| 2 | 10 | US-103 `[enabler]` Register exactly one extractor per extension from the resolved map | US-101, US-102 | P0 | US-401, US-402, US-403 | Not started |
| 2 | 11 | US-403 `[enabler]` `Export:Engines` map and licence-gated registration | US-102 | P0 | US-101, US-401, US-402, US-104 | Not started |
| 2 | 12 | US-104 Name the winning engine in the startup log | US-103 | P1 | US-401, US-402, US-403 | Not started |

⛔ **`US-102` is the join point of this wave.** It is the only story with an edge into both chains — EP-1's `US-103` needs it directly, and EP-4's `US-403` needs it directly. Whoever takes `US-102` should not also be assigned `US-401`/`US-402` concurrently; the two chains only need to agree on `US-102` finishing, not on being raced against each other.

## Wave 3 — extraction parity and export parity

Twelve stories across three chains — spreadsheet/CSV (EP-2), presentation (EP-3), and export (the rest of EP-4) — that run concurrently and join twice: `US-301` and `US-204` at `US-303`, and `US-204` and `US-404` at `US-503` (scheduled in Wave 4).

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 3 | 13 | US-201 `[enabler]` Aspose `.xlsx` extractor over `SheetAssembler` | US-103 | P0 | US-202, US-301, US-404, US-406 | Not started |
| 3 | 14 | US-202 `[enabler]` Aspose `.csv` extractor reusing the existing sniffing pass | US-103 | P0 | US-201, US-301, US-404, US-406 | Not started |
| 3 | 15 | US-301 `[enabler]` Aspose `.pptx` extractor preserving slide order and speaker notes | US-103 | P0 | US-201, US-202, US-404, US-406 | Not started |
| 3 | 16 | US-404 Aspose Word and PDF export renderers | US-401, US-402, US-403 | P0 | US-201, US-202, US-203, US-301, US-302 | Not started |
| 3 | 17 | US-406 Markdown never acquires an Aspose renderer | US-403 | P1 | all of wave 3 except US-404, US-405 | Not started |
| 3 | 18 | US-203 `[enabler]` Bound Aspose.Cells' memory footprint under load | US-201 | P0 | US-202, US-205, US-301, US-302, US-404, US-405, US-406 | Not started |
| 3 | 19 | US-205 A malformed or password-protected spreadsheet fails cleanly | US-201, US-202 | P1 | US-203, US-301, US-302, US-404, US-405, US-406 | Not started |
| 3 | 20 | US-204 `[enabler]` The segment-parity harness for `.xlsx` and `.csv` | US-201, US-202 | P0 | US-203, US-205, US-301, US-302, US-404, US-405, US-406 | Not started |
| 3 | 21 | US-302 A malformed or password-protected `.pptx` fails cleanly | US-301 | P1 | US-203, US-205, US-204, US-404, US-405, US-406 | Not started |
| 3 | 22 | US-405 Surface PDF font substitution instead of failing silently | US-404, US-005 | P1 | US-203, US-205, US-302, US-406 | Not started |
| 3 | 23 | US-206 Establish the CSV stray-quote tolerance, or the documented fallback | US-202, US-204 | P1 | US-303 | Not started |
| 3 | 24 | US-303 Extend the parity harness to `.pptx` | US-204, US-301 | P0 | US-206 | Not started |

⛔ **`US-204` is the busiest join point in the PRD.** It needs both `US-201` and `US-202`, and both `US-206` and `US-303` sit behind it — plus, in Wave 4, `US-503`. Whoever takes `US-204` should not also be racing `US-201`/`US-202` concurrently; the harness needs both extractors finished, not in flight.

⚠ **`US-206`'s outcome is not knowable in advance.** It may conclude "`.csv` parity achieved" or "`.csv` stays on `Legacy` permanently" — both are valid, planned outcomes per the PRD's own §8 assumption. Do not schedule any story past Wave 3 as though the answer is assumed; none currently depends on which way it resolves.

## Wave 4 — rollout, observability & rollback

Three stories, no edge between `US-501`/`US-502`, both mutually parallel with `US-503`'s own single dependency chain already closed in Wave 3.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 4 | 25 | US-501 Emit per-extraction and per-export engine metrics | US-103, US-403 | P1 | US-502, US-503 | Not started |
| 4 | 26 | US-502 Extend `ExportAvailabilityLogger` to report the engine | US-403 | P1 | US-501, US-503 | Not started |
| 4 | 27 | US-503 Rehearse the rollback | US-204, US-404 | P1 | US-501, US-502 | Not started |

Nothing in this wave blocks anything downstream — EP-6 depends only on `US-103` from Wave 2 — so it is scheduled fourth purely because a rollback and a set of metrics are only worth exercising against a feature that already does something, not because of a dependency edge into Wave 5.

## Wave 5 — optional extensions

Four stories: three independent enablers converging on one guard test.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 5 | 28 | US-601 `[enabler]` Aspose Word text extractor, shipped inert | US-103 | P2 | US-602, US-603 | Not started |
| 5 | 29 | US-602 `[enabler]` Aspose PDF text extraction via Aspose.Words, shipped inert | US-103 | P2 | US-601, US-603 | Not started |
| 5 | 30 | US-603 `[enabler]` `.xls`/`.xlsm` extraction via Aspose.Cells, shipped inert | US-103 | P2 | US-601, US-602 | Not started |
| 5 | 31 | US-604 A guard test that EP-6 ships inert | US-601, US-602, US-603 | P2 | — | Not started |

This wave could in principle start as soon as Wave 2's `US-103` closes — it shares no file with Waves 3 or 4 — and is scheduled last only because it is the PRD's own lowest-priority epic (P2), explicitly the first candidate to cut if the team's capacity does not stretch to it.
