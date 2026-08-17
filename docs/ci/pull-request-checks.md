# Pull-request checks

The two GitHub Actions workflows that decide whether a pull request is safe to merge: `.github/workflows/api-ci.yml` — the repository's **first .NET CI** — and `.github/workflows/ui-ci.yml`, rewritten to the same shape so both can be required at the same time.

Audience: whoever configures branch protection (read §4 before you do, because requiring the wrong subset of checks is worse than requiring none), and any contributor adding a job, a gate or a path to either workflow.

The thing worth reading even if you never touch a workflow file: **both workflows dropped their `paths:` filters**, and the filter moved into a job. That is not a style change. It is the difference between a check that _can_ be required and one that can only ever sit Pending — the open question recorded in [the rebuild PRD](../prd/enterprise-ui-rebuild.md), now closed (§3).

## 1. Overview

| Workflow         | Jobs (and their check names)                                      | Runs                                                                                                                            |
| ---------------- | ----------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------- |
| **`api-ci.yml`** | `Detect API changes` → `Unit tests`, `Integration tests`          | 1152 unit tests with no Docker; 250 integration tests against real containers                                                   |
| **`ui-ci.yml`**  | `Detect UI changes` → `Lint, test, build`, `Andes contract drift` | Prettier, ESLint + the three Node checks, Vitest, the production build and its bundle gates; and the US-107 contract comparison |

Both fire on `push` to `master`, on `pull_request` against `master`, and on `merge_group` — the last so the checks still report if a merge queue is ever enabled. Neither carries a `paths:` filter.

Within each workflow the two real jobs run **in parallel**, both gated on the cheap `changes` job. They are separate rather than sequential so that a compile error or a lint failure is reported without waiting on a 1.5 GB image pull or a .NET restore, and so that the two signals can be required independently.

## 2. Quick start

Reproduce any gate locally, in the same order CI runs it:

```bash
# in enterprise-gpt-api/  — what "Unit tests" runs
dotnet test tests/Enterprise.Gpt.Unit.Test/Enterprise.Gpt.Unit.Test.csproj --filter "Category!=Integration"

# in enterprise-gpt-api/  — what "Integration tests" runs (needs Docker)
dotnet test tests/Enterprise.Gpt.Integration.Test/Enterprise.Gpt.Integration.Test.csproj --filter "Category=Integration"

# in enterprise-gpt-ui/   — what "Lint, test, build" runs
npm ci && npm run format:check && npm run lint && npm run test:ci && npm run build

# in enterprise-gpt-ui/   — what "Andes contract drift" runs, after a restore of Enterprise.Gpt.Service
npm run check:contract
```

`npm run test:ci` is `npm test` plus a JUnit report at `enterprise-gpt-ui/test-results/ui.junit.xml`, which CI uploads as an artifact; `test-results/` is gitignored. It has its own `pretest:ci` so the generated fonts and icons are built before it, exactly as `pretest` does for `npm test`.

## 3. Why the `paths:` filters are gone

