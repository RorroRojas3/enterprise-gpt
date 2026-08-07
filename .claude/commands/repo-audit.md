---
description: Audit cross-harness parity (.claude/ vs .github/) and config cost hygiene; report findings and optionally fix mechanical drift.
argument-hint: "[--fix] [--strict]"
allowed-tools: Bash(node:*), Bash(git status:*), Bash(git diff:*), Bash(cp:*), Read, Edit, Glob, Grep
---

Audit the two harness trees against each other: mirrored skills, rule/instruction twins, agent twins and their model parity, registry listings, and cost hygiene.

The deterministic work lives in `scripts/repo-audit.mjs` — it compares content hashes, frontmatter, and registries, and prints a JSON report. Your job is only to act on what it flags.

## 1. Run the audit

```bash
node scripts/repo-audit.mjs --json
```

Append `--strict` if the user passed it (warnings then also fail).

## 2. Branch on the exit code — it is the contract

- **`0` — clean.** Print the one-line summary (it already carries the warning count) and **stop immediately**. Do not read files, do not "double-check" the trees — the hashes already did. This is the routine path and it should cost close to nothing.
- **`1` — the script itself failed** (unreadable tree, parse crash). Report the error and stop. Fix nothing on the basis of a failed check.
- **`10` — findings.** Continue below.

## 3. Read only what the report names

`affectedPaths` is your entire read-set — do not audit beyond it. Per finding:

- **`skills-mirror/differs`** — decide the canonical side: `git status` / `git diff` shows which side carries the uncommitted (newer) change; `cp` it over the other. If both sides are committed and differ, do not guess — present both versions to the user.
- **`skills-mirror/missing-*`** — `cp` the file into the tree that lacks it (or confirm with the user that it should be deleted from both).
- **`rules-parity/body-drift`** — reconcile the wording semantically and apply the same sentence to **both** files. The script's header documents which cross-reference spellings are intentional and already mapped.
- **`rules-parity/glob-mismatch`** — make the sets equal; the Claude `paths:` list is the canonical order.
- **`agent-twins/model-parity`** — align the model to the parity table in `scripts/repo-audit.mjs`, or — only with the user's explicit approval — record a documented override in its `modelParityOverrides` (and the README convention note).
- **`registry/*`** — add or remove the registry line; never invent a description — lift it from the twin.
- **`cost-hygiene/*`** (warnings) — propose narrowed globs or trims in your report; **never** apply them under `--fix`. They change behavior, not just parity, so a human decides.

## 4. Report, or fix mechanically

- **Default (no `--fix`):** output a findings table — severity, finding, proposed exact edit — and stop. Edit nothing.
- **With `--fix`:** apply **only the mechanical repairs** above (mirror copies, glob-set sync, registry lines, wording drift with a clear canonical side). Everything judgment-shaped stays in the report.

## 5. Confirm

Re-run `node scripts/repo-audit.mjs`. The errors you fixed must be gone; explain any finding you deliberately left.

## 6. Leave the diff for review

**Do not commit, do not push, do not open a PR.** This repo *is* the guidance, and a silently wrong "repair" would surface weeks later as drifted standards. `git diff` is the review surface and `git checkout --` is the undo; a human decides.
