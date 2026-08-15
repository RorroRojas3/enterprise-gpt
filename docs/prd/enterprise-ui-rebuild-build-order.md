# Build order: Enterprise GPT frontend rebuild

A dependency-resolved execution sequence for the 94 stories in [`enterprise-ui-rebuild.md`](enterprise-ui-rebuild.md), for tracking progress against.

**The PRD is authoritative.** This file adds no requirements and changes no scope. It derives one linear order from §8's phase table plus each story's `Depends on` field, because §7 is grouped by surface and its numbering is not a valid build order — four stories depend on a later-numbered one (US-105→US-109, US-106→US-109, US-307→US-901, US-401→US-405). Where this file and the PRD disagree, the PRD wins and this file is wrong.

Status here mirrors the `Status` field on each PRD story. Update both, or update the PRD and re-derive this.

**Progress: 62 / 94 done.** Current position: **P5 is complete, and with it EP-3, EP-7 and EP-9** — US-307 (row 61) closed the last row action in EP-3 and the last two controls frame `4a` was missing, and US-908 (row 62) closed EP-9. The backend enabler US-907 landed alongside them, off the parallel track, which is why US-908 never shipped its interim drain ceiling. Next is **P6, `US-1201`** — the administration epic, which nothing in P1–P5 blocks. The only thing left outstanding against a shipped P5 story is US-706, the sort enabler that retires US-705's stated order (row 89).

Within a phase, stories on the same table row have no ordering constraint between them and can be taken in any order or in parallel. `→` in the notes column means "must follow".

---

## P1 — Foundation · ✅ complete

| Order | Story                                                                     | Depends on             | Pri | Status        |
| ----- | ------------------------------------------------------------------------- | ---------------------- | --- | ------------- |
| 1     | US-101 `[enabler]` Scaffold the zoneless Angular 21 workspace             | —                      | P0  | ✅ 2026-08-09 |
| 2     | US-102 `[enabler]` Load configuration at runtime before bootstrap         | US-101                 | P0  | ✅ 2026-08-09 |
| 3     | US-103 `[enabler]` Type and normalize every API error                     | US-101                 | P0  | ✅ 2026-08-09 |
| 4     | US-104 `[enabler]` Build the reusable signal-store features               | US-101                 | P0  | ✅ 2026-08-09 |
| 5     | US-107 `[enabler]` Vendor the Andes streaming contract with a drift check | US-101                 | P0  | ✅ 2026-08-10 |
| 6     | US-108 `[enabler]` Enforce lint rules and bundle budgets in the build     | US-101                 | P0  | ✅ 2026-08-10 |
| 7     | US-109 `[enabler]` Ship the brand assets, type scale, and icon set        | US-101                 | P0  | ✅ 2026-08-10 |
| 8     | US-105 Switch between light and dark without a flash                      | US-101, US-109         | P0  | ✅ 2026-08-10 |
| 9     | US-106 `[enabler]` Build the shared UI kit                                | US-101, US-105, US-109 | P0  | ✅ 2026-08-10 |

US-109 sits ahead of US-105 and US-106 despite its number: it ships the assets both of them consume.

## P2 — Signed-in shell · ✅ complete

| Order | Story                                                                             | Depends on     | Pri | Status                                                              |
| ----- | --------------------------------------------------------------------------------- | -------------- | --- | ------------------------------------------------------------------- |
| 10    | US-201 Sign in with Entra ID                                                      | US-102         | P0  | ✅ 2026-08-11                                                       |
| 11    | US-202 Bootstrap the session from the current user                                | US-201, US-104 | P0  | ✅ 2026-08-11                                                       |
| 12    | US-203 Keep the admin area off non-admin devices                                  | US-202         | P0  | ✅ 2026-08-11                                                       |
| 13    | US-204 Gate upload affordances on the Upload File permission                      | US-202         | P1  | ✅ 2026-08-11 — primitives only; frame `2h` re-asserted by US-801, 2026-08-14 |
| 14    | US-205 Sign out and leave nothing behind                                          | US-202, US-104 | P0  | ✅ 2026-08-11                                                       |
| 15    | US-302 See my recent conversations                                                | US-202, US-104 | P0  | ✅ 2026-08-11 — with the shell that hosts the sidebar               |
| —     | ⛔ **Pattern gate** — review `ConversationListStore` before any second list store |                |     | ✅ reviewed 2026-08-11                                              |
| 16    | US-301 Collapse the sidebar and have it stay collapsed                            | US-106         | P0  | ✅ 2026-08-11                                                       |
| 17    | US-303 Start a new conversation without creating one                              | US-302         | P0  | ✅ 2026-08-11 — prompt chips deferred to US-401                     |
| 18    | US-304 Rename a conversation without unlinking it                                 | US-302, US-106 | P0  | ✅ 2026-08-11 — first Signal Form; row kebab ships Rename-only      |
| 19    | US-306 Delete a conversation                                                      | US-302, US-106 | P0  | ✅ 2026-08-11 — optimistic removal, navigation on the 204           |
| 20    | US-308 Act on the open conversation from its header                               | US-302         | P1  | ✅ 2026-08-11 — ⚠ shipped per the note below                        |

