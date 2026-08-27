# Build order: Sheet Ingestion

A dependency-resolved execution sequence for the 20 stories in [`sheet-ingestion.md`](sheet-ingestion.md), for tracking progress against.

**The PRD is authoritative.** This file adds no requirements and changes no scope. It derives four waves from §8's phase table plus each story's `Depends on` field, and assigns one global `Order` counter across all of them. Where this file and the PRD disagree, the PRD wins and this file is wrong.

A wave here is a **phase**, not a dependency depth: several waves contain internal chains, so a story's `Depends on` may name another story in the same wave. What no story does is depend on one scheduled in a **later** wave — every row was checked against that rule by hand against the full dependency list. Within a wave, the `Parallel with` column names the stories that are neither an ancestor nor a descendant of it in the dependency graph — those can be taken concurrently by however many people are available.

**Progress: 4 / 20 done.**

**Critical path.** `US-101 → US-201 → US-202 → US-304 → US-401 → US-402 → US-403 → US-405 → US-504`, nine stories deep. EP-1's `US-102 → US-103` lane and EP-3's `US-301 → US-302 → US-303` lane both join the critical path only at `US-304` (which needs `US-201`, `US-202`, **and** `US-302`), so neither is itself the bottleneck — `US-304` is, since it is the single point every one of EP-4's stories sits behind. `US-404` (the filter/list verb) and EP-5's `US-501`/`US-502`/`US-503` all run beside the critical path's tail rather than on it. Useful concurrency peaks in wave 2, where `US-301`, `US-201`, `US-303`, `US-203`, `US-204`, and `US-304`'s siblings share no dependency edge with several other wave-2 stories at once — see each row's own `Parallel with` column rather than a single headline number, since the wave's internal chain means no two people should take adjacent links of it concurrently.

## Wave 1 — accept a spreadsheet at all

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | US-101 `[enabler]` Register the `.xlsx` extractor | — | P0 | US-102 | ✅ Done (2026-08-26) |
| 1 | 2 | US-102 `[enabler]` Register the `.csv` extractor | — | P1 | US-101 | ✅ Done (2026-08-26) |
| 1 | 3 | US-103 `[enabler]` A malformed or protected spreadsheet fails cleanly | US-101, US-102 | P1 | — | ✅ Done (2026-08-26) |

⛔ **`US-103` needs both extractors.** It exercises corrupt-input handling for both formats in one story, so it cannot start until `US-101` and `US-102` both close — the only genuine block in this wave.

✅ **`US-501` (order 17, listed under wave 4 below) shipped alongside this wave, not after it.** A code review found reproducible unbounded-allocation vectors in the Wave 1 extractors themselves — a workbook is a deflate archive, so nothing upstream of extraction bounds what it inflates into — and the fix (`Sheets:MaxCharactersPerUpload`) landed directly against `SpreadsheetTextExtractor`/`CsvTextExtractor`, ahead of the row-window shape (`US-201`/`US-202`) this row's own `Depends on` column names. See wave 4's own note for the detail. This file's `Order`/`Depends on` columns are left as originally derived rather than rewritten around what actually happened — this note, not the table, is where that gets recorded.

## Wave 2 — searchable and storable

Eight stories across two chains that meet at `US-304`: EP-2's chunking chain (`US-201 → US-202 → {US-203, US-204}`) and EP-3's schema chain (`US-301 → US-302 → US-303`). `US-304` itself needs both chains — `US-201`/`US-202` for the extractors' structured output, `US-302` for the tables to write into.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 2 | 4 | US-301 `[enabler]` Add the sheet and column tables | — | P0 | all of wave 2 except US-302, US-303, US-304 | |
| 2 | 5 | US-201 `[enabler]` Header-aware row windows for `.xlsx` | US-101 | P0 | all of wave 2 except US-202, US-203, US-204, US-304 | |
| 2 | 6 | US-302 `[enabler]` Add the row table with its native `json` cells column | US-301 | P0 | all of wave 2 except US-301, US-303, US-304 | |
| 2 | 7 | US-202 `[enabler]` Header-aware row windows for `.csv` | US-102, US-201 | P0 | all of wave 2 except US-201, US-203, US-204, US-304 | |
| 2 | 8 | US-303 `[enabler]` Adapt the SQLite fixture for the `json` column | US-302 | P1 | all of wave 2 except US-301, US-302 | |
| 2 | 9 | US-203 `[enabler]` Per-sheet schema card | US-201, US-202 | P1 | all of wave 2 except US-201, US-202 | |
| 2 | 10 | US-204 `[enabler]` Sheet-aware citation | US-201, US-202 | P1 | all of wave 2 except US-201, US-202 | |
| 2 | 11 | US-304 `[enabler]` Persist sheets, columns, and rows inside the existing ingestion save | US-201, US-202, US-302 | P0 | US-303, US-203, US-204 | |

