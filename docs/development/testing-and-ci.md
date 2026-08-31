# Testing and CI

The test projects, the gate scripts, and the two workflows that run them.

## Backend tests

xUnit **v3**, NSubstitute for isolation, plain `Assert` (no FluentAssertions — v8 and later are
commercially licensed). Tests are named `MethodName_Scenario_ExpectedBehavior` and follow
Arrange-Act-Assert without `// Arrange` comments.

### `Enterprise.Gpt.Unit.Test`

Services that take `EnterpriseGptDbContext` run on **SQLite in-memory** via `SqliteDbContextFixture`
— chosen over the EF in-memory provider because `ExecuteUpdateAsync` is relational. A model
customizer neutralizes the rowversion and vector columns SQLite cannot handle. Each test class news
up its own fixture rather than sharing one through `IClassFixture`.

The project links the real `Enterprise.Gpt.Api/appsettings.json` in, so configuration tests bind the
shipped file rather than a copy.

`InternalsVisibleTo` on `Api` and `Service` is what lets these tests call the internal endpoint
handlers directly, without a host.

### `Enterprise.Gpt.Integration.Test`

`WebApplicationFactory<Program>` plus Testcontainers. Needs Docker. Every test carries
`[Collection("Integration")]` and `[Trait("Category", "Integration")]`.

- `IntegrationTestFixture` owns the SQL Server container and the factory.
- `CustomWebApplicationFactory` runs the host in the **`Testing`** environment, which is what skips
  `Database.Migrate()` and the Cosmos bootstrap, and swaps Graph, blob storage, embeddings and the
  transcript store for fakes.
- `TestAuthHandler` authenticates from an `X-Test-User` header.
- `CosmosEmulatorFixture` is a **separate collection** for transcript tests.

Two images are pulled: SQL Server 2025 (~1.5 GB — `UseCompatibilityLevel(170)` means no older engine
will do) and the Cosmos vNext emulator. No secrets and no Azure credentials are involved.

```bash
# in enterprise-gpt-api/
dotnet test                                     # everything; integration needs Docker
dotnet test --filter "Category!=Integration"    # unit only
```

## Frontend tests

Vitest through the Angular `@angular/build:unit-test` builder — there is no separate
`vitest.config.ts`.

| Target | Environment | Includes |
| --- | --- | --- |
| `test` | jsdom | everything except `*.a11y.spec.ts` |
| `test-a11y` | **real headless Chromium**, 1280x900 | only `*.a11y.spec.ts` |

`src/testing/test-providers.ts` is installed into every unit test: `provideZonelessChangeDetection()`
and `provideCheckNoChangesConfig({ exhaustive: true })`, which makes a view reading mutated
plain-object state fail immediately instead of only being visible on screen.

`src/testing/setup-dom.ts` patches what jsdom lacks — `matchMedia`, `IntersectionObserver`,
`ResizeObserver` — and resets media queries after each test so the starting viewport is a fact
rather than a sharding race.

**The a11y target is a real browser, not jsdom**, because roughly half of what axe checks is
computed style — contrast, visibility, target size — which jsdom reports as `incomplete` and passes.
That reads as a clean bill of health and is not one. Its `setupFiles` is deliberately **empty**: the
jsdom fakes would shadow Chromium's real `matchMedia` and observers and answer `false` to every media
query.

`a11y-coverage.spec.ts` runs in the jsdom suite and asserts the audit covers every shipped route, so
a new route cannot be added without being audited. It reads the route list from a separate module so
it does not pull the 600 kB auditor into the jsdom run.

```bash
# in enterprise-gpt-ui/
npm test          # Vitest, single run
npm run test:a11y # the axe target — not included in npm test
```

## The gate scripts

`npm run lint` is ESLint plus three Node checks; `npm run build` adds a fourth.

| Script | Asserts |
| --- | --- |
| `check:icons` | Every `bi-*` glyph named in a template or `.ts` exists in the checked-in manifest. Covers what `strictTemplates` cannot see — raw `class="bi bi-..."` in a template, and `bi-*` literals flowing into class bindings. |
| `check:forbidden` | A text backstop over `src/` for `bypassSecurityTrustHtml`, Google Fonts and CDN origins, Prism stylesheets, direct `localStorage.setItem`, a fifth keyframe, and a hardcoded application breakpoint. A **text** scan on purpose: it sees a runtime-assembled property name and an inline `eslint-disable`, which the structural rule cannot. |
| `check:tokens` | The pre-paint colours duplicated into `index.html` match the tokens; the code-surface and focus-ring pairs clear their WCAG minimum in both themes; the two copies of the breakpoint table agree. |
| `check-initial-chunk.mjs` | Reads the esbuild metafile and fails if a must-stay-lazy library landed in the initial graph — **naming the library and the import path that put it there**. A bundle budget provably cannot do this: a chunk from `import('mermaid')` has neither `initialFiles` nor an `entryPoint`, so it sums to zero bytes. |
| `check:contract` | The vendored Andes contract is byte-identical to the installed NuGet package below a sentinel, its pinned version matches the API's `PackageReference`, and nobody has hand-written a duplicate of its public symbols. Not in `lint` — it needs the API's NuGet cache. |