US-302 leads the phase on two counts: US-303, US-304, US-306, and US-308 all hang off it, and the PRD's risk table requires it be reviewed as a _pattern decision_ before the 15 stores that copy it exist. US-301 needs only US-106, so it can run alongside.

⚠ **US-308 was scheduled ahead of its own criteria, and shipped that way.** §8 places it in P2, but its header star routed through US-305 (P4) and two of its four kebab items through US-307 (P5); the 52px header bar shipped with those controls absent, per this note. Its sub-768px mobile-navbar criterion was additionally deferred to US-1403 on 2026-08-11 — frame `1d`'s hamburger opens frame `3e`'s sidebar overlay, which does not exist until the responsive shell does. The star was retired on 2026-08-13 when US-305 was pulled forward, and the two project items on 2026-08-15 when US-307 landed; only the mobile-navbar collapse remains, tracked in the interim-behaviors table below.

## P3 — MVP chat · ✅ complete — the minimum viable replacement, and the first phase worth deploying

| Order | Story                                                                                | Depends on     | Pri | Status                                                                                                               |
| ----- | ------------------------------------------------------------------------------------ | -------------- | --- | -------------------------------------------------------------------------------------------------------------------- |
| 21    | US-404 `[enabler]` Decode the SSE frame stream                                       | US-107         | P0  | ✅ 2026-08-12                                                                                                        |
| 22    | US-405 `[enabler]` Stream over an abortable fetch that reads errors first            | US-103, US-404 | P0  | ✅ 2026-08-12                                                                                                        |
| 23    | US-402 Choose the model for a turn                                                   | US-202         | P0  | ✅ 2026-08-12 — send ships inert until US-401                                                                        |
| 24    | US-403 Select MCP servers, and be stopped from selecting them where they cannot work | US-402         | P0  | ✅ 2026-08-12 — ⚠ two deviations, see below                                                                          |
| 25    | US-401 Send a first prompt and have the conversation created around it               | US-303, US-405 | P0  | ✅ 2026-08-12 — ⚠ URL via `router.navigateByUrl` + `replaceUrl`, a documented deviation; chips from US-303 ship here |
| 26    | US-406 Watch the answer arrive                                                       | US-405, US-104 | P0  | ✅ 2026-08-12 — also retires US-403's tool-server-unavailable panel                                                  |
| 27    | US-407 Stop a turn and keep what it produced                                         | US-406         | P0  | ✅ 2026-08-12                                                                                                        |
| 28    | US-501 See what the assistant is doing, in the order it happened                     | US-404, US-406 | P0  | ✅ 2026-08-12 — flat cards; US-502 nests them                                                                        |
| 29    | US-601 Render model output as markdown that cannot execute                           | US-108         | P0  | ✅ 2026-08-13 — gate resolved, see below                                                                             |
| 30    | US-602 Read a streaming answer without the page slowing down                         | US-601, US-406 | P0  | ✅ 2026-08-13 — split takes a third boundary the criteria omit                                                       |
| 31    | US-606 Stay with the latest message, or stay where I am reading                      | US-406         | P0  | ✅ 2026-08-13 — following driven by a `ResizeObserver`, not the turn's signals                                       |

Transport first, then the composer controls, then send, then rendering. US-402 precedes US-401 by practical necessity rather than by its `Depends on` field — a turn needs a `modelId`.

✅ **Gate at US-601, resolved 2026-08-13 — the renderer moved rather than the budget.** `provideChatMarkdown()` is provided at the `Chat` component, so the whole 124.8 kB markdown stack rides the lazy chat chunk (48.60 → 179.47 kB) and none of it is in the initial graph; `check-initial-chunk.mjs` now fails the build on any static path from `main.ts` to `ngx-markdown`, `marked`, `prismjs` or `dompurify`. The baseline was re-measured anyway: **660.24 kB raw / 161.01 kB transfer** initial, with the warn line re-stated 660 → **665 kB** and the ceiling held at 720 kB. The ~11.5 kB of initial growth is Angular's own `DomSanitizerImpl` — `MarkdownService` injects `DomSanitizer` — plus ~1.6 kB of global CSS, not the libraries.

