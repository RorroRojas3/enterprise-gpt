# Build order: Enterprise GPT frontend rebuild

A dependency-resolved execution sequence for the 94 stories in [`enterprise-ui-rebuild.md`](enterprise-ui-rebuild.md), for tracking progress against.

**The PRD is authoritative.** This file adds no requirements and changes no scope. It derives one linear order from §8's phase table plus each story's `Depends on` field, because §7 is grouped by surface and its numbering is not a valid build order — four stories depend on a later-numbered one (US-105→US-109, US-106→US-109, US-307→US-901, US-401→US-405). Where this file and the PRD disagree, the PRD wins and this file is wrong.

Status here mirrors the `Status` field on each PRD story. Update both, or update the PRD and re-derive this.

**Progress: 14 / 94 done.** Current position: P2, `US-302`.

Within a phase, stories on the same table row have no ordering constraint between them and can be taken in any order or in parallel. `→` in the notes column means "must follow".

---

## P1 — Foundation · ✅ complete

| Order | Story | Depends on | Pri | Status |
| --- | --- | --- | --- | --- |
| 1 | US-101 `[enabler]` Scaffold the zoneless Angular 21 workspace | — | P0 | ✅ 2026-08-09 |
| 2 | US-102 `[enabler]` Load configuration at runtime before bootstrap | US-101 | P0 | ✅ 2026-08-09 |
| 3 | US-103 `[enabler]` Type and normalize every API error | US-101 | P0 | ✅ 2026-08-09 |
| 4 | US-104 `[enabler]` Build the reusable signal-store features | US-101 | P0 | ✅ 2026-08-09 |
| 5 | US-107 `[enabler]` Vendor the Andes streaming contract with a drift check | US-101 | P0 | ✅ 2026-08-10 |
| 6 | US-108 `[enabler]` Enforce lint rules and bundle budgets in the build | US-101 | P0 | ✅ 2026-08-10 |
| 7 | US-109 `[enabler]` Ship the brand assets, type scale, and icon set | US-101 | P0 | ✅ 2026-08-10 |
| 8 | US-105 Switch between light and dark without a flash | US-101, US-109 | P0 | ✅ 2026-08-10 |
| 9 | US-106 `[enabler]` Build the shared UI kit | US-101, US-105, US-109 | P0 | ✅ 2026-08-10 |

US-109 sits ahead of US-105 and US-106 despite its number: it ships the assets both of them consume.

## P2 — Signed-in shell · in progress

| Order | Story | Depends on | Pri | Status |
| --- | --- | --- | --- | --- |
| 10 | US-201 Sign in with Entra ID | US-102 | P0 | ✅ 2026-08-11 |
| 11 | US-202 Bootstrap the session from the current user | US-201, US-104 | P0 | ✅ 2026-08-11 |
| 12 | US-203 Keep the admin area off non-admin devices | US-202 | P0 | ✅ 2026-08-11 |
| 13 | US-204 Gate upload affordances on the Upload File permission | US-202 | P1 | ✅ 2026-08-11 — primitives only; frame `2h` re-asserts under US-801 |
| 14 | US-205 Sign out and leave nothing behind | US-202, US-104 | P0 | ✅ 2026-08-11 |
| **15** | **US-302 See my recent conversations** | US-202, US-104 | P0 | **next** |
| — | ⛔ **Pattern gate** — review `ConversationListStore` before any second list store | | | |
| 16 | US-301 Collapse the sidebar and have it stay collapsed | US-106 | P0 | |
| 17 | US-303 Start a new conversation without creating one | US-302 | P0 | |
| 18 | US-304 Rename a conversation without unlinking it | US-302, US-106 | P0 | |
| 19 | US-306 Delete a conversation | US-302, US-106 | P0 | |
| 20 | US-308 Act on the open conversation from its header | US-302 | P1 | ⚠ see note |

US-302 leads the phase on two counts: US-303, US-304, US-306, and US-308 all hang off it, and the PRD's risk table requires it be reviewed as a *pattern decision* before the 15 stores that copy it exist. US-301 needs only US-106, so it can run alongside.

⚠ **US-308 is scheduled ahead of its own criteria.** §8 places it in P2, but its header star routes through US-305 (P4) and two of its four kebab items through US-307 (P5). Build the 52px header bar with those controls absent and revisit in P4/P5, or move the whole story to P4.

## P3 — MVP chat · the minimum viable replacement, and the first phase worth deploying

