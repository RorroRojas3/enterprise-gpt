---
name: prd
description: "Write Product Requirements Documents and break features into epics and user stories with acceptance criteria, priorities, estimates, and dependencies. Use when asked to write a PRD, spec out or scope a feature, define requirements, build a backlog, break a feature into stories, or turn requirements into GitHub issues."
license: MIT
---

# Product Requirements Document (PRD)

## Overview

Produce production-grade PRDs that bridge business vision and technical execution: a clear problem statement, measurable success criteria, and a feature broken down into epics and user stories a team can actually pick up.

## When to use

- Starting a new product or feature development cycle
- Translating a vague idea into a concrete, testable specification
- Breaking a feature into epics and user stories with acceptance criteria
- Building a backlog or creating GitHub issues from requirements
- Stakeholders need a unified "source of truth" for project scope

**Not for implementation plans.** A PRD says *what* to build and *why*. How to build it — file-level steps, architecture choices, task sequencing — belongs to the planning flow (plan mode, Planner Expert), which should reference the PRD's story IDs.

## Workflow

### 1. Discover

Identify what you don't know before writing anything. The seven gaps that matter:

1. **Problem & why now** — what pain, and what makes it urgent?
2. **Users** — who is this for; which roles or personas?
3. **Scope boundary** — what is explicitly in and out?
4. **Success metrics** — how will anyone know it worked?
5. **Constraints** — stack, budget, deadline, compliance?
6. **Auth model** — are there protected resources; who can do what?
7. **Existing code touchpoints** — what does this integrate with or replace?

Close the gaps **per your operating mode**: an interactive session asks the user directly (3–5 questions, bulleted, conversational); an autonomous agent returns the questions to its caller or proceeds and records each guess in the PRD's *Assumptions & open questions* section. Never silently invent an answer.

### 2. Analyze

Ground the PRD in the real codebase before drafting. Find the current architecture, similar existing features, the auth mechanism, and telemetry conventions — the *Technical considerations* and *Epics & user stories* sections must name real integration points, not hypothetical ones.

### 3. Draft

Follow the canonical schema in `references/prd-template.md` — do not invent a different outline. Decompose the feature using the rules in `references/story-breakdown.md`. Iterate: present the draft and ask for feedback on specific sections.

## Quality bar

Use concrete, measurable criteria. Avoid "fast", "easy", or "intuitive".

```diff
# Vague (BAD)
- The search should be fast and return relevant results.
- The UI must look modern and be easy to use.

# Concrete (GOOD)
+ The search must return results within 200ms for a 10k record dataset.
+ The search algorithm must achieve >= 85% Precision@10 in benchmark evals.
+ The UI must achieve a 100% Lighthouse Accessibility score.
```

- **No fabricated constraints.** If the user didn't specify a stack, deadline, or metric target, ask, or mark it `TBD` and record it under *Assumptions & open questions*.
- **Every story is testable.** Each acceptance criterion is observable behavior someone could verify.
- **Traceability.** Every functional requirement maps to at least one story; IDs (`FR-n`, `EP-n`, `US-xxx`) are stable so plans, commits, and issues can reference them.

## Output

Write the PRD to `docs/prd/<feature-slug>.md` (create the folder if needed) unless the user specifies another location.

## References

Read only what the task calls for:

| Reference | Read when |
| --- | --- |
| `references/prd-template.md` | Structuring or drafting the document — the canonical schema and final checklist |
| `references/story-breakdown.md` | Decomposing a feature into epics and user stories: slicing, INVEST, acceptance criteria, priorities, estimates, dependencies |
| `references/github-issues.md` | Only after the user has explicitly confirmed creating GitHub issues from the PRD |