Also fixed here, per the risk table: the coalescing and head/tail split in US-405 and US-602. Both have unit-level criteria that run against fixtures with no backend; getting them wrong is a rewrite of the chat surface.

## P4 — Chat completeness

| Order | Story                                                           | Depends on     | Pri | Status                                                                                     |
| ----- | --------------------------------------------------------------- | -------------- | --- | ------------------------------------------------------------------------------------------ |
| 32    | US-305 Favorite a conversation                                  | US-302         | P1  | ✅ 2026-08-13 — taken early, with P3's rendering stories; retires US-308's header star     |
| 33    | US-408 Recover when the conversation is already busy            | US-405         | P1  | ✅ 2026-08-13 — frame `1h`'s row variant; a focus defect on repeated Retry fixed with it   |
| 34    | US-409 See the name the server gave my conversation             | US-406, US-302 | P1  | ✅ 2026-08-13 — taken after US-410, see below                                              |
| 35    | US-410 Resume a conversation with the settings it last used     | US-402, US-403 | P1  | ✅ 2026-08-13 — ⚠ re-sized S → M: history replay shipped here                              |
| 36    | US-412 Understand an MCP consent requirement                    | US-403, US-103 | P1  | ✅ 2026-08-13 — interim variant; consent action still waits on US-411                      |
| 37    | US-413 Dictate a prompt                                         | US-401         | P2  | ✅ 2026-08-13                                                                              |
| 38    | US-502 See nested work inside the activity that caused it       | US-501         | P1  | ✅ 2026-08-13 — render-side only; the filter is root membership, not `parentScopeId`       |
| 39    | US-503 Read the model's reasoning as it streams                 | US-501         | P1  | ✅ 2026-08-13 — the duration is measured client-side; the wire carries none                |
| 40    | US-504 See what a turn cost                                     | US-501         | P1  | ✅ 2026-08-13 — footer always visible until US-607; the activity strip shows what is known |
| 41    | US-505 Understand an activity that failed                       | US-501         | P1  | ✅ 2026-08-13 — mostly already structural; taken after US-502, which made it observable    |
| 42    | US-603 Copy a code block                                        | US-601         | P1  | ✅ 2026-08-13 — custom renderer + one delegated listener; `div`/`button` join the profile  |
| 43    | US-604 Read code in a theme that matches the page               | US-105, US-603 | P1  | ✅ 2026-08-14 — ⚠ no stylesheet swap; verification and gates instead, see below            |
| 45    | US-607 Copy a prompt or a response                              | US-601         | P1  | ✅ 2026-08-14 — retires US-504's always-visible footer                                     |
| 44    | US-605 Render diagrams and math without paying for them upfront | US-601, US-108 | P2  | ✅ 2026-08-14 — the custom-renderer branch again; diagrams appear at settle                |

US-305 was pulled forward and landed with P3, which is what unblocked the header star deferred out of US-308; the rest of the phase is untouched.

**EP-6 completed 2026-08-14, in the order US-604 → US-607 → US-605.** They are sequential rather than parallel for one mechanical reason: US-607 and US-605 both edit `assistant-turn.html`, and taking the smallest first meant the code-block chrome was settled before anything else touched it. US-701 shares no file with any of the three and was taken alongside them.

⚠ **US-604 does not swap a stylesheet, and will not.** The board fixes `--code-bg` and `--code-head` dark in _both_ themes and `check-tokens.mjs` enforces that parity, so there is one token-driven Prism palette and nothing to swap to — which is also why the story's third criterion is free. Agreed with the product owner before implementation. What the story shipped instead is the enforcement its fourth criterion needed: 26 measured code-surface pairs, including the head bar's hovered state and `--accent` at SC 1.4.11's 3:1, plus a check that the pair list has not fallen behind the stylesheets and a ban on importing a Prism theme — in `angular.json` as well as `src/`, since the `styles` array is the route a theme would most likely arrive by.

⚠ **US-605 renders a diagram at settle, not as it streams.** The tail's fences have had their info strings stripped, and re-laying-out an SVG on every flush of the head would fight both the batch cadence and US-606's `ResizeObserver`. A mermaid fence reads as a `mermaid`-labelled code block while the answer arrives — the same contract US-602 set for highlighting. It also declines `ngx-markdown`'s own integrations, as US-603 declined its clipboard directive: theirs mutate the shared renderer globally, interpolate the source unescaped, and offer no error hook, which puts the story's fourth criterion out of reach.