| Order | Story | Depends on | Pri | Status |
| --- | --- | --- | --- | --- |
| 21 | US-404 `[enabler]` Decode the SSE frame stream | US-107 | P0 | |
| 22 | US-405 `[enabler]` Stream over an abortable fetch that reads errors first | US-103, US-404 | P0 | |
| 23 | US-402 Choose the model for a turn | US-202 | P0 | |
| 24 | US-403 Select MCP servers, and be stopped from selecting them where they cannot work | US-402 | P0 | |
| 25 | US-401 Send a first prompt and have the conversation created around it | US-303, US-405 | P0 | |
| 26 | US-406 Watch the answer arrive | US-405, US-104 | P0 | |
| 27 | US-407 Stop a turn and keep what it produced | US-406 | P0 | |
| 28 | US-501 See what the assistant is doing, in the order it happened | US-404, US-406 | P0 | |
| 29 | US-601 Render model output as markdown that cannot execute | US-108 | P0 | ⛔ see gate |
| 30 | US-602 Read a streaming answer without the page slowing down | US-601, US-406 | P0 | |
| 31 | US-606 Stay with the latest message, or stay where I am reading | US-406 | P0 | |

Transport first, then the composer controls, then send, then rendering. US-402 precedes US-401 by practical necessity rather than by its `Depends on` field — a turn needs a `modelId`.

⛔ **Gate at US-601.** The PRD's last open question comes due at the *start* of this story, not after it: `ngx-markdown` + marked + Prism are unpaid against the 629.33 kB / 720 kB baseline. Either re-measure and re-state the budget, or move the transcript renderer behind the lazy chat route — the choice changes the shape of the chat route.

Also fixed here, per the risk table: the coalescing and head/tail split in US-405 and US-602. Both have unit-level criteria that run against fixtures with no backend; getting them wrong is a rewrite of the chat surface.

## P4 — Chat completeness

| Order | Story | Depends on | Pri | Status |
| --- | --- | --- | --- | --- |
| 32 | US-305 Favorite a conversation | US-302 | P1 | |
| 33 | US-408 Recover when the conversation is already busy | US-405 | P1 | |
| 34 | US-409 See the name the server gave my conversation | US-406, US-302 | P1 | |
| 35 | US-410 Resume a conversation with the settings it last used | US-402, US-403 | P1 | |
| 36 | US-412 Understand an MCP consent requirement | US-403, US-103 | P1 | interim until US-411 |
| 37 | US-413 Dictate a prompt | US-401 | P2 | |
| 38 | US-502 See nested work inside the activity that caused it | US-501 | P1 | |
| 39 | US-503 Read the model's reasoning as it streams | US-501 | P1 | |
| 40 | US-504 See what a turn cost | US-501 | P1 | |
| 41 | US-505 Understand an activity that failed | US-501 | P1 | |
| 42 | US-603 Copy a code block | US-601 | P1 | |
| 43 | US-604 Read code in a theme that matches the page | US-105, US-603 | P1 | → US-603 |
| 44 | US-605 Render diagrams and math without paying for them upfront | US-601, US-108 | P2 | |
| 45 | US-607 Copy a prompt or a response | US-601 | P1 | |

US-305 lands here, which is also what unblocks the header star deferred out of US-308.

## P5 — Library, files, projects

| Order | Story | Depends on | Pri | Status |
| --- | --- | --- | --- | --- |
| 46 | US-701 Find a conversation by name | US-302 | P0 | |
| 47 | US-702 Page through my conversations | US-701 | P1 | |
| 48 | US-703 Filter to my favorites | US-701, US-305 | P1 | |
| 49 | US-704 Delete several conversations at once | US-702, US-106 | P1 | |
| 50 | US-705 Understand why conversations cannot be sorted | US-702 | P2 | interim until US-706 |
| 51 | US-801 Attach files to a conversation | US-204, US-401 | P1 | |
| 52 | US-802 Watch an upload get processed | US-801 | P1 | |
| 53 | US-803 Tell an expired upload apart from a failed one | US-802 | P1 | |
| 54 | US-804 Download a document | US-802 | P1 | |
| 55 | US-805 Understand why a file was rejected | US-801, US-103 | P1 | |
| 56 | US-901 Browse and search projects | US-104, US-106 | P1 | |
| 57 | US-903 Create, rename, describe, and delete a project | US-901 | P1 | |
| 58 | US-904 Give a project standing instructions | US-903 | P1 | |
| 59 | US-905 Manage a project's files | US-802, US-903 | P1 | |
| 60 | US-906 Start a conversation inside a project | US-401, US-903 | P1 | |
| 61 | US-307 Move a conversation into or out of a project | US-304, US-901 | P1 | deferred out of EP-3 |
| 62 | US-908 See and manage a project's conversations | US-903, US-307 | P1 | |

**Start US-907 (B1) during P3.** It is the one enabler the PRD explicitly schedules early, so US-908 never ships with its interim 500-item drain ceiling.

## P6 — Administration

