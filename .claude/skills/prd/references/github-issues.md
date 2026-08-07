# Creating GitHub issues from a PRD

Turn PRD section 7 (epics & user stories) into GitHub issues with the `gh` CLI. **Gate: only do this after the user has explicitly confirmed issue creation** — presenting the PRD is never implicit approval.

## Preflight

```bash
gh auth status          # authenticated?
gh repo view            # correct repository?
gh label list           # which labels already exist?
```

Create any missing labels first (idempotent — `gh label create` fails harmlessly if the label exists; `--force` updates it):

```bash
gh label create user-story --color 0E8A16 --description "A user story from a PRD" --force
gh label create epic --color 5319E7 --description "An epic grouping user stories" --force
gh label create P0 --color B60205 --force
gh label create P1 --color D93F0B --force
gh label create P2 --color FBCA04 --force
gh label create <feature-slug> --color C5DEF5 --description "Stories for <feature title>" --force
```

## Mapping

| PRD element | GitHub issue |
| --- | --- |
| Epic `EP-n` | Parent issue titled `EP-1: {epic name}`, labels `epic`, `<feature-slug>`; body gets a task list of its stories after they exist |
| Story `US-xxx` | One issue titled `US-101: {story title}`, labels `user-story`, `P0/P1/P2`, `<feature-slug>` |

Story issue body: the story statement, the acceptance criteria as a `- [ ]` checklist, priority and estimate, `Part of #<epic issue number>`, and `Blocked by #<n>` per dependency. Keep PRD IDs in titles — they are the traceability link back to the document.

## Order of operations

1. **Idempotency check** — never duplicate on a re-run:

   ```bash
   gh issue list --search "US-101 in:title" --state all --json number,title
   ```

   Skip any ID that already has an issue and note it in the final report.

2. **Create epic issues first**, capturing numbers:

   ```bash
   ep1=$(gh issue create --title "EP-1: Saved searches" --label epic,saved-searches \
     --body-file - <<'EOF' | grep -o '[0-9]*$'
   Goal: Users can save and re-run searches.
   Priority: P0 · Estimate: M

   From PRD: docs/prd/saved-searches.md
   Stories: (task list added after story issues are created)
   EOF
   )
   ```

3. **Create story issues**, referencing the epic:

   ```bash
   gh issue create --title "US-101: Save the current search" --label user-story,P0,saved-searches \
     --body-file - <<EOF
   As a signed-in user, I want to save my current search so that I can re-run it later.

   **Priority**: P0 · **Estimate**: S
   Part of #$ep1

   ## Acceptance criteria
   - [ ] Given a signed-in user with an active search, when they select "Save search" and enter a name, then the search appears in their saved list within 1s.
   - [ ] Given a name longer than 100 characters, when they save, then a validation message shows and nothing is saved.
   EOF
   ```

   For a dependency, add a `Blocked by #<n>` line using the number captured for that story.

4. **Edit each epic's body** to add its task list now that story numbers exist:

   ```bash
   gh issue edit "$ep1" --body-file - <<EOF
   Goal: Users can save and re-run searches.
   Priority: P0 · Estimate: M

   From PRD: docs/prd/saved-searches.md

   ## Stories
   - [ ] #12
   - [ ] #13
   EOF
   ```

## Report

Finish with a table mapping every PRD ID to its issue URL, plus any IDs skipped (already existed) or failed (with the error):

| PRD ID | Issue |
| --- | --- |
| EP-1 | <https://github.com/owner/repo/issues/11> |
| US-101 | <https://github.com/owner/repo/issues/12> |