**EP-4 completed 2026-08-13, in two lanes rather than the numbered order.** US-408 and US-412 are two variants of one component and were taken together; US-410 preceded US-409 because both extend `TurnStore` and `ConversationStore`, and US-409's "was this the conversation's first turn" reads cleanest once replayed history exists to answer it. US-413 touches only the composer and was independent of all four.

⚠ **US-410 was an S that turned out to be an M.** Its third criterion is about a _refetched_ transcript, and nothing in the client had ever fetched one — reopening a conversation showed a blank screen. History replay shipped inside the story rather than becoming a new one, agreed with the product owner before implementation.

**EP-5 completed 2026-08-13, in the order US-502 → US-505 → US-504 → US-503, not the numbered one.** The three card stories share `ActivityCard`, so they are strictly sequential whatever order they take; US-505 follows US-502 because nesting is what makes "a failed child does not fail its parent" something a spec can see, and US-504's chevron is easier to place once the card's header is final. US-603 shares no code with any of them, but it adds a delegated listener to `Transcript` and cases to `transcript.spec.ts`, which is why it ran after rather than beside them — the only two files a parallel worktree would have collided on.

**Three of the five had no data behind part of their design**, and each resolved differently rather than by one rule: US-503 measures the reasoning duration client-side because a plausible number exists to measure; US-504 renders the activity strip with the duration alone, because that keeps the chevron live and the token columns arrive with the field; US-505 leaves frame `1f`'s failure-reason line unbuilt, because nothing on the wire could fill it and inventing copy is the fabricated status line US-406 refuses.

**The review round changed US-503 twice over.** Its first cut opened the timing window on the turn's _first_ `ReasoningDelta` — which is the server's seed, so the pill reported stream latency as thinking time — and hung the region inside a component that `showLiveTurn` keeps off screen until the first token, so a story called "read the model's reasoning **as it streams**" showed it only once streaming was over. Both are fixed, and the fix that resolved them together was to treat the seeded delta as what it is: a request-level status line, which US-406 already decided this app does not render. It is now measured out of the text by length and skipped by the clock, and the ridgeline hands the gap to the region exactly when the model starts reasoning in its own words.

## P5 — Library, files, projects · ✅ complete

| Order | Story                                                 | Depends on     | Pri | Status                                                                              |
| ----- | ----------------------------------------------------- | -------------- | --- | ----------------------------------------------------------------------------------- |
| 46    | US-701 Find a conversation by name                    | US-302         | P0  | ✅ 2026-08-14 — the screen and the URL contract; the `name=` search already existed |
| 47    | US-702 Page through my conversations                  | US-701         | P1  | ✅ 2026-08-14 — a query replaces rows, a page appends; two guards keep them apart   |
| 48    | US-703 Filter to my favorites                         | US-701, US-305 | P1  | ✅ 2026-08-14 — `?favorites=1`; an unstarred row leaves the filtered list           |
| 49    | US-704 Delete several conversations at once           | US-702, US-106 | P1  | ✅ 2026-08-14 — the request lives in the root actions store, not this route's       |
| 50    | US-705 Understand why conversations cannot be sorted  | US-702         | P2  | ✅ 2026-08-14 — the order is stated; interim until US-706                           |
| 51    | US-801 Attach files to a conversation                 | US-204, US-401 | P1  | ✅ 2026-08-14 — uploads start as early as an id exists; Send gates on them          |
| 52    | US-802 Watch an upload get processed                  | US-801         | P1  | ✅ 2026-08-14 — staged poll 500ms→5s; the POST is raw `XMLHttpRequest` (see below)  |
| 53    | US-803 Tell an expired upload apart from a failed one | US-802         | P1  | ✅ 2026-08-14 — a 404 is `unknown`, never a failure, and offers no Retry            |
| 54    | US-804 Download a document                            | US-802         | P1  | ✅ 2026-08-14 — signed link fetched on click, handed to a detached `<a download>`   |
| 55    | US-805 Understand why a file was rejected             | US-801, US-103 | P1  | ✅ 2026-08-14 — four refusal shapes, not two; ⚠ two board departures, see below     |
| 56    | US-901 Browse and search projects                     | US-104, US-106 | P1  | ✅ 2026-08-14 — server-side search, a drained set, a stated ceiling; no sort, no star |
| 57    | US-903 Create, rename, describe, and delete a project | US-901         | P1  | ✅ 2026-08-14 — one modal for create and edit; a delete releases its conversations    |
| 58    | US-904 Give a project standing instructions           | US-903         | P1  | ✅ 2026-08-14 — the detail screen and its tab routes; the warning rides canDeactivate |
| 59    | US-905 Manage a project's files                       | US-802, US-903 | P1  | ✅ 2026-08-14 — no new upload code at all; EP-8's `UploadTarget` is why               |
| 60    | US-906 Start a conversation inside a project          | US-401, US-903 | P1  | ✅ 2026-08-14 — ⚠ re-sized S → M: the composer moved to `shared/` behind a token      |
| 61    | US-307 Move a conversation into or out of a project   | US-304, US-901 | P1  | ✅ 2026-08-15 — deferred out of EP-3; one picker, five invokers, and not a `Menu`   |
| 62    | US-908 See and manage a project's conversations       | US-903, US-307 | P1  | ✅ 2026-08-15 — one filtered request; its interim ceiling never shipped             |