ESLint itself carries the layer bans (`no-restricted-imports`), `@ngrx/eslint-plugin` with
`signalsTypeChecked`, and a `no-restricted-syntax` rule banning `bypassSecurityTrustHtml`.
Unused-symbol detection is **tsc's**, not ESLint's, because tsc counts a `{@link}` as a use.

Prettier is separate: `npm run lint` does not run it, so run `npm run format` before handing work
back.

## The workflows

Two files, same shape: a `changes` job, then dependent jobs.

**`api-ci.yml`** — `changes`, then `unit` (no Docker) and `integration`.

- Unit restores and tests **the unit test project**, not the solution. Restore is transitive over
  project references and that project reaches all six product projects, so building the solution
  would add only the integration assembly the job beside it compiles anyway.
- Unit runs first in wall-clock terms because it needs no Docker: a compile error should be reported
  long before a container has booted.
- Integration pins `runs-on: ubuntu-24.04` rather than `ubuntu-latest`, because it is the one job
  depending on the runner image's preinstalled Docker and two large images pulling cleanly — a
  runner-image migration should arrive as a deliberate pull request rather than a surprise red
  `master`.

**`ui-ci.yml`** — `changes`, then `build` (the UI gates) and `contract` (one .NET restore).

`build` runs `npm ci`, `format:check`, `lint`, `test:ci`, the results upload, `build`, a Chromium
install, then `test:a11y`. That order is the job's one rule: **the cheapest gates report first**, so
a formatting failure is reported in seconds rather than after a production build, and the
accessibility audit is last because it runs a second Angular build and launches a browser.

Environmental invariants, in the order they bite:

- **Generated assets must exist before either test run.** `pretest:ci` and `pretest:a11y` both run
  `npm run assets`; a target added without its `pre*` script fails on a missing icon sprite rather
  than on whatever it was written to check.
- **The job needs a browser.** `npx --no playwright install --with-deps chromium`. `--with-deps`
  installs the system libraries Chromium needs on a bare image, and `--no` makes a missing local
  binary a loud failure instead of npx silently fetching an unpinned `playwright` from the registry.

`contract` is isolated because it is the only thing here needing the .NET toolchain, and ~60s of
restore does not belong on the critical path of every UI pull request. It restores
`Enterprise.Gpt.Service`, and the invariant is worth knowing: **some** project restored there must
still reference the Andes package, or the check fails with "not on this machine" for a reason nobody
will connect to a `.csproj` edit.

Neither .NET job caches NuGet. Restoring the graph fills `~/.nuget/packages` with hundreds of
megabytes, competing for the repository's cache budget against the npm cache — which is on the hot
path of every UI pull request.

### Why the path filters live in a job

**Neither workflow carries a `paths:` filter.** A workflow skipped by a path filter never reports a
check at all — its status stays Pending, so it can never be a required check without making every
unrelated pull request unmergeable. A **job** skipped by a conditional reports Success and does not
block merging.

So the filter lives in the `changes` job, and every dependent's `if:` is written **fail-open**:

```yaml
if: ${{ !cancelled() && needs.changes.outputs.build != 'false' }}
```

`!= 'false'` means an absent answer runs the gates, and `!cancelled()` keeps the job alive when
`changes` failed rather than letting it be skipped into a false pass. `!cancelled()` rather than
`always()`, so `cancel-in-progress` still stops the run.

**Branch protection must list all three job names per workflow, the `changes` job included** — a
`changes` job that dies after writing `false` would otherwise hand out a green check over untested
code.

### Security posture

- `permissions: contents: read` at the workflow level in both files. Results travel as **artifacts**,
  not bot comments, which is what keeps these safe on a fork's pull request.
- `persist-credentials: false` on every checkout.
- Every third-party action pinned by commit SHA, with the version in a trailing comment.
  `.github/dependabot.yml` keeps them current and tracks **only** the `github-actions` ecosystem.
- No secrets referenced anywhere in either workflow.
- Values reach `run:` through `env:`, never through `${{ }}` interpolation.

### Concurrency

```yaml
group: ${{ github.workflow }}-${{ github.event.pull_request.number || github.sha }}
cancel-in-progress: ${{ github.event_name == 'pull_request' }}
```

Pull requests collapse onto the PR number, so a force-push cancels the run it superseded. Every other
run gets its own group **from its SHA** — keying `master` on the ref would serialize merges into one
group where a *pending* run is cancelled when the next queues, leaving the middle of three merges
with no result at all.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/TestInfrastructure/` | SQLite fixture, time provider, harnesses |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/TestInfrastructure/` | Containers, factory, auth handler, fakes |
| `enterprise-gpt-ui/src/testing/` | Providers, DOM patches, axe harness, route coverage |
| `enterprise-gpt-ui/scripts/` | The five gate scripts |
| `enterprise-gpt-ui/angular.json` | The two test targets and the budgets |
| `.github/workflows/api-ci.yml`, `ui-ci.yml` | The two workflows |

## Related

- [../architecture/frontend.md](../architecture/frontend.md)
- [../frontend/design-system.md](../frontend/design-system.md)