| Order | Story | Depends on | Pri | Status |
| --- | --- | --- | --- | --- |
| 63 | US-1201 Find and page through users | US-203, US-104 | P1 | |
| 64 | US-1202 Create a user | US-1201 | P1 | |
| 65 | US-1203 Edit a user's profile and permissions | US-1201 | P1 | |
| 66 | US-1204 Deactivate a user | US-1201 | P1 | |
| 67 | US-1207 Manage the model catalog | US-203 | P1 | |
| 68 | US-1208 Manage MCP servers | US-203 | P1 | |
| 69 | US-1209 Reach an admin tab by URL | US-203 | P1 | |

## P7 — Conformance · gates run against everything shipped in P1–P6

| Order | Story | Depends on | Pri | Status |
| --- | --- | --- | --- | --- |
| 70 | US-1401 Use the whole app from the keyboard | US-606, US-903, US-1201 | P0 | |
| 71 | US-1402 Follow a streaming answer with a screen reader | US-406, US-501 | P0 | |
| 72 | US-1403 Use the app on a tablet or a phone | US-301, US-1201 | P0 | |
| 73 | US-1404 Respect a reduced-motion preference | US-106 | P1 | |
| 74 | US-1405 `[enabler]` Gate accessibility and budgets in the build | US-108, US-1401 | P1 | → US-1401 |

The `--warn` contrast question in §9 must be resolved upstream in `docs/design/` before US-1401's axe gate, not worked around locally.

## P8 — Enabled features · each enabler releases a frontend story

| Order | Story | Depends on | Pri | Status |
| --- | --- | --- | --- | --- |
| 75 | US-1001 `[enabler]` List the documents belonging to a conversation (B2) | — | P1 | open question on cross-conversation fetch |
| 76 | US-1002 Browse every document I have uploaded | US-1001, US-106 | P1 | |
| 77 | US-1003 Filter documents and group them by conversation | US-1002 | P2 | |
| 78 | US-1004 `[enabler]` Distinguish model-created documents (B7) | — | P2 | |
| 79 | US-1005 Filter to documents the assistant created | US-1004, US-1002 | P2 | |
| 80 | US-1101 `[enabler]` Put a message identity on the transcript and the stream (B4) | — | P1 | |
| 81 | US-1102 `[enabler]` Record feedback on an assistant message (B5) | US-1101 | P1 | |
| 82 | US-1103 Rate an assistant response | US-1102, US-607 | P2 | |
| 83 | US-1301 `[enabler]` Expose the usage audit trail over HTTP (B6) | — | P2 | verify against `ConversationUsage` first |
| 84 | US-1302 See what the platform is spending | US-1301, US-1209 | P2 | |
| 85 | US-1501 `[enabler]` Expose conversation export over HTTP (B11) | — | P2 | renderer stack undecided |
| 86 | US-1502 Download a conversation | US-1501, US-106 | P2 | |
| 87 | US-909 `[enabler]` Support project favorites (B3) | — | P2 | |
| 88 | US-910 Pin favorite projects to the sidebar | US-909, US-907, US-301 | P2 | replaces device-local pins |
| 89 | US-706 `[enabler]` Accept sort parameters on the paginated list endpoints (B8) | — | P2 | releases US-705's disabled sort |
| 90 | US-902 Sort projects, honestly | US-901 | P2 | |
| 91 | US-1205 `[enabler]` Filter user search by permission (B10) | — | P2 | |
| 92 | US-1206 Filter users by permission | US-1205, US-1201 | P2 | |
| 93 | US-411 `[enabler]` Carry the consent scope on the MCP authorization problem (B9) | — | P2 | upgrades US-412 |
| 94 | US-412 revisit — consent scope rendered from the problem extension | US-411 | P1 | not a separate story |

**These eleven enablers are a parallel track, not a final phase.** Nine have no dependencies and can start any time from P1 onward; only US-1102 and US-1004 wait on another enabler. Landing one early removes an interim behavior rather than adding a feature. Two questions gate the track: whether B3/B4/B7's new columns force the repository's **first EF Core migration** (settle before US-909 or US-1101), and which server-side stack renders `.docx`/`.pdf` for US-1501.

---

## Deferred and interim behaviors to retire

Each of these ships deliberately incomplete and is closed later. Kept here so none of them quietly becomes permanent.

| Interim behavior | Shipped in | Retired by |
| --- | --- | --- |
| Header star and two kebab items absent | US-308 (P2) | US-305 (P4), US-307 (P5) |
| Upload gating verified against a test host, not the composer | US-204 (P2) | US-801 (P5) |
| MCP consent message without the server's consent scope | US-412 (P4) | US-411 (P8) |
| Conversation and project sort disabled with a stated reason | US-705 (P5) | US-706 (P8) |
| 500-item drain ceiling with a visible notice | US-908 (P5) | US-907, started in P3 |
| Device-local project pins that do not sync | US-910's predecessor | US-909 (P8) |
| Export control absent rather than an `UnavailablePanel` | pre-US-1501 | US-1501 (P8) |