**EP-7 completed 2026-08-14, in the numbered order.** The four stories all edit the same three files, so they are sequential whatever order they take, and each one's shape depends on the one before it: US-704's selection has to survive a Load more, and US-705's "no client-side reordering" is only assertable once a partially loaded list exists. US-307 later added the project chip and the per-row kebab the frame also draws (row 61); US-706 remains a backend enabler on the parallel track (row 89).

**EP-8 completed 2026-08-14, in the numbered order.** The five stories are one feature — every one of them is a state the same chip can be in — so they were taken as a unit rather than sequenced. Documented in [docs/ui/file-attachments.md](../ui/file-attachments.md). Two decisions in it were not derivable from the criteria and are recorded there in full: **uploads start as early as a conversation id exists** and `TurnStore` gates the stream on `UploadStore.settled$()`, because retrieval attaches `document_search` from what the conversation _already_ holds and a file posted as the stream opens is invisible to the very turn that asked about it; and **the upload POST is raw `XMLHttpRequest`**, because `HttpEventType.UploadProgress` is emitted by `HttpXhrBackend` only and the app runs `provideHttpClient(withFetch())` — so frame `4l`'s transfer percentage is unobtainable through `HttpClient` here. That transport restates the auth interceptor's single forced-refresh replay via `authErrorDecision` and deliberately does **not** restate `retryInterceptor`, which would queue the file twice. Concurrency is capped at three transfers, because the origin's six connections are shared with the SSE stream and the status polls.

⚠ **Two deliberate departures from frame `4l`**, both because the board is wrong about the API rather than because the app disagrees with the design. The board's example copy names XLSX and CSV, which the API does not accept, and omits PPTX, which it does — so the supported set is built from `GET api/documents/file-extensions` and no string is quoted from the board. And the board draws Retry on its **unsupported** chip, which this app does not: re-posting a `.mov` produces the same refusal, the same reasoning `canRetry` in `core/errors` already records. Retry stays on the `failed` arm, where a second attempt can succeed. Neither story's criteria are weakened — US-802 ties Retry to `Failed`, and US-805 asks only that the unsupported chip name the extension and the supported set.

**No re-baseline.** The initial bundle measures **663.92 kB raw / 163.32 kB transfer** against 670 kB warn / 720 kB error. Everything EP-8 added rides the lazy `chat` chunk or `core/` code only chat imports, and the feature adds no dependency of any kind.

**EP-9's first five stories completed 2026-08-14, in the numbered order — the one epic in P5 that had to be.** US-903 needs a grid to be invoked from; US-904 builds the detail screen; and US-905 and US-906 both add to that screen's tab strip and its composer slot, which frame `4e` draws on one page. Two worktrees would have collided on the same three files for no gain. US-908 closed the epic on 2026-08-15, after US-307.

**A prep step came first, and it is the largest thing in the epic.** Frame `4e` puts the _shared_ composer on the project screen, but the component lived in `features/chat/` and injected the route-scoped `TurnStore` — and dependencies run one way. So `UploadStore` moved to `core/documents/`, the composer stack moved to `shared/composer/`, and what executes a prompt became the `COMPOSER_HOST` token: `TurnStore` on `/chat`, `ProjectComposerHost` on a project. Neither move touches the initial graph — the composer now sits in a chunk the two lazy routes share — and a `no-restricted-imports` rule was added so the layering that forced all this is machine-checked rather than remembered.

**Two decisions were not derivable from the criteria.** `beginEdit` **fetches the project before opening the form**, because `ProjectSummaryDto` omits `instructions` entirely and a form seeded from a card would clear them on a full-representation PUT. And a project delete **releases its conversations client-side** (`ConversationListStore.releaseProject`, and the same clear on `ConversationStore`), because the server does exactly that and `toUpdateBody` echoes `projectId` — a row left holding a deleted id would take a 404 on its next rename, a failure with nothing to do with projects.