GitHub has **two** "skipped" behaviours and they are not the same. The whole design of both files follows from the difference, which is documented under [Handling skipped but required checks](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/troubleshooting-required-status-checks#handling-skipped-but-required-checks):

| Skipped by                  | What the check reports         | Consequence                                                                                         |
| --------------------------- | ------------------------------ | --------------------------------------------------------------------------------------------------- |
| A **path or branch filter** | Nothing — it stays **Pending** | A filtered workflow can never be a required check: every unrelated pull request becomes unmergeable |
| A **job-level `if:`**       | **Success**                    | A conditionally skipped job is safe to require                                                      |

`ui-ci.yml` used to filter on `enterprise-gpt-ui/**` and three other paths, which is why its checks were documented as "must not be made required in branch protection as written". Removing the filter is the answer, and the filter did not disappear — it moved into a `changes` job whose outputs the real jobs condition on.

**The condition is written fail-open, and that is the most consequential line in either file:**

```yaml
if: ${{ !cancelled() && needs.changes.outputs.api != 'false' }}
```

Three things are load-bearing:

- **`!= 'false'`, never `== 'true'`.** A job whose `needs` dependency _failed_ is skipped — and a skipped job reports Success. So `== 'true'` would hand out a green required check over completely untested code the moment the filter job broke for any reason at all. `!= 'false'` means an absent answer runs the tests.
- **`!cancelled()`** keeps the job alive when `changes` failed, rather than letting it be skipped into that false pass.
- **`!cancelled()` rather than `always()`**, so `cancel-in-progress` still stops a superseded run instead of forcing every job to completion.

The cost of dropping the filter is that every pull request now spends a runner-minute on the `changes` job. That is the price of a signal that can be required.

## 4. Branch protection — require all three names per workflow

**List every job, not only the gates:**

- `Detect API changes`, `Unit tests`, `Integration tests`
- `Detect UI changes`, `Lint, test, build`, `Andes contract drift`

Requiring only the gates leaves a real window. The `changes` job writes `api=false` to `$GITHUB_OUTPUT` and _then_ does something else; if it dies after that write — a runner eviction, a `set -euo pipefail` trip in a later line, a timeout — its dependents read `false`, skip, and report Success over code nobody tested. The `changes` job is part of the trusted computing base for a merge decision, so it has to be one of the things that must be green.

This is a repository setting rather than a file: **nothing in the repository can enforce it**, which is why it is written down here.

## 5. The `changes` job

It answers one question — did anything this workflow tests actually move? — and nothing else.

```bash
# A push to master, or a merge_group run, always runs everything.
if [ "$EVENT_NAME" != "pull_request" ]; then echo "api=true" >> "$GITHUB_OUTPUT"; exit 0; fi

changed=$(git diff --name-only HEAD^1 HEAD -- enterprise-gpt-api .github/workflows/api-ci.yml)
```

Four decisions in that script:

- **`HEAD^1`, not the event payload's `base.sha`.** On a pull request, `HEAD` is the merge commit, so its first parent _is_ the base the change will land on. The payload's value goes stale the moment `master` advances and the merge ref is regenerated, which silently widens the diff. `fetch-depth: 2` is exactly what that comparison needs.
- **A non-pull-request event runs everything.** Two pull requests approved against different bases can both merge and leave `master` unvalidated, so a push to `master` re-runs the lot.
- **The allowlists are hand-maintained.** A root-level file that comes to affect a build — a `global.json`, `Directory.Build.props`, an `.nvmrc`, a Prettier config — must be added when it is introduced. None exists today. A missing entry **under-runs** the gates rather than over-running them, which is the dangerous direction; that is the one thing to check when adding a file outside the two project trees.
- **The event name reaches the script through `env:`, never through `${{ }}` inside `run:`.** Interpolation splices a value into the shell before bash sees a quote. Nothing author-controlled is used here today; the habit is the protection.

`ui-ci.yml` emits **two** outputs rather than one, because its two gates read different inputs: `npm run lint` runs `check:tokens`, which diffs the app's tokens against `docs/design/project/theme.css`; `check:contract` walks every `.csproj` under the API and compares the vendored contract against the package they reference. Neither is in the other's input set, and one shared boolean would run the whole Angular pipeline for a backend dependency bump — and a .NET restore for a change to a colour.

## 6. API CI

### 6.1 `Unit tests`

Restore, build and test **the unit test project**, not the solution. Restore is transitive over project references, and that project reaches Api, Service, Repository, Dto and Entity (and Common through Dto) — so all six product projects are already compiled. Building the solution would add only the integration test assembly: 250 tests this job never runs and the job beside it compiles anyway.

It runs first in wall-clock terms because it needs no Docker: a compile error should be reported long before a container has finished booting.

### 6.2 `Integration tests`

`WebApplicationFactory<Program>` plus Testcontainers, so two images are pulled: **SQL Server 2025** (~1.5 GB — `UseCompatibilityLevel(170)` means no older engine will do) and the **Cosmos DB vNext emulator**, used only by the transcript-store tests. No secrets and no Azure credentials are involved: `CustomWebApplicationFactory` supplies well-formed fake settings inline, forces the `Testing` environment (which skips `Database.Migrate()`), and swaps Graph, blob storage, embeddings and the transcript store for fakes.

Two settings are deliberate:

- **`runs-on: ubuntu-24.04`, pinned rather than `ubuntu-latest`.** This is the one job that depends on the runner image's preinstalled Docker and on two large images pulling cleanly, so a runner-image migration should arrive as a deliberate pull request rather than as a surprise red `master`.
- **`timeout-minutes: 30`** — the pull plus the suite with room to spare. If it is ever actually reached, a container is wedged rather than slow.

If the Cosmos emulator proves flaky, the fix is a fourth job filtered with `--filter "Category=Integration&FullyQualifiedName!~TranscriptStore"` — **not** a retry loop, which would only hide which of the two containers was at fault.

## 7. UI CI

`Lint, test, build` runs `npm ci`, then `format:check`, `lint`, `test:ci`, the results upload, and `build` — in that order, so a formatting or lint failure is reported in seconds rather than after a production build. The checkout is deliberately **full, never sparse**: `check:tokens` reads `../docs/design/project/theme.css` from outside the UI tree. Node comes from `node-version-file: enterprise-gpt-ui/package.json`, so the runner follows the `engines.node` range rather than a second copy of it in YAML.

`Andes contract drift` is isolated because it is the only thing here that needs the .NET toolchain, and ~60s of restore does not belong on the critical path of every UI pull request. It restores `Enterprise.Gpt.Service` — the lighter of the two projects that reference `Andes.Extensions.AI.UI` — and the invariant it depends on is worth knowing: **some** project restored there must still reference the package, or the check fails with "not on this machine" for a reason nobody will connect to a `.csproj` edit. Restore `Enterprise.Gpt.Api` instead if `Service` ever drops it.

**Neither .NET job caches NuGet.** Restoring the graph fills `~/.nuget/packages` with hundreds of megabytes, which would compete for the repository's 10 GB LRU cache budget against the npm cache — and the npm cache is on the hot path of every UI pull request. (`setup-dotnet`'s own `cache:` is a separate non-option: it requires `packages.lock.json` files this repository does not have.) Re-measure before adding one back.

