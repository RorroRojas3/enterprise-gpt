# Breaking a feature into epics and user stories

Rules for section 7 of the PRD (`prd-template.md`). The goal is a story set a team can pick up in order, with no hidden work and no story that can't be verified.

## 1. Epics

An epic is an **independently shippable slice of the feature tied to one goal** — when the epic is done, a user can do something they couldn't before. Typical feature: 2–6 epics.

Each epic gets: a one-line goal, a priority, a rolled-up estimate, and its dependencies on other epics.

**Anti-pattern — epics as architectural layers.** "Backend epic" / "Frontend epic" / "Database epic" is not a decomposition; no layer ships value alone, and every story ends up depending on stories in other epics. Slice by user capability instead ("Search", "Saved filters", "Admin moderation").

## 2. Vertical slicing

Every story cuts through the whole stack it needs — UI to API to data — so completing it produces observable behavior. When a story is too big, split it with the **SPIDR** heuristics:

- **Spike** — extract a time-boxed research story when unknowns block estimation.
- **Path** — split by user path (happy path first, then alternates).
- **Interface** — split by interface (web first, then mobile; one input method first).
- **Data** — split by data subset (one record type first, then the rest).
- **Rules** — split by business rule (relaxed rules first, then tighten).

Technical enabler stories (infrastructure, migrations, spikes) are allowed but must be **few**, labeled `[enabler]` in the title, and justified by the stories they unblock. Never dress them up in fake-user phrasing — "As a developer, I want a repository class so that the code is clean" is not a user story; write "`[enabler]` Provision the search index" and say which stories depend on it.

## 3. INVEST checklist

Each story should be **I**ndependent, **N**egotiable, **V**aluable, **E**stimable, **S**mall, **T**estable. The two most commonly violated:

- **Independent** — minimize `depends on` links. A story that blocks three or more others is a bottleneck: schedule it first or split it so the others can start.
- **Small** — see estimates below; if it doesn't fit in L, it isn't a story yet.

## 4. Acceptance criteria

- Use **Given/When/Then**: `Given {context}, when {action}, then {observable outcome}.`
- **2–6 criteria per story.** Fewer usually means the story is under-specified; more usually means it should be split.
- Every criterion is **observable and testable** — something a reviewer or automated test can check, not an intention.
- Every story that takes input includes **at least one unhappy path** (invalid input, unauthorized caller, downstream failure).
- No criterion may merely restate the story ("Given a user, when they search, then search works" says nothing).
- Performance and quality criteria carry **numbers**: "results within 200ms at p95", "≥ 85% Precision@10", never "fast" or "accurate".

## 5. Priority

Use one scheme — **P0 / P1 / P2**:

- **P0** — blocks the release; the feature is not viable without it.
- **P1** — should ship in this cycle; the feature is degraded without it.
- **P2** — nice to have; first candidate to cut.

(MoSCoW mapping for teams that use it: P0 = Must, P1 = Should, P2 = Could; Won't-haves are the PRD's non-goals.)

**Sanity rule:** if more than ~60% of stories are P0, prioritization failed — redo it until the P0 set is a genuine minimum.

## 6. Estimates

T-shirt sizes, relative effort — not commitments:

- **S** — half a day or less.
- **M** — up to two days.
- **L** — up to a week.
- **XL** — does not exist in a finished PRD. Split it (see SPIDR) before it enters the document.

## 7. Dependencies

- Record as `Depends on: US-xxx[, US-yyy]` on the story, and epic-level dependencies in the epics table.
- Keep the graph **shallow** — long chains serialize the team. Prefer restructuring stories over deep dependency trees.
- Milestone sequencing in PRD section 8 is the **topological order of this graph**: the MVP is the P0 stories of the epics nothing depends on. Derive phases from the graph; don't invent them separately.

## 8. Coverage checklist

Before finalizing the story set, check each of these and add the missing story if it applies:

- [ ] **Authn/authz** — a story defining who can access what, if any resource is protected.
- [ ] **Validation & error states** — every user input has validation and error behavior covered by some story's ACs.
- [ ] **UI states** — empty, loading, and failure states for every new surface.
- [ ] **Accessibility** — keyboard navigation, ARIA, contrast for new UI.
- [ ] **Telemetry** — a story emitting the events the Overview's success criteria are measured by. A PRD whose metrics nothing measures is decorative.
- [ ] **Migration/backfill** — if the data model changes, a story for existing data.
- [ ] **Feature flag & rollback** — if the change is risky, a story wiring the flag and the back-out path.

Do **not** add documentation stories — in repositories using this configuration, docs and the changelog are handled by the SE Technical Writer flow after implementation.

## 9. Micro-example

```markdown
| ID | Epic | Goal | Priority | Estimate | Depends on |
| --- | --- | --- | --- | --- | --- |
| EP-1 | Saved searches | Users can save and re-run searches | P0 | M | — |

### EP-1: Saved searches

#### US-101: Save the current search

- **Story**: As a signed-in user, I want to save my current search so that I can re-run it later.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Acceptance criteria**:
  - Given a signed-in user with an active search, when they select "Save search" and enter a name, then the search appears in their saved list within 1s.
  - Given a name longer than 100 characters, when they save, then a validation message shows and nothing is saved.
  - Given an anonymous user, when they open the search menu, then "Save search" is not offered.

#### US-102: Re-run a saved search

- **Story**: As a signed-in user, I want to run a saved search from my list so that I don't rebuild filters by hand.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-101
- **Acceptance criteria**:
  - Given a saved search, when the user selects it, then the same filters apply and results load within 500ms at p95.
  - Given a saved search referencing a filter that no longer exists, when the user selects it, then the remaining filters apply and a notice names the dropped filter.
```