**No re-baseline.** The initial bundle measures **667.05 kB raw / 168.02 kB transfer** against 670 kB warn / 720 kB error. The +3.1 kB is the events plugin's handler API reaching `ConversationListStore`, which is on the initial graph; everything else rides the lazy `projects` chunk or the composer chunk it shares with `chat`.

**US-307 and US-908 closed the phase on 2026-08-15, in that order, which was not optional.** US-908's row menu *is* US-307's five actions and its panel opens US-307's picker; the other order would have meant building the menu twice. Three decisions in them were not derivable from the criteria and are recorded in [docs/ui/projects.md](../ui/projects.md) in full. **The project picker is not a `Menu`** — `Menu` renders its own trigger, its panel is `role="menu"` (whose only valid children are `menuitem*`), and its keyboard handling swallows the arrows and closes on Tab, which is three collisions with a search field; it is a non-modal `role="dialog"` holding a listbox, borrowing only placement and dismissal, which were extracted so both share them. **A root `ProjectLookupStore` drains every project to the 500 ceiling**, because `ConversationDto` carries a `projectId` and no name, so no chip anywhere could render one without it — and unlike the projects grid it **keeps** the pages already loaded when a drain fails, since a lookup that can still name 200 of 500 projects beats one that names none. And **the project dialogs moved from `ProjectsLayout` to `Shell`**, which US-903 forecast: the picker's "New project" is the first invoker outside the projects feature.

**US-907 (B1) landed with them rather than during P3**, which is where the PRD scheduled it — earlier would have been better, but landing it in the same batch was enough to keep US-908's interim 500-item drain ceiling out of every build. See the interim-behaviours table below.

**No re-baseline, and 1.1 kB of headroom left.** The initial bundle measures **668.90 kB raw / 168.23 kB transfer** against 670 kB warn / 720 kB error. The +1.85 kB is `ProjectLookupStore`, which is root-scoped and released by `SessionBootstrap` — the same shape as EP-9's +3.1 kB, and the same lesson: **the next root-scoped store trips the warning line.** The picker, the dialogs and every chip they feed ride lazy chunks.

## P6 — Administration

| Order | Story                                         | Depends on     | Pri | Status |
| ----- | --------------------------------------------- | -------------- | --- | ------ |
| 63    | US-1201 Find and page through users           | US-203, US-104 | P1  |        |
| 64    | US-1202 Create a user                         | US-1201        | P1  |        |
| 65    | US-1203 Edit a user's profile and permissions | US-1201        | P1  |        |
| 66    | US-1204 Deactivate a user                     | US-1201        | P1  |        |
| 67    | US-1207 Manage the model catalog              | US-203         | P1  |        |
| 68    | US-1208 Manage MCP servers                    | US-203         | P1  |        |
| 69    | US-1209 Reach an admin tab by URL             | US-203         | P1  |        |

## P7 — Conformance · gates run against everything shipped in P1–P6

| Order | Story                                                           | Depends on              | Pri | Status    |
| ----- | --------------------------------------------------------------- | ----------------------- | --- | --------- |
| 70    | US-1401 Use the whole app from the keyboard                     | US-606, US-903, US-1201 | P0  |           |
| 71    | US-1402 Follow a streaming answer with a screen reader          | US-406, US-501          | P0  |           |
| 72    | US-1403 Use the app on a tablet or a phone                      | US-301, US-1201         | P0  |           |
| 73    | US-1404 Respect a reduced-motion preference                     | US-106                  | P1  |           |
| 74    | US-1405 `[enabler]` Gate accessibility and budgets in the build | US-108, US-1401         | P1  | → US-1401 |

The `--warn` contrast question in §9 must be resolved upstream in `docs/design/` before US-1401's axe gate, not worked around locally.

## P8 — Enabled features · each enabler releases a frontend story

