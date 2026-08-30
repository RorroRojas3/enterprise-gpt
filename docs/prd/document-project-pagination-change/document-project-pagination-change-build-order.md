# Build order: Document & Project List Pagination

A dependency-resolved execution sequence for the 8 stories in [`document-project-pagination-change.md`](document-project-pagination-change.md), for tracking progress against.

**The PRD is authoritative.** This file adds no requirements and changes no scope. It derives three waves from §8's phase table plus each story's `Depends on` field, and assigns one global `Order` counter across all of them. Where this file and the PRD disagree, the PRD wins and this file is wrong.

A wave here is a **phase**, not a dependency depth: a story's `Depends on` may name another story in the same wave. What no story does is depend on one scheduled in a **later** wave. Within a wave, the `Parallel with` column names the stories that are neither an ancestor nor a descendant of it in the dependency graph.

Status here mirrors the `Status` field on each PRD story. Update both, or update the PRD and re-derive this. Nothing in this feature has started; every row below is **Not started**.

**Progress: 0 / 8 done.**

**Critical path.** Two chains tie for longest at three stories each: `US-101 → US-201 → US-202` and `US-101 → US-102 → US-202` — `US-201` and `US-102` can run in parallel once `US-101` closes, but `US-202` needs both closed before it can start. `US-301 → US-302` is a fully independent two-story chain with no edge into either of the above; it can start on day one alongside `US-101`. `US-203` is the one story with only a single dependency (`US-201`) and no further downstream story waiting on it. **Useful concurrency is 2 in wave 1** (`US-101` and `US-301` share no dependency edge) and **3 in wave 2** (`US-102`, `US-201`, and `US-302` share no dependency edge with one another).

## Wave 1 — the two independent starting points

Two stories, no edge between them. `US-101` opens the only chain the rest of EP-1 and all of EP-2 sit behind; `US-301` opens EP-3's fully independent chain.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | US-101 `[enabler]` Add server-side `name` filtering to `GET api/documents` | — | P0 | US-301 | Not started |
| 1 | 2 | US-301 `[enabler]` Rebuild `ProjectListStore` on `withOffsetPagination` and Load more | — | P0 | US-101 | Not started |

⛔ **`US-101` gates every story in EP-2.** The client change EP-2 makes is incorrect without the server-side `name` parameter already in place — a paginated `DocumentLibraryStore` that removed `withClientQuery` before `GET api/documents` accepted `name` would leave Documents' search working nowhere at all, client or server. Do not start `US-201` before `US-101` closes. `US-301` has no such gate and can start the same day.

## Wave 2 — the count, the Documents rewrite, and the Projects a11y pass

Three stories, three independent chains, each gated on exactly one wave-1 story. `US-102` and `US-201` both need `US-101`; `US-302` needs `US-301`. None of the three needs either of the other two.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 2 | 3 | US-102 Grouped counts state how many of a conversation's documents match the active filters | US-101 | P1 | US-201, US-302 | Not started |
| 2 | 4 | US-201 `[enabler]` Rebuild `DocumentLibraryStore` on `withOffsetPagination` and Load more | US-101 | P0 | US-102, US-302 | Not started |
| 2 | 5 | US-302 Load more control matches the Conversations library's keyboard operability and focus restoration, adapted to a card grid | US-301 | P1 | US-102, US-201 | Not started |

⚠ **`US-302` closes EP-3 entirely.** Once it lands, Projects' Load more work is done and needs nothing from EP-1 or EP-2 — it can ship independently of how quickly wave 3 below closes.

## Wave 3 — Documents polish

Two stories, both needing `US-201`; only `US-202` also needs `US-102`. Neither has an edge into the other.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 3 | 6 | US-202 Grouped view groups only what is loaded, worded honestly | US-201, US-102 | P1 | US-203 | Not started |
| 3 | 7 | US-203 Load more control matches the Conversations library's keyboard operability and focus restoration | US-201 | P1 | US-202 | Not started |

✅ **This is the last wave.** Once both stories close, all 8 stories in the PRD are done and both screens carry the same paging model Conversations already ships.
