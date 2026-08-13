# CLAUDE.md

Project memory for **Enterprise GPT**. It loads automatically every session and governs how Claude Code works in this repository.

Layout: this file lives at `.claude/CLAUDE.md` (**not** the repo root); detailed standards in `.claude/rules/` auto-apply by file path; skills, subagents, and the `/ngrx-signals-sync` + `/repo-audit` commands live under `.claude/`; `settings.json` pins `model: opus`, `effortLevel: xhigh`, and 10 official plugins.

## About this application

An enterprise ChatGPT/Claude-style AI chat platform: users authenticate with Microsoft Entra ID, hold streaming (SSE) conversations with LLMs, upload documents into conversations (text extraction → embeddings → SQL Server vector search), and administrators manage the model catalog and MCP tool servers.

### `enterprise-gpt-api/` — .NET 10 backend (`Enterprise.Gpt.sln`)

Six projects + two test projects, all `net10.0`, `Nullable`/`ImplicitUsings` enabled. No `LangVersion` pin (so C# 14), **no `.editorconfig`**, no central package management — versions are pinned per-csproj.

- **Layering** — `Api → Service → Repository → Entity`, plus `Dto` and `Common`. Note the non-obvious edges: **`Entity → Dto`** and **`Repository → Dto`**, so `Dto` sits *below* `Entity`, not above. There is no `Abstractions` project — interfaces live beside their implementations (`IModelService` and `ModelService` share `Service/ModelService.cs`). `InternalsVisibleTo("Enterprise.Gpt.Unit.Test")` in `Api` and `Service` lets unit tests call `internal static` endpoint handlers directly.
- **Endpoints** — seven **minimal-API** modules in `Enterprise.Gpt.Api/Endpoints/` (Conversation, Document, Mcp, Model, Permission, Project, User); `ModelEndpoints.cs` is the template for new work. **No controllers survive and there is no `Controllers/` directory** — `ConversationEndpoints.cs` replaced the last one, and `Program.cs` never calls `MapControllers()`. The SSE action lives there as `StreamConversationAsync`, which sets its `text/event-stream` headers lazily on the first fragment so a pre-stream failure still returns `application/problem+json`. Everything is `.RequireAuthorization()` at the group level; conversation routes carry no permission filter, because ownership is enforced in the service and another user's conversation is a 404, not a 403.
- **Endpoint filters** (`Api/Filters/`) — `PermissionEndpointFilter.Require(...)` and `MaxUploadSizeEndpointFilter.Require(...)` are **static factories**, not `IEndpointFilter`s; there is no `IEndpointFilter` type left in the project. `Require(params Guid[])` is **AND** — the caller must hold every listed permission — and validates its ids against `PermissionIds.Names` at *map* time, so a bad id fails app start rather than emitting a nameless 403. Admin routes are just `Require(PermissionIds.Administrator)`; admins are deliberately **not** implicitly granted every permission.
- **Permission cache** — authorization reads the singleton `IUserPermissionCache` (`Service/Caching/UserPermissionCache.cs`), not the database: entries are written at sign-in (`POST api/users/me`) and loaded lazily on a miss (single-flight, `Lazy<Task<T>>`, like `McpClientCache`). Invalidation is **per user**, never a full reload, from `PermissionService`, `UserService`, and `McpServerService.DeactivateMcpServerAsync`. A user with zero grants caches the *empty set* — treating that as a miss would restore the per-request query. Entries carry an absolute `Permissions:Cache:EntryLifetime` (default 5 min) purely as the scale-out staleness bound, since invalidation is per-instance. `McpToolProvider.AcquireToolsAsync` and `McpServerService.GetPermittedMcpServersAsync` deliberately stay on the database — see the comments there before "optimizing" them onto the cache.
- **Errors** — RFC 9457 **Problem Details** (`application/problem+json`) everywhere, written through `IProblemDetailsService` by three chained `IExceptionHandler`s, by the endpoint filters, and by `app.UseStatusCodePages()` — so bare 401s, unrouted 404s and 405s carry a body too. Domain `type` URIs live in `Api/Problems/ProblemTypes.cs` under the relative base `/problems/` (`validation-error`, `upload-too-large`, `resource-not-found`, `forbidden`, `permission-required`, `conversation-busy`, `mcp-authorization-required`, `mcp-server-unavailable`, `provider-not-configured`, `storage-not-configured`); they are **opaque identifiers clients match verbatim, not links**, so changing one is a breaking API change. A problem with no domain type carries the RFC 9110 status-section link ASP.NET Core supplies. `CustomizeProblemDetails` (`Problems/ProblemDetailsRegistration.cs`) stamps `traceId` (`Activity.Current?.Id`, else `HttpContext.TraceIdentifier`) and `instance` on every problem, in one place. `ValidationException` → 400 as `HttpValidationProblemDetails`, whose `errors` dictionary is keyed by the failing **property name**; `ConversationBusyException` → 409, `NotFoundException`/`KeyNotFoundException` → 404, `Forbidden`/`McpAuthorizationRequired` → 403 (deliberately not 401 — a 401 sends clients into token-refresh loops that cannot fix a consent requirement), `McpServerUnavailableException` → 502, `OperationCanceledException` → 499 with no body, anything else → 500 with the message suppressed. Handlers short-circuit on `Response.HasStarted` so a faulting SSE stream is not corrupted. New endpoints declare error responses with `.ProducesProblem(status)` and `.ProducesValidationProblem()` — **never** `.Produces<T>(status)` — and every route group carries a group-level `.ProducesProblem(401)`.
- **Persistence** — `EnterpriseGptDbContext` on SQL Server (EF Core 10, `UseCompatibilityLevel(170)` = **SQL Server 2025**; pre-2025 engines and LocalDB reject it). Every FK is forced to `DeleteBehavior.NoAction`; the eight `IEntityTypeConfiguration`s are applied explicitly, not by assembly scan. Soft delete via nullable `DateDeactivated` on `BaseEntity` with **no `HasQueryFilter`** — every query filters manually. Optimistic concurrency via `[Timestamp] byte[] Version`. `Repository/Migrations/` exists only as a csproj `<Folder Include>` and holds **zero migrations**, yet `Database.Migrate()` runs at startup (skipped in the `Testing` environment).
- **Cosmos DB** — conversation *message* history, partitioned by `/userId`, via the **raw `CosmosClient` SDK**, not an EF Core provider.
- **LLM access** — Microsoft.Extensions.AI keyed `IChatClient` (Azure AI Foundry) with function invocation and `UseOpenTelemetry()`; MCP tool servers via `ModelContextProtocol` 2.0.0; document ingestion runs on a custom background-job pipeline (queue + hosted processor). Mapping is hand-written static extension classes in `Service/Mappers/`, each exposing both a materialized `MapToXDto` and an `Expression<Func<X, XDto>>` for server-side projection — no AutoMapper.

### `enterprise-gpt-ui/` — Angular 21 frontend

The old `enterprise-ui/` is **deleted**. It could not read the framed SSE stream and is being rebuilt from scratch at `enterprise-gpt-ui/` against `docs/prd/enterprise-ui-rebuild.md`; nothing was migrated. Docs that still link into `enterprise-ui/` (notably `docs/conversations/streaming-contract.md` §7) point at paths that no longer exist. The PRD now names `enterprise-gpt-ui/` wherever it means the rebuild target; the four surviving `enterprise-ui/` mentions are deliberate history about the deleted client, not drift.

**Design reference.** `docs/ui/mockups/` is deleted; the UI authority is the Claude Design handoff bundle at `docs/design/`. Boards must be served over HTTP — `node docs/design/serve.mjs` (port 4300) or the `serve-design` VS Code task — because `support.js` resolves each `<dc-import>` by `fetch()`, which a `file://` origin blocks. `docs/design/project/theme.css` is the token source of record. The prototypes load React, Bootstrap, Bootstrap Icons, and Google Fonts from CDNs; that is a prototype artifact only, and the app self-hosts all four from npm.

Angular **21.2.19**, standalone components, **zoneless** (the v21 default; `provideZonelessChangeDetection()` is stated explicitly in `app.config.ts` anyway), `@ngrx/signals` **21.1.1** pinned exact to match the skill's doc snapshot. Bootstrap 5.3 + **SCSS**: `src/styles.scss` is an `@import` chain, not `@use`, because Bootstrap 5.3 still shares one global `!default` namespace across its partials. Order is load-bearing — Bootstrap's `root` before `tokens`, its components before `bootstrap-overrides`, `motion` last. Components read tokens through `var(--…)` and **never** `@use 'tokens'`, which emits `:root` and would re-scope it to the component. Markdown is **`ngx-markdown` 21.3.0 over marked + Prism**, with DOMPurify behind its `SANITIZE` provider — wired by US-601 through `provideChatMarkdown()` on the `Chat` component, so the whole stack rides the lazy chat chunk and a build gate keeps it out of the initial graph. Supplying a `SANITIZE` function *replaces* Angular's `DomSanitizer` in that pipeline, so DOMPurify's closed profile is the only DOM-boundary layer, backed by a `renderer.html = () => ''` override that drops raw HTML at the parser. markdown-it was removed; `ngx-markdown` performs the `bypassSecurityTrustHtml` internally, which is why application code has zero call sites. MSAL `msal-angular` 6 / `msal-browser` 5 is installed but **not yet wired** — that is US-201. Tests run on **Vitest** via `@angular/build:unit-test` (`watch: false`, `providersFile: src/testing/test-providers.ts`, which enables `provideCheckNoChangesConfig({ exhaustive: true })`). Prettier is configured (`npm run format`) and **ESLint is wired** by US-108 — flat config in `eslint.config.mjs` over ESLint 10 + typescript-eslint 8 + angular-eslint 21.4 + `@ngrx/eslint-plugin` 21.1.1 (`signalsTypeChecked`, which needs `projectService: true`), with `no-restricted-syntax` banning `bypassSecurityTrustHtml`. `npm run lint` also runs three Node checks — `check:icons`, `check:forbidden`, `check:tokens` — and `npm run build` runs `check-initial-chunk.mjs` over the esbuild metafile. Unused-symbol detection is tsc's (`noUnusedLocals`/`noUnusedParameters`), not ESLint's, because tsc counts a `{@link}` as a use. `tsconfig.json` is strict, including `strictTemplates`, `noPropertyAccessFromIndexSignature`, and `noUncheckedIndexedAccess`.

- **Layout** — dependencies run one way: `features → shared → core → domain`. `core/` holds app-wide singletons and pure policy, `domain/` is framework-free (so the SSE codec and activity fold test in Node with no `TestBed`), `shared/` is presentational only, `features/` is route-owned with its own stores. Path aliases `@core/* @domain/* @shared/* @features/* @testing/*`. Components use the v21 convention — `foo.ts`/`foo.html`/`foo.scss`, no `.component` suffix. No barrel `index.ts` files.
- **Runtime config** — no `environments/`, no `fileReplacements`. `main.ts` fetches `config.json` (`cache: 'no-store'`, resolved against `document.baseURI`), validates it with a narrow assertion, and provides `APP_CONFIG` **before** `bootstrapApplication`. A missing or invalid config renders the static fatal shell in `index.html` and does not bootstrap. `public/config.json` is the committed dev copy and is replaced per environment.
- **Errors** — `core/errors/` mirrors the API's ten problem types verbatim and normalizes every failure into one discriminated `AppError` (14 arms) through `toAppError` (sync) and `toAppErrorFromResponse` (async, for the raw-fetch stream path). `applyServerErrors` maps the API's PascalCase `errors` keys onto camelCase form controls using .NET's own camel-casing algorithm. `authErrorDecision` is the single source of truth for whether a failure may trigger a token refresh — only a bare 401 may, and the exhaustive `satisfies` table makes adding an arm without deciding a compile error. `retryInterceptor` retries GETs only, on 502/503/504 and transport failures, with full-jitter backoff, and never on 409 or an app-typed 503.
- **State** — the five composable store features live in `core/state/` (`withRequestStatus`, `withOffsetPagination`, `withPendingIds`, `withClientQuery`, `withResetOnSignOut`); US-104 built them and every store composes them. `withResetOnSignOut` **must be composed last** — it snapshots `getState` during construction, and a state-contributing feature after it would survive sign-out; a dev-only `onInit` guard throws naming the offending keys. Cross-store coordination goes through the `@ngrx/signals/events` plugin — a deliberate opt-in: `sessionEvents.signedOut` and `conversationEvents.updated`/`.deleted` (US-304/305/306 — server-confirmed facts only, with one bounded exception where the favourite endpoint's 204 carries no body; optimistic patches and rollbacks stay inside `ConversationListStore`) live in `core/events/`, with `turnEvents` (US-409) and `adminEvents` (US-1207/1208) to follow. Clearing state is not cancelling work: a store that loads asynchronously must also `takeUntil(injectSignedOut())`.
- **Bundle budget** — production build warns above **665 kB** initial raw and fails above **720 kB**, with a `styles` bundle budget at 65/80 kB; baseline is **660.24 kB raw / 161.01 kB transfer** as measured after US-601/602/606 (styles 62.08 kB). MSAL alone is 248.9 kB of that and none of it is deferrable — `main.ts` must `initialize()` the instance before `bootstrapApplication`. Both the shell and the chat route are **lazy**, which is what keeps the signed-in chrome off the four unguarded auth routes and is how the whole 124.8 kB markdown stack sits in the lazy `chat` chunk (179.47 kB) instead of the initial graph — `check-initial-chunk.mjs` fails the build if a static import from `main.ts` reaches `ngx-markdown`, `marked`, `prismjs` or `dompurify`. Re-baseline whenever a story adds a substantial initial-chunk dependency — and check first whether the dependency actually has to be initial.

### `docs/`

Feature designs: `documents/upload-workflow.md`, `models/model-management.md`, `users/user-management.md`, plus UI mockups under `ui/mockups/`. `docs/prd/enterprise-ui-rebuild.md` is the frontend rebuild PRD — the authority for every `US-xxx` story reference.

## Communication & comments (always)

**Responses**

- Lead with the answer or the change. No preamble, no filler, no restating the request.
- Do not re-summarize a plan or diff the user already saw; report only what changed or went wrong.
- Match length to substance — a one-line answer is a complete answer.

**Code comments**

- Comment only what code cannot say: why a decision was made, constraints, non-obvious invariants, workarounds with links.
- Never narrate what code does. No per-function comment quota. No change-narration comments ("added X", "now uses Y").
- XML doc comments on public APIs are API documentation, not comments — that standard stands.

## C# coding standards (always)

Full standards live in `.claude/rules/csharp.md` (auto-applies to `*.cs`). The always-on core:

- Target the latest C# version (currently **C# 14**); prefer pattern matching, switch expressions, and `nameof(...)`.
- Prefer **primary constructors**; capture each injected dependency into a `private readonly` `_camelCase` field and use the field in method bodies.
- Prefer **collection expressions** (`[]`, `[1, 2, 3]`, `[.. items]`) over `new List<T>()`, `new T[] { }`, or `Array.Empty<T>()`.
- PascalCase for types, methods, and public members; `_camelCase` private fields; camelCase locals and parameters; `I`-prefixed interfaces.
- Declare variables non-nullable; validate `null` at entry points only; use `is null` / `is not null` — **never** `== null` / `!= null`.
- Suffix async methods with `Async`; **never** block with `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`; no `async void` outside event handlers; flow a `CancellationToken` through long-running operations (see the `csharp-async` skill). `ConfigureAwait(false)` is applied in hosted-service, cache, lock, and MCP-client paths — follow the surrounding file.
- **Never log PII or secrets**; prefer `DefaultAzureCredential` + Azure Key Vault / Managed Identity over secrets in code or config.
- XML doc comments on all public APIs; interfaces carry the docs and implementations use `/// <inheritdoc />` (see the `csharp-docs` skill).
- xUnit **v3** tests under `enterprise-gpt-api/tests/` in `Enterprise.Gpt.Unit.Test` and `Enterprise.Gpt.Integration.Test`, named `MethodName_Scenario_ExpectedBehavior`; Arrange-Act-Assert structure but **no** `// Arrange` / `// Act` / `// Assert` comments; plain xUnit `Assert` (no FluentAssertions — v8+ is commercially licensed); isolate with **NSubstitute** (see the `csharp-xunit` skill).
- Unit tests: services taking `EnterpriseGptDbContext` run on SQLite in-memory via `SqliteDbContextFixture` (a model customizer neutralizes the rowversion and vector columns SQLite cannot handle); each test class news up its own fixture rather than using `IClassFixture`. Integration tests: `WebApplicationFactory<Program>` + Testcontainers **SQL Server 2025** (`CustomWebApplicationFactory` + `TestAuthHandler`, header `X-Test-User`); they need Docker and carry both `[Collection("Integration")]` and `[Trait("Category", "Integration")]`.
- When reviewing, make only **high-confidence** suggestions; comment on _why_ a non-obvious design decision was made.

## Standards vs. the current codebase

The rules files set the target. These are the places the code has not migrated yet — write new code the "for new code" way and do **not** bulk-convert existing files unless asked.

| Rule (target) | Codebase today | For new code |
| --- | --- | --- |
| File-scoped namespaces | 0 file-scoped vs 54 block-scoped in Api+Service; only `tests/` is file-scoped | File-scoped in new files; leave existing ones alone |
| Structured logging (Serilog) | No Serilog package; plain `ILogger<T>` | `ILogger<T>` with structured templates; do not add Serilog casually |
| `.editorconfig` formatting | None exists under `enterprise-gpt-api/` | Follow the surrounding file; no tooling enforces style |
| Schema via migrations | `Migrations/` is empty; `Migrate()` runs at startup; tests use `EnsureCreated()` | Flag before adding the first migration — it changes startup behavior |
| API versioning | None; routes are plain `api/{resource}` | Match the existing routes |
| Zoneless Angular | Zoneless throughout — v21's default, plus an explicit `provideZonelessChangeDetection()`; no `zone.js` anywhere | Assume zoneless semantics; nothing re-renders without a signal write, `markForCheck`, or `AsyncPipe` |
| `OnPush` everywhere | The only component so far is `OnPush`; `angular.json` sets it as the schematic default | `OnPush` on new components |
| `rxMethod` / `withEntities` / `protectedState` | US-104's five composable features are in `core/state/`; `ConversationListStore` (`core/conversations/`) is US-302's reference list store and the shape the remaining ones copy | Copy `ConversationListStore` rather than re-deriving: `withEntities` + the composable features, `withResetOnSignOut` **last**, and one `rxMethod` wired in `onInit` to a signal computation so loading is declarative and `switchMap` handles the races |
| Frontend lint (ESLint, bundle + safety gates) | Installed by US-108 and green | Run `npm run lint` before handing work back; do not add a second lint mechanism beside it |

## Angular / NgRx state (always)

- Standalone components, `ChangeDetectionStrategy.OnPush`, signals for state, signal-based `input()` / `output()` and `inject()`, and the built-in control flow (`@if` / `@for` / `@empty`) — all already the norm here.
- Non-trivial state belongs in an **NgRx Signal Store** (`@ngrx/signals`) — invoke the `ngrx-signal-store` skill rather than hand-rolling a `BehaviorSubject` service.
- Keep `protectedState` on; write state only via `patchState` with standalone updaters.
- Use `rxMethod` (not `signalMethod`) whenever requests can overlap — `switchMap` is what prevents a stale response overwriting a fresh one. One store per entity type; `withEntities` for keyed collections.
- This is **not** classic NgRx: no actions, reducers, or effects unless the Events plugin is a deliberate choice.
- **Forms are Signal Forms** (`@angular/forms/signals`): `form()` over a `signal(model)`, schema rules (`required`, `maxLength`, `validate`, …), `[formField]` bindings — a product-owner decision (2026-08-11, first applied in US-304). Never introduce `ReactiveFormsModule`/`FormsModule` in new code. The API is `@experimental` in Angular 21.2, so re-check it against the installed types on every Angular upgrade; `@angular/forms/signals/compat` is the bridge if an integration ever demands an `AbstractControl`. Server validation maps through `serverMessagesFor` (`core/errors/`) fed to a declarative `validate` rule — `applyServerErrors` is the `AbstractControl` counterpart and gains no new call sites.
- For Angular questions that are not about state, use the `angular-developer` skill and the `angular-cli` MCP server instead of relying on memory. Some code comments say "Angular 19 pattern" — they are stale; the app is on Angular 21.

## Skills

The skill roster (names + descriptions) is always in context; routing notes the roster lacks:

- `ngrx-signal-store` is the source of truth for Angular state management — prefer it over `angular-developer` there. Its docs snapshot is pinned to `@ngrx/signals` 21.1.1 (the app resolves 21.1.0 from `^21.0.1`) and refreshed with `/ngrx-signals-sync`.
- `ef-core` covers EF Core work and is preloaded by `csharp-code-reviewer`; `microsoft-docs` routes documentation lookups across Microsoft Learn and Context7.
- The three `github-actions-*` skills are the lanes preloaded by the `github-actions-reviewer` subagent; each is also invokable on its own.
- `prd` is preloaded by `prd-generator`; `technical-writing` (document-type templates) by `se-technical-writer`.
- `microsoft-agent-framework` is in public preview — ground its advice in live docs.
- `angular-developer` is vendored from `angular/skills` and pinned by hash in `.claude/skills-lock.json`.

## Detailed standards — `.claude/rules/`

Six rules, auto-loaded when you edit a matching file; the globs live in each rule's frontmatter under a `paths:` list (the rules carry no `description:`): `csharp`, `aspnet-rest-apis`, `azure-functions-csharp`, `blazor-wasm`, `csharp-mcp-server`, `terraform`. When reviewing or planning without editing, `Read` the matching rule directly.

## MCP servers — see `@.mcp.json`

`.claude/settings.json` sets `enableAllProjectMcpServers: true`, so the servers configured in `@.mcp.json` are available. Use them when relevant:

> **Trust gate:** since Claude Code v2.1.196, a checked-in `.claude/settings.json` cannot approve its own repo's MCP servers while the folder is **untrusted** — the key is ignored and servers sit at "Pending approval" until the workspace trust dialog is accepted. To have these servers auto-approve in every repo (even before trusting), add a name-based list to your **user-level** `~/.claude/settings.json`: `"enabledMcpjsonServers": ["microsoft-learn", "terraform", "angular-cli", "context7"]`. If a server shows **Rejected**, a stale per-project choice is cached — run `claude mcp reset-project-choices` in that repo.

- **`microsoft-learn`** — ground version-specific .NET/Azure answers in official docs (`microsoft_docs_search` → `microsoft_code_sample_search` → `microsoft_docs_fetch`) instead of memory.
- **`angular-cli`** — ground Angular answers in the installed version (`list_projects` → `get_best_practices` → `search_documentation` → `find_examples`).
- **`terraform`** — infrastructure-as-code.
- **`context7`** — docs outside learn.microsoft.com (VS Code, GitHub, Aspire); resolve the library ID first, then query.

## Delegation rules

Every subagent is pinned to extra-high reasoning effort; `github-actions-reviewer` runs on `opus`, the other four on `sonnet`. Tools and preloaded skills live in each agent's frontmatter. Reviewer loops are capped: apply Critical/High findings, re-review only the changed files, at most two rounds, then surface anything still open to the user.

- **When the user asks to write a PRD, spec a feature, define requirements, or break a feature into epics/user stories**, delegate to `prd-generator` — do not write PRDs inline. Its report always starts with a `PRD-STATUS:` line. If it starts `PRD-STATUS: NEEDS-INPUT`, show its questions to the user verbatim (do not answer them yourself) and re-invoke the agent with the answers. It only creates GitHub issues when re-invoked with a statement that the user explicitly approved issue creation for the PRD path. `docs/prd/` is owned by `prd-generator` and currently holds `enterprise-ui-rebuild.md`. A PRD is a pre-implementation artifact: writing one gets no `se-technical-writer` delegation and no changelog entry. Implementation plans for a feature that has a PRD should reference its story IDs (`US-xxx`).
- **After implementing or modifying C# code**, delegate a quality review to `csharp-code-reviewer`. It reports findings; it does not edit files.
- **After implementing or modifying Angular code**, delegate a quality review to `angular-code-reviewer`. It reports findings; it does not edit files.
- **After writing or modifying GitHub Actions workflows** (`.github/workflows/*.yml` or composite actions), delegate a review to `github-actions-reviewer`. It reports findings; it does not edit files. `.github/workflows/ui-ci.yml` (US-108) is the only workflow — a `build` job running the UI gates and a `contract` job restoring one .NET project to check the vendored Andes contract. `.github/dependabot.yml` keeps its SHA-pinned actions current. Note the trade-off it carries: its `paths` filters mean a skipped run never reports, so these checks must not be made required in branch protection as written.
- **After a feature is implemented and the relevant reviewer verdict passes**, ALWAYS delegate to `se-technical-writer` to author or update Markdown docs under `docs/` **and** add the entry to the root `CHANGELOG.md` under `[Unreleased]`. Also delegate to it whenever implementation details need documenting on their own.

## Changelog & feature tracking

Root `CHANGELOG.md`, [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format, currently one `## [Unreleased]` section.

- One entry per PR under `## [Unreleased]`, in the matching subsection (`### Added`, `### Changed`, `### Fixed`, `### Removed`, `### Deprecated`, `### Security`) — concise, reader-facing phrasing, not a commit list.
- `se-technical-writer` owns it; routine cleanups with no behavior change still get a one-line entry, even when they need no docs.
- On release, `[Unreleased]` is renamed to the version and date, and a fresh `[Unreleased]` section is started.

## Common commands

```bash
# in enterprise-gpt-api/
dotnet build                                    # compile the solution
dotnet test                                     # all tests (integration needs Docker running)
dotnet test --filter "Category!=Integration"    # unit tests only
dotnet format                                   # formatting (note: no .editorconfig, so this is near-inert)

# in enterprise-gpt-ui/
npm start            # ng serve on http://localhost:4200 (the only origin the API's CORS policy allows)
npm run build        # ng build — fails above the 720 kB initial-bundle budget, then checks the initial graph
npm run lint         # eslint --max-warnings=0, then the icon, forbidden-API and token checks
npm run check:contract  # the vendored Andes contract against the restored NuGet package
npm test             # ng test — Vitest, single run
npm run test:watch   # Vitest, watch mode
npm run format       # Prettier (format:check for the read-only variant)
```

The .NET side still has no CI workflow. The dev client needs `dotnet dev-certs https --trust` — until then every call to `https://localhost:7045` fails while *looking* like a CORS error.

## Maintenance

- `/ngrx-signals-sync` — check the official NgRx Signals docs for drift and refresh the `ngrx-signal-store` skill. Cheap and silent when nothing changed; when something did, it leaves the diff in the working tree for review rather than committing.
- `/repo-audit` — **inert in this repository.** It shells out to `node scripts/repo-audit.mjs` and audits `.claude/` ↔ `.github/` parity; neither `scripts/` nor a `.github/` harness tree exists here.
