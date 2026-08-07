# CLAUDE.md

Project memory for **Enterprise GPT**. It loads automatically every session and governs how Claude Code works in this repository.

Layout: this file lives at `.claude/CLAUDE.md` (**not** the repo root); detailed standards in `.claude/rules/` auto-apply by file path; skills, subagents, and the `/ngrx-signals-sync` + `/repo-audit` commands live under `.claude/`; `settings.json` pins `model: opus`, `effortLevel: xhigh`, and 10 official plugins.

## About this application

An enterprise ChatGPT/Claude-style AI chat platform: users authenticate with Microsoft Entra ID, hold streaming (SSE) conversations with LLMs, upload documents into conversations (text extraction → embeddings → SQL Server vector search), and administrators manage the model catalog and MCP tool servers.

### `enterprise-gpt-api/` — .NET 10 backend (`Enterprise.Gpt.sln`)

Six projects + two test projects, all `net10.0`, `Nullable`/`ImplicitUsings` enabled. No `LangVersion` pin (so C# 14), **no `.editorconfig`**, no central package management — versions are pinned per-csproj.

- **Layering** — `Api → Service → Repository → Entity`, plus `Dto` and `Common`. Note the non-obvious edges: **`Entity → Dto`** and **`Repository → Dto`**, so `Dto` sits *below* `Entity`, not above. There is no `Abstractions` project — interfaces live beside their implementations (`IModelService` and `ModelService` share `Service/ModelService.cs`). `InternalsVisibleTo("Enterprise.Gpt.Unit.Test")` in `Api` and `Service` lets unit tests call `internal static` endpoint handlers directly.
- **Endpoints** — five **minimal-API** modules in `Enterprise.Gpt.Api/Endpoints/` (Document, Mcp, Model, Permission, User); `ModelEndpoints.cs` is the template for new work. Exactly one controller survives — `Controllers/ConversationsController.cs` — kept because of the SSE `text/event-stream` action that sets headers lazily on the first fragment. Everything is `.RequireAuthorization()` at the group level.
- **Endpoint filters** (`Api/Filters/`) — `AdminEndpointFilter` is a real `IEndpointFilter` (`.AddEndpointFilter<AdminEndpointFilter>()`). `PermissionEndpointFilter.Require(...)` and `MaxUploadSizeEndpointFilter.Require(...)` are **static factories**, not `IEndpointFilter`s. Admins are deliberately **not** implicitly granted every permission.
- **Errors** — a custom `ErrorDto` envelope (`statusCode`, `errors[]`, `traceId`, `timestamp`), written by three chained `IExceptionHandler`s. `ValidationException` → 400, `ConversationBusyException` → 409, `NotFoundException`/`KeyNotFoundException` → 404, `Forbidden`/`McpAuthorizationRequired` → 403 (deliberately not 401 — a 401 sends clients into token-refresh loops that cannot fix a consent requirement), `McpServerUnavailableException` → 502, `OperationCanceledException` → 499 with no body, anything else → 500 with the message suppressed. Handlers short-circuit on `Response.HasStarted` so a faulting SSE stream is not corrupted. Follow this pattern for new endpoints.
- **Persistence** — `EnterpriseGptDbContext` on SQL Server (EF Core 10, `UseCompatibilityLevel(170)` = **SQL Server 2025**; pre-2025 engines and LocalDB reject it). Every FK is forced to `DeleteBehavior.NoAction`; the eight `IEntityTypeConfiguration`s are applied explicitly, not by assembly scan. Soft delete via nullable `DateDeactivated` on `BaseEntity` with **no `HasQueryFilter`** — every query filters manually. Optimistic concurrency via `[Timestamp] byte[] Version`. `Repository/Migrations/` exists only as a csproj `<Folder Include>` and holds **zero migrations**, yet `Database.Migrate()` runs at startup (skipped in the `Testing` environment).
- **Cosmos DB** — conversation *message* history, partitioned by `/userId`, via the **raw `CosmosClient` SDK**, not an EF Core provider.
- **LLM access** — Microsoft.Extensions.AI keyed `IChatClient` (Azure AI Foundry) with function invocation and `UseOpenTelemetry()`; MCP tool servers via `ModelContextProtocol` 2.0.0; document ingestion runs on a custom background-job pipeline (queue + hosted processor). Mapping is hand-written static extension classes in `Service/Mappers/`, each exposing both a materialized `MapToXDto` and an `Expression<Func<X, XDto>>` for server-side projection — no AutoMapper.

### `enterprise-ui/` — Angular 21 frontend

Standalone components, `@ngrx/signals` 21 stores in `src/app/store/`, MSAL (Entra ID) auth wired entirely in `src/app/app.config.ts`, Bootstrap 5 + SCSS. Markdown rendering is **markdown-it + markdown-it-highlightjs**. Theming is CSS custom properties in `src/styles.scss` with a `[data-theme="dark"]` override block. Tests run on **Karma + Jasmine**, and there are currently **zero `*.spec.ts` files**. No ESLint or Prettier; `tsconfig.json` is strict, including `strictTemplates` and `noPropertyAccessFromIndexSignature`.

### `docs/`

Feature designs: `documents/upload-workflow.md`, `models/model-management.md`, `users/user-management.md`, plus UI mockups under `ui/mockups/`.

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
| Problem Details (RFC 9457) | Custom `ErrorDto` via chained handlers; `AddProblemDetails()` is registered but unreachable | **Repo-wide invariant** — new endpoints inherit `ErrorDto`. Migrating the envelope is a deliberate, separate change |
| Structured logging (Serilog) | No Serilog package; plain `ILogger<T>` | `ILogger<T>` with structured templates; do not add Serilog casually |
| `.editorconfig` formatting | None exists under `enterprise-gpt-api/` | Follow the surrounding file; no tooling enforces style |
| Schema via migrations | `Migrations/` is empty; `Migrate()` runs at startup; tests use `EnsureCreated()` | Flag before adding the first migration — it changes startup behavior |
| API versioning | None; routes are plain `api/{resource}` | Match the existing routes |
| Zoneless Angular | `provideZoneChangeDetection({ eventCoalescing: true })` in `app.config.ts` | Keep new code zoneless-ready; do **not** assume zoneless semantics |
| `OnPush` everywhere | 1 of 9 components | `OnPush` on new components |
| `rxMethod` / `withEntities` / `protectedState` | None used; `store/store.service.ts` is ~20 raw writable `signal()` fields | Follow the skill for new stores; treat `store.service.ts` as legacy and do not extend it |

## Angular / NgRx state (always)

- Standalone components, `ChangeDetectionStrategy.OnPush`, signals for state, signal-based `input()` / `output()` and `inject()`, and the built-in control flow (`@if` / `@for` / `@empty`) — all already the norm here.
- Non-trivial state belongs in an **NgRx Signal Store** (`@ngrx/signals`) — invoke the `ngrx-signal-store` skill rather than hand-rolling a `BehaviorSubject` service or adding to `store.service.ts`.
- Keep `protectedState` on; write state only via `patchState` with standalone updaters.
- Use `rxMethod` (not `signalMethod`) whenever requests can overlap — `switchMap` is what prevents a stale response overwriting a fresh one. One store per entity type; `withEntities` for keyed collections.
- This is **not** classic NgRx: no actions, reducers, or effects unless the Events plugin is a deliberate choice.
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

- **When the user asks to write a PRD, spec a feature, define requirements, or break a feature into epics/user stories**, delegate to `prd-generator` — do not write PRDs inline. Its report always starts with a `PRD-STATUS:` line. If it starts `PRD-STATUS: NEEDS-INPUT`, show its questions to the user verbatim (do not answer them yourself) and re-invoke the agent with the answers. It only creates GitHub issues when re-invoked with a statement that the user explicitly approved issue creation for the PRD path. `docs/prd/` is owned by `prd-generator` and **does not exist yet** — the agent creates it on first use. A PRD is a pre-implementation artifact: writing one gets no `se-technical-writer` delegation and no changelog entry. Implementation plans for a feature that has a PRD should reference its story IDs (`US-xxx`).
- **After implementing or modifying C# code**, delegate a quality review to `csharp-code-reviewer`. It reports findings; it does not edit files.
- **After implementing or modifying Angular code**, delegate a quality review to `angular-code-reviewer`. It reports findings; it does not edit files.
- **After writing or modifying GitHub Actions workflows** (`.github/workflows/*.yml` or composite actions), delegate a review to `github-actions-reviewer`. It reports findings; it does not edit files. `.github/workflows/` is currently **empty** — there is no CI yet, so this rule activates with the first workflow.
- **After a feature is implemented and the relevant reviewer verdict passes**, ALWAYS delegate to `se-technical-writer` to author or update Markdown docs under `docs/` **and** add the entry to the root `CHANGELOG.md` under `[Unreleased]`. Also delegate to it whenever implementation details need documenting on their own.

## Changelog & feature tracking

Root `CHANGELOG.md`, [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format. It **does not exist yet** — `se-technical-writer` creates it on its first delegation.

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

# in enterprise-ui/
npm start        # ng serve
npm run build    # ng build
npm test         # ng test — Karma + Jasmine; currently zero *.spec.ts files
```

There is no lint or format script in `enterprise-ui`, and no CI workflow in either stack.

## Maintenance

- `/ngrx-signals-sync` — check the official NgRx Signals docs for drift and refresh the `ngrx-signal-store` skill. Cheap and silent when nothing changed; when something did, it leaves the diff in the working tree for review rather than committing.
- `/repo-audit` — **inert in this repository.** It shells out to `node scripts/repo-audit.mjs` and audits `.claude/` ↔ `.github/` parity; neither `scripts/` nor a `.github/` harness tree exists here.