| Order | Story                                                                            | Depends on             | Pri | Status                                    |
| ----- | -------------------------------------------------------------------------------- | ---------------------- | --- | ----------------------------------------- |
| 75    | US-1001 `[enabler]` List the documents belonging to a conversation (B2)          | —                      | P1  | open question on cross-conversation fetch |
| 76    | US-1002 Browse every document I have uploaded                                    | US-1001, US-106        | P1  |                                           |
| 77    | US-1003 Filter documents and group them by conversation                          | US-1002                | P2  |                                           |
| 78    | US-1004 `[enabler]` Distinguish model-created documents (B7)                     | —                      | P2  |                                           |
| 79    | US-1005 Filter to documents the assistant created                                | US-1004, US-1002       | P2  |                                           |
| 80    | US-1101 `[enabler]` Put a message identity on the transcript and the stream (B4) | —                      | P1  |                                           |
| 81    | US-1102 `[enabler]` Record feedback on an assistant message (B5)                 | US-1101                | P1  |                                           |
| 82    | US-1103 Rate an assistant response                                               | US-1102, US-607        | P2  |                                           |
| 83    | US-1301 `[enabler]` Expose the usage audit trail over HTTP (B6)                  | —                      | P2  | verify against `ConversationUsage` first  |
| 84    | US-1302 See what the platform is spending                                        | US-1301, US-1209       | P2  |                                           |
| 85    | US-1501 `[enabler]` Expose conversation export over HTTP (B11)                   | —                      | P2  | renderer stack undecided                  |
| 86    | US-1502 Download a conversation                                                  | US-1501, US-106        | P2  |                                           |
| 87    | US-909 `[enabler]` Support project favorites (B3)                                | —                      | P2  |                                           |
| 88    | US-910 Pin favorite projects to the sidebar                                      | US-909, US-907, US-301 | P2  | replaces device-local pins                |
| 89    | US-706 `[enabler]` Accept sort parameters on the paginated list endpoints (B8)   | —                      | P2  | releases US-705's disabled sort           |
| 90    | US-902 Sort projects, honestly                                                   | US-901                 | P2  |                                           |
| 91    | US-1205 `[enabler]` Filter user search by permission (B10)                       | —                      | P2  |                                           |
| 92    | US-1206 Filter users by permission                                               | US-1205, US-1201       | P2  |                                           |
| 93    | US-411 `[enabler]` Carry the consent scope on the MCP authorization problem (B9) | —                      | P2  | upgrades US-412                           |
| 94    | US-412 revisit — consent scope rendered from the problem extension               | US-411                 | P1  | not a separate story                      |

**These enablers are a parallel track, not a final phase.** Eight of the ten below have no dependencies and can start any time; only US-1102 and US-1004 wait on another enabler. Landing one early removes an interim behavior rather than adding a feature — **US-907 is the proof**: it shipped with US-908 rather than in P3 as scheduled, and that was still early enough for US-908's interim drain ceiling never to reach a build. Two questions still gate the track: whether B3/B4/B7's new columns need a migration of their own (settle before US-909 or US-1101), and which server-side stack renders `.docx`/`.pdf` for US-1501.

---

## Deferred and interim behaviors to retire

Each of these ships deliberately incomplete and is closed later. Kept here so none of them quietly becomes permanent.