⛔ **`US-304` is the join point and the whole feature's bottleneck.** It is the only story in this wave with edges into both chains, and every one of EP-4's stories (`US-401`–`US-405`) sits behind it. Whoever takes `US-304` should not also be assigned `US-201`, `US-202`, or `US-302` concurrently — the join needs all three finished, not raced.

## Wave 3 — the deterministic lookup

Five stories, almost a strict chain: `US-401 → US-402 → {US-403, US-404} `, `US-403 → US-405`.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 3 | 12 | US-401 `[enabler]` Resolve a sheet and column by name, refusing what is unknown | US-304 | P0 | — | |
| 3 | 13 | US-402 `[enabler]` Build the parameterized, no-raw-SQL query | US-401 | P0 | — | |
| 3 | 14 | US-403 The model can aggregate a column, optionally grouped by another | US-402 | P0 | US-404 | |
| 3 | 15 | US-404 The model can filter and list matching rows | US-402 | P1 | US-403, US-405 | |
| 3 | 16 | US-405 `[enabler]` Attach `sheet_query` beside `document_search` | US-403 | P0 | US-404 | |

⛔ **`US-401` and `US-402` are a strict, un-parallelizable chain.** Both are single-owner, security-relevant stories (name resolution and SQL construction); `US-402`'s acceptance criteria depend on `US-401`'s refusal behavior already existing to build against. Do not split either across two people.

⚠ **`US-405` (P0) depends only on `US-403`, not on `US-404` (P1).** This is deliberate: `sheet_query` can attach to the turn and ship its headline aggregate capability without waiting on the filter/list verb, which can land as a follow-up enhancement to an already-attached tool.

## Wave 4 — governance and switch-on

Four stories with no dependency edge between them — all mutually parallel — gated only on the pieces of wave 2 and wave 3 each one measures or flags.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 4 | 17 | US-501 `[enabler]` Cap rows and columns accepted per sheet | US-201, US-202 | P0 | US-502, US-503, US-504 | ✅ Done (2026-08-26) — pulled into wave 1 |
| 4 | 18 | US-502 Re-tune retrieval defaults for row-window corpora | US-201, US-202 | P1 | US-501, US-503, US-504 | |
| 4 | 19 | US-503 `[enabler]` Emit sheet ingestion and `sheet_query` telemetry | US-304, US-405 | P1 | US-501, US-502, US-504 | |
| 4 | 20 | US-504 `[enabler]` Feature-flag `sheet_query` with a documented rollback | US-405 | P0 | US-501, US-502, US-503 | |

✅ **`US-501` did not wait for wave 4 — it was pulled into wave 1, at the user's explicit decision, and closed 2026-08-26.** A code review during Wave 1 measured reproducible unbounded-allocation vectors in the new extractors (up to 1.4 GB of peak managed heap from a file a few megabytes in size — a workbook is a deflate archive, so nothing upstream of extraction bounds what it inflates into) and treated that as a decompression-bomb exposure too real to leave open until wave 4. Wave 4 therefore has one fewer story left to schedule, not a story silently dropped — see wave 1's own note above for where it actually landed.

⚠ **`US-504` is the true switch-on gate for `sheet_query`.** Until it lands, `SheetQuery:Enabled` has no wiring to default `false` deliberately — treat wave 3's stories as code-complete-but-not-yet-safe-to-enable until this closes.
