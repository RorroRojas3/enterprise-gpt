# Canonical PRD schema

This is the single source of truth for PRD structure. Follow it exactly; add subheadings within a section when useful, but do not add, remove, or reorder top-level sections. Sections marked *conditional* are omitted entirely when they don't apply — never left in as empty stubs.

## Formatting rules

- Title case for the document title only (`PRD: {Project title}`); sentence case for every other heading.
- Pure Markdown, consistent numbering, no horizontal rules, no disclaimers or footers.
- Refer to the work conversationally ("the project", "this feature"), and fix grammar or casing errors carried over from the request.

## ID scheme

Stable IDs make the PRD traceable from plan steps, commits, and GitHub issues:

- `FR-n` — functional requirements (`FR-1`, `FR-2`, …)
- `EP-n` — epics (`EP-1`, `EP-2`, …)
- `US-<epic><nn>` — user stories; the first digit(s) are the epic number (`US-101` = epic 1, story 01; `US-204` = epic 2, story 04)

## The nine sections

### 1. Overview

- **Problem**: 1–2 sentences on the pain point and why now.
- **Solution**: 1–2 sentences on the proposed fix.
- **Success criteria**: 3–5 bullets; each states *metric + target + how it is measured* (e.g. "reduce median search time from 8s to 4s, measured via the existing `search.completed` telemetry event"). Only very large, product-scale PRDs should split metrics into separate user/business/technical subsections.
- Optionally close with a short narrative paragraph describing the user's journey end to end.

### 2. Goals & non-goals

- **Goals**: bullet list — business and user goals together, or split if both are substantial.
- **Non-goals**: bullet list of what is deliberately out of scope. Non-goals protect the timeline; be explicit.

### 3. Users & access

- **Personas**: `**{persona name}**: {one-line description}` per key user type.
- **Role-based access** *(conditional — only when authn/authz applies)*: `**{role}**: {what they can do}` per role.

### 4. Functional requirements

A table; every requirement must map to at least one story in section 7 (checked in the final checklist):

| ID | Requirement | Priority | Epic(s) |
| --- | --- | --- | --- |
| FR-1 | {requirement} | P0 | EP-1 |

### 5. User experience *(conditional — user-facing UI only)*

- **Entry points & first-time flow**: how users reach the feature.
- **Core experience**: the main flow, step by step.
- **Edge cases & UI states**: empty, loading, error, and failure states.
- **UI/UX highlights**: accessibility, responsiveness, design-system notes.

### 6. Technical considerations

- **Integration points**: name the real projects, services, and APIs found during codebase analysis — not hypothetical ones.
- **Data storage & privacy**: what is stored where; PII handling; compliance.
- **Security**: authn/authz approach, secret handling, input validation.
- **Scalability & performance**: expected load, targets, known bottlenecks.
- **AI system requirements** *(conditional — features involving models or agents)*: tools/APIs the system needs, evaluation strategy, and pass thresholds (e.g. "≥ 90% of the 50-question benchmark must match expected citations").

### 7. Epics & user stories

First the epics table:

| ID | Epic | Goal | Priority | Estimate | Depends on |
| --- | --- | --- | --- | --- | --- |
| EP-1 | {name} | {one-line goal} | P0 | M | — |

Then the stories, grouped under an `### EP-n: {epic name}` heading each, using this block per story:

```markdown
#### US-101: {story title}

- **Story**: As a {user}, I want to {action} so that {benefit}.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Acceptance criteria**:
  - Given {context}, when {action}, then {observable outcome}.
  - Given {invalid input}, when {action}, then {error behavior}.
```

Decomposition rules — slicing, INVEST, AC quality, priorities, estimates, dependencies — live in `story-breakdown.md`; read it before writing this section.

### 8. Milestones & rollout

- **Phases**: derive from the epic dependency graph — the MVP is the P0 stories of the epics nothing depends on; later phases follow topological order. Per phase: name, contents (epic/story IDs), and a relative time estimate.
- **Risks & mitigations**: technical risks (latency, cost, dependency failures) with a mitigation each.
- **Rollout & rollback**: feature flags, staged rollout, and how to back out.
- Team size/composition is optional — omit it rather than fabricate one.

### 9. Assumptions & open questions

Always present, even if it says "None."

- **Assumptions**: every guess made where the requester didn't specify — one bullet each, phrased so a reviewer can veto it.
- **Open questions**: unresolved items that don't block the current draft, each with who should answer it.

## Final checklist

Verify before delivering the PRD:

- [ ] Every FR in section 4 maps to at least one story in section 7.
- [ ] Every story is testable — each AC is observable behavior.
- [ ] An authn/authz story exists if the feature touches any protected resource.
- [ ] No vague adjectives ("fast", "easy", "intuitive") — numbers and measurable outcomes instead.
- [ ] No XL stories — anything estimated beyond L was split before it entered the document.
- [ ] Success criteria each name a metric, a target, and a measurement source.
- [ ] Assumptions & open questions is populated (or explicitly "None").
- [ ] Section order and IDs match this template.