## 8. Security posture

- **`permissions: contents: read`** at the workflow level in both files. No job writes anything back to the repository, and none needs `checks: write` or `pull-requests: write`: results travel as **artifacts**, not as a bot comment, which is what keeps these safe to run on a fork's pull request.
- **`persist-credentials: false`** on every checkout, so no token is left in `.git/config` for a later step to reach.
- **Every third-party action is pinned by commit SHA** with the version in a trailing comment. `.github/dependabot.yml` keeps them current on a monthly schedule (§10).
- **No secrets are referenced anywhere in either workflow.**
- **Values reach `run:` through `env:`**, never through `${{ }}` interpolation (§5).

## 9. Concurrency and cost

```yaml
group: ${{ github.workflow }}-${{ github.event.pull_request.number || github.sha }}
cancel-in-progress: ${{ github.event_name == 'pull_request' }}
```

Pull requests collapse onto the pull-request number, so a force-push cancels the run it superseded. Every other run gets its own group **from its SHA**, and that detail matters: keying `master` on the ref would serialise merges into one group where a _pending_ run is cancelled when the next one queues — three merges landing together would leave the middle commit with no result at all, which is the opposite of a durable `master` signal.

Test results upload under `if: ${{ !cancelled() }}` rather than `always()`, because a red run is exactly when a `.trx` or a JUnit report is worth reading, while a cancelled one has nothing worth keeping. The UI upload uses `if-no-files-found: warn` rather than `ignore`: Vitest's JUnit reporter opens the file in `onInit`, so it exists even when the run dies mid-suite — an empty path therefore means the reporter wiring broke, which is the one case worth an annotation.

## 10. Known limits, recorded rather than hidden

| Limit                                                                                                                                  | Why it is acceptable for now                                                                                                                                                            |
| -------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **The Testcontainers images use mutable tags** — `mcr.microsoft.com/mssql/server:2025-latest` and the Cosmos emulator's `vnext-latest` | An upstream push can turn `master` red with no change in this repository. Pinning by digest is a change to the **test fixtures**, not to a workflow file, and is worth doing on its own |
| **`dependabot.yml` tracks only the `github-actions` ecosystem**                                                                        | Deliberate. `nuget` and `npm` are adjacent to this work rather than part of it, and both would open a much larger update surface than SHA-pinned actions                                |
| **The `changes` allowlists are hand-maintained** (§5)                                                                                  | Nothing generates them, and a missing entry under-runs the gates. Check them when adding a file outside the two project trees                                                           |
| **Every pull request pays for a `changes` job**                                                                                        | The price of a check that can be required (§3)                                                                                                                                          |

## 11. Troubleshooting

| Symptom                                                                          | Cause                                                                                                                                             |
| -------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------- |
| A required check sits **Pending** forever and the pull request cannot merge      | A `paths:`/`branches:` filter was reintroduced at the workflow level. Filter inside a job instead (§3)                                            |
| A pull request goes **green without running any test**                           | The job condition was written `== 'true'`, or `!cancelled()` was dropped, so a failed `changes` job skipped its dependents into a false pass (§3) |
| **Only the gates are required**, and a broken `changes` job merges untested code | Branch protection is missing the `Detect …` check names (§4)                                                                                      |
| The gates do not run for a change that clearly affects them                      | A new root-level file is missing from the `changes` allowlist (§5)                                                                                |
| `check:tokens` fails on CI with a missing file                                   | The checkout was made sparse or shallow. It reads `docs/design/project/theme.css` from outside `enterprise-gpt-ui/` (§7)                          |
| `check:contract` reports the package is "not on this machine"                    | The restored project no longer references `Andes.Extensions.AI.UI` (§7)                                                                           |
| `Integration tests` reddens with no change in this repository                    | An upstream push to a `*-latest` image tag (§10)                                                                                                  |
| Three merges land together and the middle commit has no result                   | The concurrency group was keyed on the ref rather than the SHA (§9)                                                                               |

## 12. Key files

| Concern                         | File                                                                                                                                                                             |
| ------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| API workflow                    | [`.github/workflows/api-ci.yml`](../../.github/workflows/api-ci.yml)                                                                                                             |
| UI workflow                     | [`.github/workflows/ui-ci.yml`](../../.github/workflows/ui-ci.yml)                                                                                                               |
| Action version updates          | [`.github/dependabot.yml`](../../.github/dependabot.yml)                                                                                                                         |
| The UI's CI test script         | [`enterprise-gpt-ui/package.json`](../../enterprise-gpt-ui/package.json) — `test:ci`, `pretest:ci`                                                                               |
| Integration test infrastructure | [`tests/Enterprise.Gpt.Integration.Test/TestInfrastructure/`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/TestInfrastructure/) — the two image tags live here |
| Related reference               | [Frontend Foundation](../ui/frontend-foundation.md) for the UI gates themselves, [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md)                                    |