| Interim behavior                                                                                                                                                 | Shipped in                                  | Retired by                                                                                                                           |
| ---------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| ~~Header star absent~~                                                                                                                                           | US-308 (P2)                                 | ✅ retired by US-305, 2026-08-13                                                                                                     |
| ~~Two kebab items (move to / remove from project) absent~~                                                                                                       | US-308 (P2)                                 | ✅ retired by US-307, 2026-08-15 — in the sidebar row's kebab and the chat header's, with "Remove from project" **absent rather than inert** on a standalone conversation |
| Tools rows show the server name without frame `2c`'s mono key — `McpDto` carries no key field                                                                    | US-403 (P3)                                 | a backend enabler adding a key, if ever prioritized                                                                                  |
| ~~409-busy and MCP-consent errors render the generic turn-error notice~~                                                                                         | US-406/407 (P3)                             | ✅ retired by US-408 and US-412, 2026-08-13                                                                                          |
| `composer--streaming` dims `.composer__aux` — the microphone, the attach control and the project pill carry it; the conversation download does not exist yet     | US-407 (P3)                                 | US-1502 adopts the class when it lands (US-413 did, 2026-08-13; US-801 did, 2026-08-14; US-307 did, 2026-08-15)                      |
| Chat header does not collapse into frame `1d`'s 54px mobile navbar below 768px                                                                                   | US-308 (P2)                                 | US-1403 (P7)                                                                                                                         |
| ~~Upload gating verified against a test host, not the composer~~                                                                                                 | US-204 (P2)                                 | ✅ retired by US-801, 2026-08-14 — the paperclip and the prompt box's drop zone are both bound to `canUploadFiles()`                 |
| No way to detach a document once its upload succeeded — the API has a `DELETE` for **project** documents only, so a conversation chip's `×` renames itself from "Remove" to "Dismiss" and hides the chip without detaching anything | US-801 (P5) | a backend enabler adding `DELETE api/documents/conversations/{id}/{documentId}`, if ever prioritized |
| MCP consent card with no action, because the scope to request one is not on the wire                                                                             | US-412 (P4)                                 | US-411 (P8)                                                                                                                          |
| Failed activity cards carry no reason, because `ActivityFailed` sends none                                                                                       | US-505 (P4)                                 | a backend enabler putting failure text on the event, if ever prioritized                                                             |
| The activity cost strip shows `duration` only — no server attributes tokens per activity                                                                         | US-504 (P4)                                 | a contract upgrade populating `AssistantActivity.usage`                                                                              |
| ~~The message footer is always visible rather than revealed on hover and focus~~                                                                                 | US-504 (P4)                                 | ✅ retired by US-607, 2026-08-14                                                                                                     |
| ~~The library shows the first page only — no counter, no Load more~~                                                                                             | US-701 (P5)                                 | ✅ retired by US-702, 2026-08-14                                                                                                     |
| ~~The library offers no favourites filter, and no row star~~                                                                                                     | US-701 (P5)                                 | ✅ retired by US-703, 2026-08-14                                                                                                     |
| ~~The library offers no row selection or bulk bar~~ (the **per-row kebab** was always US-307's)                                                                   | US-701 (P5)                                 | ✅ selection and the bulk bar retired by US-704, 2026-08-14; the kebab, with the project chip, by US-307, 2026-08-15                 |
| The library's row star is confirm-then-render rather than optimistic — `ConversationActionsStore` cannot patch the library's own entities, so a flip would have no rollback behind it | US-703 (P5)                                 | nothing planned; revisit only if the two stores ever share an entity surface                                                          |
| Diagrams appear when a turn settles, never as it streams                                                                                                         | US-605 (P4)                                 | nothing planned — re-laying-out an SVG per flush is the cost this avoids                                                             |
| The code-head Copy control has no `bi-copy` glyph — the sprite needs `svg`/`use` in the sanitizer profile                                                        | US-603 (P4)                                 | nothing planned; revisit only if the profile widens for another reason                                                               |
| A code block that overflows its column scrolls by pointer only — the `<pre>` has no `tabindex`, so keyboard users cannot reach the rest of the line (WCAG 2.1.1) | pre-dates US-603, which now owns the chrome | US-1401 (P7), which owns keyboard operability and the axe gate; it needs `tabindex` in `CHAT_ALLOWED_ATTR` and a name for the region |
| Replayed history carries no activity timeline, since the stream is never persisted                                                                               | US-410 (P4)                                 | nothing planned — a server-side turn record would be a new enabler                                                                   |
| Conversation and project sort stated rather than offered — the library's toolbar reads "Newest first" and carries the reason; there is no sort control          | US-705 (P5)                                 | US-706 (P8), which replaces `ORDER_EXPLANATION` and its two toolbar elements with a real control issuing `sort=`/`dir=`              |
| No sort control on the projects grid; a truncation line states the ceiling in frame `4g`'s wording rather than frame `4d`'s, which names sorting                  | US-901 (P5)                                 | US-902 (P8)                                                                                                                          |
| The project card's favourite star is absent — `ProjectSummaryDto` carries no flag                                                                                 | US-901 (P5)                                 | US-909 (P8)                                                                                                                          |
| ~~The project dialogs mount on `ProjectsLayout` rather than the shell, because both invokers are still inside the feature~~                                       | US-903 (P5)                                 | ✅ retired by US-307, 2026-08-15 — and it retired `ProjectsLayout`'s destroy-time cancel with them; a dialog dismissed with browser Back now stays open over the next route, exactly as the conversation dialogs always have |
| ~~The project detail tab strip has no Conversations tab~~                                                                                                        | US-904 (P5)                                 | ✅ retired by US-908, 2026-08-15                                                                                                     |
| ~~The project composer has no project picker — the project is fixed by the route~~                                                                               | US-906 (P5)                                 | ✅ retired by US-307, 2026-08-15 — a pill with two arms: the picker on `/chat`, a static chip on a project's own screen, where the project **is** the route. Still absent on empty `/chat`, where there is no conversation to move |
| ~~`SessionBootstrap` still does not request projects (US-202's fourth criterion), because no root project list has a consumer yet~~                                | pre-EP-9                                    | ✅ retired by US-307, 2026-08-15 — `ProjectLookupStore` is the first consumer, feeding both the picker's list and every chip that has to name a `projectId` |
| **500-item drain ceiling with a visible notice — never shipped.** US-907 landed in the same batch as US-908, so the interim criterion was live for no build at all; the panel issues one filtered request | US-908 (P5) — not, in the end, shipped      | US-907, 2026-08-15                                                                                                                   |
| Device-local project pins that do not sync                                                                                                                       | US-910's predecessor                        | US-909 (P8)                                                                                                                          |
| Export control absent rather than an `UnavailablePanel`                                                                                                          | pre-US-1501                                 | US-1501 (P8)                                                                                                                         |
