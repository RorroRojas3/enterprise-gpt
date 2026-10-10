<!-- andes:begin v1.12.1 -->
# Andes engineering standards

Shared by Claude Code and GitHub Copilot. The `andes-init` skill manages this block and replaces it on refresh — put project-specific instructions after the `andes:end` marker (`andes-init` scaffolds those sections on first install).

## Communication & comments

- Lead with the answer or the change; no preamble, no restating the request. Don't re-summarize what the user already saw — report only what changed or went wrong. Match length to substance.
- Comment only what code cannot say: why a decision was made, constraints, non-obvious invariants, workarounds with links. Never narrate what code does or what changed. XML doc comments on public APIs are API documentation, not comments.

## Load the standards before you edit

Detailed standards live in skills that load on demand. Load the matching skill before writing or reviewing code:

| Working on | Load |
| --- | --- |
| Any `*.cs` | `csharp-standards`, plus `aspnet-rest-apis` (web APIs), `azure-functions-csharp` (Functions), `csharp-mcp-server` (MCP servers), `ef-core` (EF Core; `ef-core-base-entities` / `ef-core-enum-reference-tables` for entities and lookup tables), `csharp-async`, `csharp-docs` (public APIs) as the change needs; `dotnet-api-architecture` when adding, moving, renaming, or registering files, folders, or projects. Non-negotiables: Minimal APIs only (no controllers), FluentValidation only (no DataAnnotations), services query the `DbContext` directly (no repositories), primary constructors, collection expressions, `var`, and the `csharp-standards` file layout |
| .NET tests | `csharp-xunit` — xUnit v3 + NSubstitute only; never FluentAssertions, Shouldly, Moq, NUnit, or MSTest. Test databases: Testcontainers → SQLite in-memory → dedicated test database → EF Core InMemory as a last resort |
| `*.razor`, `*.razor.cs` | `blazor-wasm` |
| Angular code | `angular-standards`; `ngrx-signal-store` for any state; `angular-ui-architecture` when adding, moving, or naming files or wiring layer lint; `angular-developer` references for depth |
| `*.tf` | `terraform-conventions` |
| `.github/workflows/*.yml`, `action.yml` | `github-actions-hardening`, plus `github-actions-efficiency` / `github-actions-runtime-upgrade-conventions` when relevant |
| Microsoft Agent Framework | `microsoft-agent-framework` |

Ground version-specific answers in the MCP servers when they are installed — `microsoft-learn` (.NET, Azure, other Microsoft docs; ships with `andes-core`), `angular-cli` (Angular), `context7` (any other library; ships with `andes-core`), `terraform` (providers, modules) — instead of memory.

## Review loop

After changing code, run the matching reviewer on the diff: `andes-csharp-code-reviewer` (C#, including Blazor), `andes-angular-code-reviewer` (Angular), `andes-github-actions-reviewer` (workflows, composite actions). Terraform has no reviewer — run `terraform fmt -check` and `terraform validate` instead. On Copilot CLI, every subagent (`task`) call also needs `name`, such as `csharp-review-1`.

1. Reviewers report only High and Medium findings plus a verdict. They never edit files or hand work back.
2. The implementer fixes every reported finding, then runs the reviewer once more on only the files changed since round 1.
3. Two rounds maximum. If High findings remain after round 2, stop and report them to the user instead of iterating; list any open Medium findings in the final summary.
4. After a passing verdict (**Approve** or **Approve with changes**), invoke `andes-se-technical-writer` to update `docs/` and add the `CHANGELOG.md` entry — unless your caller said it handles documentation. Only then is the change done; the final summary gives the verdict and the round count.

## Docs, changelog & requirements

- `andes-se-technical-writer` owns `docs/` and the root `CHANGELOG.md` ([Keep a Changelog](https://keepachangelog.com/en/1.1.0/)): one reader-facing entry per PR under `## [Unreleased]` in the matching subsection. Routine cleanups with no behavior change still get a one-line entry.
- To write a PRD, spec a feature, or break it into epics and user stories, delegate to `andes-prd-generator` (writes `docs/prd/`). If its report starts `PRD-STATUS: NEEDS-INPUT`, show its questions to the user verbatim and re-invoke it with the answers. It creates GitHub issues only after the user explicitly approves. PRDs and the implementation plans under `docs/plans/` get no changelog entry; plans reference story IDs (`US-xxx`). Plans are working files for following the implementation: never commit `docs/plans/`; keep it in `.gitignore`.
- When `andes-azure-devops` is installed, delegate creating, updating, or removing Azure DevOps epics, features, and stories (including from a PRD) to `andes-ado-backlog-manager`. It previews every change and always asks who to assign and which iteration; if its report starts `ADO-STATUS: NEEDS-INPUT` or `ADO-STATUS: NEEDS-SETUP`, show it to the user verbatim and re-invoke it with the answers and the user's explicit approval.
<!-- andes:end -->

## About this repository

**Enterprise GPT** is an enterprise ChatGPT/Claude-style AI chat platform. Users authenticate with Microsoft Entra ID, hold streaming (SSE) conversations with LLMs, and upload documents into conversations (text extraction → embeddings → SQL Server vector search). Administrators manage the model catalog and MCP tool servers.

Two applications and their reference docs live here:

- `enterprise-gpt-api/` — .NET 10 backend.
- `enterprise-gpt-ui/` — Angular 21 frontend. It was rebuilt from scratch; the old `enterprise-ui/` is deleted and nothing was migrated.
- `docs/` — engineer-facing reference, indexed by `docs/README.md`.
  - `architecture/` holds the cross-cutting docs every other one links up into: `overview.md`, `backend.md`, `frontend.md`, `data-model.md`, `auth-and-permissions.md`.
  - Each subsystem has its own folder:
    - `conversations/`: turn lifecycle, the SSE contract and its client, transcripts, the usage audit trail, export
    - `documents/`: ingestion, retrieval, sheet query, summarization, downloads
    - `tools/`: MCP servers, the File Agent
    - `models/`: catalog, providers
    - `frontend/`: state, errors, design system, answer rendering, layout
    - `admin/`: users and permissions, projects
    - `observability/`: telemetry, KQL cookbook
    - `operations/`: configuration, runbooks
    - `development/testing-and-ci.md`
  - `operations/runbooks.md` is the one file holding operator procedures, including the destructive Cosmos transcript cutover.
  - These docs carry no story ids, no release history and no rejected-alternative narratives. Keep new writing to the same rule.

**PRDs are not part of this repository**, and `docs/prd/` does not exist. Delegate to `andes-prd-generator` only when a user explicitly asks for a PRD; what it writes is a scratch artifact, not something to check in.

Agent configuration: this file is the single instruction source. `.claude/CLAUDE.md` only imports it. `.claude/settings.json` pins `model: opus` and `effortLevel: xhigh`, and enables the `andes-core`, `andes-dotnet`, `andes-angular` and `andes-github` plugins, which supply the skills, reviewers and MCP servers named above.

## Layout

### `enterprise-gpt-api/` — .NET 10 backend (`Enterprise.Gpt.slnx`)

Six projects plus two test projects under `test/`. `global.json` pins the SDK and selects the Microsoft Testing Platform test runner. `Directory.Build.props` sets `net10.0`, `Nullable` and `ImplicitUsings` for every project, and `Directory.Packages.props` holds every package version (central package management). `.editorconfig` makes a namespace that does not match its folder (IDE0130) a build error.

- **Layering:** `Api → Service → Repository → Entity → Common`, plus `Dto → Common` and `Service → Dto`. Entity and Repository never reference Dto; enums live in `Common/Enums/` and constant catalogs in `Common/Constants/`.
  - The folder map follows `dotnet-api-architecture`: Service by feature (`Conversations/`, `Documents/`, `Exports/`, `McpServers/`, `ChatModels/`, …), Repository by provider (`Sql/`, `Cosmos/`), Entity and Dto by feature. `ChatModels/` is the model-catalog feature, named so it never clashes with the kind-first `Models/` subfolders.
  - There is no `Abstractions` project. An interface with one implementation shares its file: `IModelService` and `ModelService` are both in `Service/ChatModels/ModelService.cs`.
  - `InternalsVisibleTo("Enterprise.Gpt.Unit.Test")` in `Api` and `Service` lets unit tests call `internal static` endpoint handlers directly.
- **Endpoints:** eight minimal-API modules in `Enterprise.Gpt.Api/Endpoints/` (Conversation, Document, Mcp, Model, Permission, Project, Report, User). `ModelEndpoints.cs` is the template for new work.
  - No controllers exist, and `Program.cs` never calls `MapControllers()`.
  - The SSE action is `ConversationEndpoints.StreamConversationAsync`. It sets its `text/event-stream` headers lazily on the first fragment, so a pre-stream failure still returns `application/problem+json`.
  - Every group carries `.RequireAuthorization()`.
  - Conversation routes carry no permission filter. Ownership is enforced in the service, and another user's conversation is a 404, not a 403.
- **Endpoint filters** (`Api/Filters/`): `PermissionEndpointFilter.Require(...)` and `MaxUploadSizeEndpointFilter.Require(...)` are static factories, not `IEndpointFilter`s.
  - `Require(params Guid[])` is **AND**: the caller must hold every listed permission.
  - It validates its ids against `PermissionIds.Names` at *map* time, so a bad id fails app start rather than emitting a nameless 403.
  - Admin routes are `Require(PermissionIds.Administrator)`. Admins are deliberately **not** implicitly granted every permission.
- **Permission cache:** authorization reads the singleton `IUserPermissionCache` (`Service/Caching/UserPermissionCache.cs`), not the database.
  - Entries are written at sign-in (`POST api/users/me`) and loaded lazily on a miss: single-flight via `Lazy<Task<T>>`, like `McpClientCache`.
  - Invalidation is **per user**, never a full reload. It comes from `PermissionService`, `UserService` and `McpServerService.DeactivateMcpServerAsync`.
  - A user with zero grants caches the *empty set*. Treating that as a miss would restore the per-request query.
  - Entries carry an absolute `Permissions:Cache:EntryLifetime` (default 5 min) purely as the scale-out staleness bound, because invalidation is per instance.
  - `McpToolProvider.AcquireToolsAsync` and `McpServerService.GetPermittedMcpServersAsync` deliberately stay on the database. Read the comments there before "optimizing" them onto the cache.
- **Errors:** RFC 9457 Problem Details (`application/problem+json`) everywhere.
  - Who writes them:
    - three chained `IExceptionHandler`s, through `IProblemDetailsService`;
    - the endpoint filters;
    - `app.UseStatusCodePages()`, so bare 401s, unrouted 404s and 405s carry a body too.
  - Domain problem types:
    - They live in `Api/Problems/Constants/ProblemTypes.cs` under the relative base `/problems/`: `validation-error`, `upload-too-large`, `resource-not-found`, `forbidden`, `permission-required`, `conversation-busy`, `mcp-server-unavailable`, `provider-not-configured`, `storage-not-configured`.
    - They are **opaque identifiers clients match verbatim, not links**, so changing one is a breaking API change.
    - A problem with no domain type carries the RFC 9110 status-section link ASP.NET Core supplies.
  - `CustomizeProblemDetails` (`Problems/ProblemDetailsRegistration.cs`) stamps `traceId` (`Activity.Current?.Id`, else `HttpContext.TraceIdentifier`) and `instance` on every problem, in one place.
  - Status mapping:

    | Exception | Status |
    |---|---|
    | `ValidationException` | 400 as `HttpValidationProblemDetails`; `errors` is keyed by the failing **property name** |
    | `ConversationBusyException` | 409 |
    | `NotFoundException` | 404 |
    | `ForbiddenException` | 403 |
    | `McpServerUnavailableException` | 502 (see below) |
    | `OperationCanceledException` | 499, no body |
    | anything else | 500, message suppressed |

    `McpServerUnavailableException` also covers a token acquisition that needs user interaction. These servers are consented tenant-wide, so that state is a broken registration, not something the user can act on.
  - Handlers short-circuit on `Response.HasStarted`, so a faulting SSE stream is not corrupted.
  - New endpoints declare error responses with `.ProducesProblem(status)` and `.ProducesValidationProblem()`, **never** `.Produces<T>(status)`. Every route group carries a group-level `.ProducesProblem(401)`.
- **Persistence:** `EnterpriseGptDbContext` on SQL Server, EF Core 10.
  - `UseCompatibilityLevel(170)` means **SQL Server 2025**; pre-2025 engines and LocalDB reject it.
  - Every FK is forced to `DeleteBehavior.NoAction`. The 26 `IEntityTypeConfiguration`s are applied explicitly, not by assembly scan.
  - Soft delete uses a nullable `DateDeactivated` on `BaseEntity` with **no `HasQueryFilter`**; every query filters manually.
  - Optimistic concurrency uses `[Timestamp] byte[] Version`.
  - Migrations:
    - `Repository/Sql/Migrations/` holds `Initial` (the whole schema plus the `HasData` seeds) followed by incremental migrations.
    - `Database.Migrate()` runs at startup (skipped in the `Testing` environment), so a database built from empty is migrated and seeded.
    - A database this application did not build from empty must be baselined first; `docs/operations/runbooks.md` has the steps.
  - A new bool column that must backfill is configured `HasDefaultValue(x).ValueGeneratedNever()`, because a store-generated bool is silently dropped from EF's `HasData` seed differ.
- **Cosmos DB:** holds the conversation transcript, one document per message plus a `type`-discriminated header.
  - Partition key: the synthetic `/pk`, holding `{userId}:{conversationId}`.
  - Accessed through the **raw `CosmosClient` SDK** with the System.Text.Json serializer, not an EF Core provider. Dates are fixed-width UTC because `ORDER BY dateCreated` is a lexicographic string compare.
  - Access goes through `ITranscriptStore` (`Repository/Cosmos/CosmosTranscriptStore.cs`): transactional batches, continuation-token paging, partition purge. **Every member requires a partition key.**
  - The container id key is `CosmosDb:TranscriptContainerId`. The old `CosmosDb:ContainerId` fails startup by design.
  - Read `docs/conversations/transcripts.md`, and `docs/operations/runbooks.md` before deploying, because the cutover destroys every existing transcript.
- **LLM access:** Microsoft.Extensions.AI keyed `IChatClient` (Azure AI Foundry), with function invocation and `UseOpenTelemetry()`.
  - MCP tool servers go through `ModelContextProtocol`.
  - Document ingestion runs on a custom background-job pipeline (queue + hosted processor).
  - Mapping is hand-written static `<Entity>Mapper` classes beside their feature service, each exposing both a materialized `MapToXDto` and an `Expression<Func<X, XDto>>` for server-side projection. No AutoMapper.
  - `Andes.Extensions.AI*` are the repository owner's own packages; changing them upstream is an option, not a vendor request.

### `enterprise-gpt-ui/` — Angular 21 frontend

Angular **21.2**, standalone components, **zoneless**. Zoneless is the v21 default, and `provideZonelessChangeDetection()` is stated explicitly in `app.config.ts` anyway. `@ngrx/signals` is pinned exact at **21.1.1**.

- **Styles:** Bootstrap 5.3 + **SCSS**.
  - `src/styles.scss` is an `@import` chain, not `@use`, because Bootstrap 5.3 still shares one global `!default` namespace across its partials.
  - The order is load-bearing: Bootstrap's `root` before `tokens`, its components before `bootstrap-overrides`, and `motion` last.
  - Components read tokens through `var(--…)` and **never** `@use 'tokens'`, which emits `:root` and would re-scope it to the component.
- **Markdown:** `ngx-markdown` over marked + Prism, with DOMPurify behind its `SANITIZE` provider.
  - It is wired through `provideChatMarkdown()` on the `Chat` component, so the whole stack rides the lazy chat chunk. A build gate keeps it out of the initial graph.
  - Supplying a `SANITIZE` function *replaces* Angular's `DomSanitizer` in that pipeline, so DOMPurify's closed profile is the only DOM-boundary layer. A `renderer.html = () => ''` override backs it by dropping raw HTML at the parser.
  - `ngx-markdown` performs `bypassSecurityTrustHtml` internally, which is why application code has zero call sites.
- **MSAL:** `msal-angular` 6 / `msal-browser` 5.
- **Tests:** **Vitest** via `@angular/build:unit-test` (`watch: false`, `providersFile: src/testing/test-providers.ts`, which enables `provideCheckNoChangesConfig({ exhaustive: true })`). The axe accessibility specs are a separate target (`npm run test:a11y`) that `npm test` skips.
- **Lint and type checks:** ESLint 10 flat config (`eslint.config.mjs`) with typescript-eslint, angular-eslint and `@ngrx/eslint-plugin` (`signalsTypeChecked`, which needs `projectService: true`).
  - `no-restricted-syntax` bans `bypassSecurityTrustHtml`.
  - `npm run lint` also runs `check:icons`, `check:forbidden` and `check:tokens`. `npm run build` runs `check-initial-chunk.mjs` over the esbuild metafile.
  - Unused-symbol detection is tsc's (`noUnusedLocals`/`noUnusedParameters`), not ESLint's, because tsc counts a `{@link}` as a use.
  - `tsconfig.json` is strict, including `strictTemplates`, `noPropertyAccessFromIndexSignature` and `noUncheckedIndexedAccess`.
- **Folder layout:** dependencies run one way: `features → shared → core → domain`.
  - `core/` holds app-wide singletons and pure policy.
  - `domain/` is framework-free, so the SSE codec and the activity fold test in Node with no `TestBed`.
  - `shared/` is presentational only.
  - `features/` is route-owned, with its own stores.
  - Path aliases: `@core/* @domain/* @shared/* @features/* @testing/*`.
  - Components use the v21 convention: `foo.ts`/`foo.html`/`foo.scss`, no `.component` suffix. No barrel `index.ts` files.
- **Runtime config:** no `environments/` and no `fileReplacements`.
  - `main.ts` fetches `config.json` (`cache: 'no-store'`, resolved against `document.baseURI`), validates it with a narrow assertion, and provides `APP_CONFIG` **before** `bootstrapApplication`.
  - A missing or invalid config renders the static fatal shell in `index.html` and does not bootstrap.
  - `public/config.json` is the committed dev copy and is replaced per environment.
- **Errors:** `core/errors/` mirrors the API's problem types verbatim.
  - Every failure is normalized into one discriminated `AppError`, through `toAppError` (sync) and `toAppErrorFromResponse` (async, for the raw-fetch stream path).
  - `serverMessagesFor` maps the API's PascalCase `errors` keys onto camelCase form fields, using .NET's own camel-casing algorithm.
  - `authErrorDecision` is the single source of truth for whether a failure may trigger a token refresh. Only a bare 401 may, and its exhaustive `satisfies` table makes adding an arm without deciding a compile error.
  - `retryInterceptor` retries GETs only, on 502/503/504 and transport failures, with full-jitter backoff. It never retries a 409 or an app-typed 503.
- **State:** the five composable store features live in `core/state/`: `withRequestStatus`, `withOffsetPagination`, `withPendingIds`, `withClientQuery`, `withResetOnSignOut`. Every store composes them.
  - `withResetOnSignOut` **must be composed last**. It snapshots `getState` during construction, so a state-contributing feature after it would survive sign-out. A dev-only `onInit` guard throws, naming the offending keys.
  - Cross-store coordination goes through the `@ngrx/signals/events` plugin, a deliberate opt-in. Events live in `core/events/`:
    - `sessionEvents.signedOut`.
    - `conversationEvents.updated`/`.deleted`: server-confirmed facts only. The one bounded exception is the favourite endpoint, whose 204 carries no body. Optimistic patches and rollbacks stay inside `ConversationListStore`.
    - `adminEvents`: user events carrying a DTO, plus two `type<void>()` catalog events, `modelCatalogChanged` and `mcpCatalogChanged`. Each has a route-scoped and a root observer, and both refetch.
    - `turnEvents`.
  - Clearing state is not cancelling work: a store that loads asynchronously must also `takeUntil(injectSignedOut())`.
- **Bundle budget:** the production build warns above **680 kB** initial raw and fails above **720 kB**; the `styles` budget is 66/80 kB.
  - MSAL is ~249 kB of the initial graph, and none of it is deferrable, because `main.ts` must `initialize()` the instance before `bootstrapApplication`.
  - The rule to preserve is which libraries stay off the initial graph:
    - the markdown stack (`ngx-markdown`, `marked`, `prismjs`, `dompurify`) rides the lazy chat chunk;
    - `mermaid`, `katex` and `@microsoft/applicationinsights-web` are reached only through `await import(…)` behind DI tokens and `config.json` flags, in lazy chunks of their own.
  - `check-initial-chunk.mjs` fails the build if a static import from `main.ts` (or from the chat chunk, for the two flagged libraries) reaches any of them, and names the import path that did it. It also asserts that mermaid and katex exist *somewhere*, since every other check passes when a library is simply gone.
  - Re-baseline only when a dependency genuinely has to be initial, and check first whether it does.

## Build, test, run

```bash
# in enterprise-gpt-api/
dotnet build                                          # compile the solution
dotnet test                                           # all tests (integration needs Docker running)
dotnet test --project test/Enterprise.Gpt.Unit.Test   # unit tests only
dotnet format --verify-no-changes                     # formatting gate (dotnet format to apply)

# in enterprise-gpt-ui/
npm start               # ng serve on http://localhost:4200 (the only origin the API's CORS policy allows)
npm run build           # ng build — fails above the 720 kB initial-bundle budget, then checks the initial graph
npm run lint            # eslint --max-warnings=0, then the icon, forbidden-API and token checks
npm run check:contract  # the vendored Andes contract against the restored NuGet package
npm test                # ng test — Vitest, single run (skips the a11y specs)
npm run test:a11y       # the axe accessibility suite
npm run test:watch      # Vitest, watch mode
npm run format          # Prettier (format:check for the read-only variant); lint does not run it
```

- **Dev certificate:** the dev client needs `dotnet dev-certs https --trust`. Until then, every call to `https://localhost:7045` fails while *looking* like a CORS error.
- **CI:** both sides have CI, documented in `docs/development/testing-and-ci.md`.
  - `ui-ci.yml`: a `changes` job, then `build` (the UI gates) and `contract` (one .NET restore checking the vendored Andes contract).
  - `api-ci.yml`: `changes`, then `unit` (no Docker) and `integration` (Testcontainers SQL Server 2025 + the Cosmos vNext emulator).
  - `.github/dependabot.yml` keeps their SHA-pinned actions current and tracks **only** the `github-actions` ecosystem.
- **No `paths:` filters:** a workflow skipped by a path filter never reports, while a job skipped by a conditional reports Success. So the filter lives in the `changes` job, and the dependents' `if:` is written **fail-open** (`!cancelled() && … != 'false'`).
- **Branch protection** must list **all three** job names per workflow, `Detect …` included. Otherwise a `changes` job that dies after writing `false` hands out a green check over untested code.

## Conventions

### Code comments (stricter than the block above)

Write for a senior engineer who knows the language and can read the code.

- **Default to no comment.** Add one only where a competent reader would still ask *why*. There is no per-function or per-member quota.
- **One or two lines.** Three need a reason. Past six, the content belongs in `docs/`, with a one-line pointer from the code.
- **Never reference PRD story ids, epics or design frames** (`US-1402`, `FR-11`, "PRD decision 1", "frame `2f`"), in source or in test names. Traceability belongs in the PR description.
- **Never explain the language, framework or API**, and never narrate ("added X", "now uses Y").
- **Compress rather than delete.** A real *why* buried in a paragraph becomes one sentence.

```ts
// Good — the one thing the code cannot say
/** Depth, not a flag: `dragleave` fires on every child crossing. */
```

### C#

- **XML docs:**
  - A one-sentence `<summary>` on public API surface. Interfaces carry the docs; implementations use `/// <inheritdoc />`.
  - `<param>`/`<returns>` only where they add what the signature does not.
  - `<remarks>` is for a caveat a caller must know, two sentences at most.
  - `internal` and test types are commented only where a *why* exists.
  - **This overrides `csharp-docs` wherever the two disagree.**
- **`ConfigureAwait(false)`** is applied in hosted-service, cache, lock and MCP-client paths. Follow the surrounding file.
- **Unit tests:** services taking `EnterpriseGptDbContext` run on SQLite in-memory via `SqliteDbContextFixture`. A model customizer neutralizes the rowversion and vector columns SQLite cannot handle. Each test class news up its own fixture rather than using `IClassFixture`.
- **Integration tests:** `WebApplicationFactory<Program>` + Testcontainers **SQL Server 2025** (`CustomWebApplicationFactory` + `TestAuthHandler`, header `X-Test-User`). They need Docker and carry both `[Collection("Integration")]` and `[Trait("Category", "Integration")]`.
- **Live-model tests:** `FileAgentSpike` / `ConversionMatrixTests` / `SandboxInventoryTests` drive a live model and a remote sandbox. Rerun them alone before calling a failure a regression.

### Angular / NgRx

- **Components and state:** standalone components, `ChangeDetectionStrategy.OnPush`, signal `input()`/`output()`, `inject()`, and built-in control flow. Non-trivial state goes in an NgRx Signal Store.
- **Stores:** keep `protectedState` on, and write state only through `patchState` with standalone updaters.
  - Use `rxMethod` (not `signalMethod`) whenever requests can overlap.
  - One store per entity type; `withEntities` for keyed collections.
  - **Copy `ConversationListStore`** (`core/conversations/`) rather than re-deriving: `withEntities` + the composable features, `withResetOnSignOut` **last**, and one `rxMethod` wired in `onInit` to a signal computation.
  - This is not classic NgRx: no actions, reducers or effects unless the Events plugin is a deliberate choice.
- **Forms are Signal Forms** (`@angular/forms/signals`): `form()` over a `signal(model)`, schema rules, `[formField]` bindings.
  - This is a product-owner decision. Never introduce `ReactiveFormsModule`/`FormsModule`.
  - The API is `@experimental` in Angular 21.2, so re-check it against the installed types on every Angular upgrade. `@angular/forms/signals/compat` is the bridge if an integration ever demands an `AbstractControl`.
  - Server validation maps through `serverMessagesFor` (`core/errors/`), fed to a declarative `validate` rule.
- **TSDoc:** a one-line `/** */` where the exported name is not enough. No multi-paragraph headers on components, directives or stores; screen rationale belongs in `docs/`.
- **Stale comments:** comments saying "Angular 19 pattern" are stale; the app is on Angular 21.
- **Before handing work back:** run `npm run lint`, then `npm run format`.

### Standards vs. the current codebase

The skills set the target. These rows are where the code has not migrated yet. Write new code the "for new code" way, and do **not** bulk-convert existing files unless asked.

| Rule (target) | Codebase today | For new code |
|---|---|---|
| File-scoped namespaces | Mixed; `test/` is all file-scoped | File-scoped in new files |
| `[LoggerMessage]` logging | ~185 direct `ILogger` calls; Serilog is not used | `[LoggerMessage]` partials in a `Logging` region; do not add Serilog |
| Schema via migrations | `Initial` plus incremental migrations; `Migrate()` runs at startup; tests use `EnsureCreated()` | `dotnet ef migrations add <Verb><Subject>` |
| API versioning | None; routes are plain `api/{resource}` | Match the existing routes |
| Zoneless Angular | Zoneless throughout; no `zone.js` | Nothing re-renders without a signal write, `markForCheck`, or `AsyncPipe` |
| `OnPush` everywhere | `angular.json` sets it as the schematic default | `OnPush` on new components |
| Frontend lint | ESLint plus the bundle and safety gates, green | Run `npm run lint` before handing work back; do not add a second lint mechanism |

### Workflow

- **Commits:** never commit or push. Leave work in the working tree for the user to review.
- **Reviewers and docs:** after C# changes run `andes-csharp-code-reviewer`; after Angular changes `andes-angular-code-reviewer`; after workflow changes `andes-github-actions-reviewer`. Then `andes-se-technical-writer` updates `docs/` and `CHANGELOG.md`.
- **Changelog:** a single `## [Unreleased]` section. On release it is renamed to the version and date, and a fresh `[Unreleased]` is started.
