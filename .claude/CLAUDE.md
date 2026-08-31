# CLAUDE.md

Project memory for **Enterprise GPT**. It loads automatically every session and governs how Claude Code works in this repository.

Layout: this file lives at `.claude/CLAUDE.md` (**not** the repo root); detailed standards in `.claude/rules/` auto-apply by file path; skills, subagents, and the `/ngrx-signals-sync` + `/repo-audit` commands live under `.claude/`; `settings.json` pins `model: opus`, `effortLevel: xhigh`, and 10 official plugins.

## About this application

An enterprise ChatGPT/Claude-style AI chat platform: users authenticate with Microsoft Entra ID, hold streaming (SSE) conversations with LLMs, upload documents into conversations (text extraction → embeddings → SQL Server vector search), and administrators manage the model catalog and MCP tool servers.

### `enterprise-gpt-api/` — .NET 10 backend (`Enterprise.Gpt.sln`)

Six projects + two test projects, all `net10.0`, `Nullable`/`ImplicitUsings` enabled. No `LangVersion` pin (so C# 14), **no `.editorconfig`**, no central package management — versions are pinned per-csproj.

- **Layering** — `Api → Service → Repository → Entity`, plus `Dto` and `Common`. Note the non-obvious edges: **`Entity → Dto`** and **`Repository → Dto`**, so `Dto` sits _below_ `Entity`, not above. There is no `Abstractions` project — interfaces live beside their implementations (`IModelService` and `ModelService` share `Service/ModelService.cs`). `InternalsVisibleTo("Enterprise.Gpt.Unit.Test")` in `Api` and `Service` lets unit tests call `internal static` endpoint handlers directly.
- **Endpoints** — eight **minimal-API** modules in `Enterprise.Gpt.Api/Endpoints/` (Conversation, Document, Mcp, Model, Permission, Project, Report, User); `ModelEndpoints.cs` is the template for new work. **No controllers survive and there is no `Controllers/` directory** — `ConversationEndpoints.cs` replaced the last one, and `Program.cs` never calls `MapControllers()`. The SSE action lives there as `StreamConversationAsync`, which sets its `text/event-stream` headers lazily on the first fragment so a pre-stream failure still returns `application/problem+json`. Everything is `.RequireAuthorization()` at the group level; conversation routes carry no permission filter, because ownership is enforced in the service and another user's conversation is a 404, not a 403.
- **Endpoint filters** (`Api/Filters/`) — `PermissionEndpointFilter.Require(...)` and `MaxUploadSizeEndpointFilter.Require(...)` are **static factories**, not `IEndpointFilter`s; there is no `IEndpointFilter` type left in the project. `Require(params Guid[])` is **AND** — the caller must hold every listed permission — and validates its ids against `PermissionIds.Names` at _map_ time, so a bad id fails app start rather than emitting a nameless 403. Admin routes are just `Require(PermissionIds.Administrator)`; admins are deliberately **not** implicitly granted every permission.
- **Permission cache** — authorization reads the singleton `IUserPermissionCache` (`Service/Caching/UserPermissionCache.cs`), not the database: entries are written at sign-in (`POST api/users/me`) and loaded lazily on a miss (single-flight, `Lazy<Task<T>>`, like `McpClientCache`). Invalidation is **per user**, never a full reload, from `PermissionService`, `UserService`, and `McpServerService.DeactivateMcpServerAsync`. A user with zero grants caches the _empty set_ — treating that as a miss would restore the per-request query. Entries carry an absolute `Permissions:Cache:EntryLifetime` (default 5 min) purely as the scale-out staleness bound, since invalidation is per-instance. `McpToolProvider.AcquireToolsAsync` and `McpServerService.GetPermittedMcpServersAsync` deliberately stay on the database — see the comments there before "optimizing" them onto the cache.
- **Errors** — RFC 9457 **Problem Details** (`application/problem+json`) everywhere, written through `IProblemDetailsService` by three chained `IExceptionHandler`s, by the endpoint filters, and by `app.UseStatusCodePages()` — so bare 401s, unrouted 404s and 405s carry a body too. Domain `type` URIs live in `Api/Problems/ProblemTypes.cs` under the relative base `/problems/` (`validation-error`, `upload-too-large`, `resource-not-found`, `forbidden`, `permission-required`, `conversation-busy`, `mcp-server-unavailable`, `provider-not-configured`, `storage-not-configured`); they are **opaque identifiers clients match verbatim, not links**, so changing one is a breaking API change. A problem with no domain type carries the RFC 9110 status-section link ASP.NET Core supplies. `CustomizeProblemDetails` (`Problems/ProblemDetailsRegistration.cs`) stamps `traceId` (`Activity.Current?.Id`, else `HttpContext.TraceIdentifier`) and `instance` on every problem, in one place. `ValidationException` → 400 as `HttpValidationProblemDetails`, whose `errors` dictionary is keyed by the failing **property name**; `ConversationBusyException` → 409, `NotFoundException`/`KeyNotFoundException` → 404, `ForbiddenException` → 403, `McpServerUnavailableException` → 502 (which since US-414 also carries a token acquisition that needs user interaction — these servers are consented tenant-wide, so that is a broken registration, not a user-actionable state), `OperationCanceledException` → 499 with no body, anything else → 500 with the message suppressed. Handlers short-circuit on `Response.HasStarted` so a faulting SSE stream is not corrupted. New endpoints declare error responses with `.ProducesProblem(status)` and `.ProducesValidationProblem()` — **never** `.Produces<T>(status)` — and every route group carries a group-level `.ProducesProblem(401)`.
- **Persistence** — `EnterpriseGptDbContext` on SQL Server (EF Core 10, `UseCompatibilityLevel(170)` = **SQL Server 2025**; pre-2025 engines and LocalDB reject it). Every FK is forced to `DeleteBehavior.NoAction`; the 26 `IEntityTypeConfiguration`s are applied explicitly, not by assembly scan. Soft delete via nullable `DateDeactivated` on `BaseEntity` with **no `HasQueryFilter`** — every query filters manually. Optimistic concurrency via `[Timestamp] byte[] Version`. `Repository/Migrations/` holds a single `Initial` migration — the whole schema plus the `HasData` seeds — and `Database.Migrate()` runs at startup (skipped in the `Testing` environment), so a database built from empty is migrated and seeded. One convention it carries: a new bool column that must backfill is configured `HasDefaultValue(x).ValueGeneratedNever()`, because a store-generated bool is silently dropped from EF's `HasData` seed differ. `Migrate()` expects `__EFMigrationsHistory` to hold the `Initial` row and nothing else, so a database this application did not build from empty must be baselined first — `docs/operations/runbooks.md` has the steps.
- **Cosmos DB** — the conversation transcript, **one document per message** plus a `type`-discriminated header, under a synthetic partition key `/pk` holding `{userId}:{conversationId}`, via the **raw `CosmosClient` SDK** (System.Text.Json serializer, fixed-width UTC dates because `ORDER BY dateCreated` is a lexicographic string compare), not an EF Core provider. Access goes through `Service/Transcripts/ITranscriptStore` — transactional batches, continuation-token paging, partition purge — and **every member requires a partition key**; `IAzureCosmosService` is deleted. The container id key is `CosmosDb:TranscriptContainerId`; the old `CosmosDb:ContainerId` now fails startup by design. See `docs/conversations/transcripts.md`, and `docs/operations/runbooks.md` before deploying, because the cutover destroys every existing transcript.
- **LLM access** — Microsoft.Extensions.AI keyed `IChatClient` (Azure AI Foundry) with function invocation and `UseOpenTelemetry()`; MCP tool servers via `ModelContextProtocol` 2.0.0; document ingestion runs on a custom background-job pipeline (queue + hosted processor). Mapping is hand-written static extension classes in `Service/Mappers/`, each exposing both a materialized `MapToXDto` and an `Expression<Func<X, XDto>>` for server-side projection — no AutoMapper.

### `enterprise-gpt-ui/` — Angular 21 frontend

The old `enterprise-ui/` is **deleted**; the client at `enterprise-gpt-ui/` was rebuilt from scratch and nothing was migrated.

Angular **21.2.19**, standalone components, **zoneless** (the v21 default; `provideZonelessChangeDetection()` is stated explicitly in `app.config.ts` anyway), `@ngrx/signals` **21.1.1** pinned exact to match the skill's doc snapshot. Bootstrap 5.3 + **SCSS**: `src/styles.scss` is an `@import` chain, not `@use`, because Bootstrap 5.3 still shares one global `!default` namespace across its partials. Order is load-bearing — Bootstrap's `root` before `tokens`, its components before `bootstrap-overrides`, `motion` last. Components read tokens through `var(--…)` and **never** `@use 'tokens'`, which emits `:root` and would re-scope it to the component. Markdown is **`ngx-markdown` 21.3.0 over marked + Prism**, with DOMPurify behind its `SANITIZE` provider — wired by US-601 through `provideChatMarkdown()` on the `Chat` component, so the whole stack rides the lazy chat chunk and a build gate keeps it out of the initial graph. Supplying a `SANITIZE` function _replaces_ Angular's `DomSanitizer` in that pipeline, so DOMPurify's closed profile is the only DOM-boundary layer, backed by a `renderer.html = () => ''` override that drops raw HTML at the parser. markdown-it was removed; `ngx-markdown` performs the `bypassSecurityTrustHtml` internally, which is why application code has zero call sites. MSAL `msal-angular` 6 / `msal-browser` 5 is installed but **not yet wired** — that is US-201. Tests run on **Vitest** via `@angular/build:unit-test` (`watch: false`, `providersFile: src/testing/test-providers.ts`, which enables `provideCheckNoChangesConfig({ exhaustive: true })`). Prettier is configured (`npm run format`) and **ESLint is wired** by US-108 — flat config in `eslint.config.mjs` over ESLint 10 + typescript-eslint 8 + angular-eslint 21.4 + `@ngrx/eslint-plugin` 21.1.1 (`signalsTypeChecked`, which needs `projectService: true`), with `no-restricted-syntax` banning `bypassSecurityTrustHtml`. `npm run lint` also runs three Node checks — `check:icons`, `check:forbidden`, `check:tokens` — and `npm run build` runs `check-initial-chunk.mjs` over the esbuild metafile. Unused-symbol detection is tsc's (`noUnusedLocals`/`noUnusedParameters`), not ESLint's, because tsc counts a `{@link}` as a use. `tsconfig.json` is strict, including `strictTemplates`, `noPropertyAccessFromIndexSignature`, and `noUncheckedIndexedAccess`.

- **Layout** — dependencies run one way: `features → shared → core → domain`. `core/` holds app-wide singletons and pure policy, `domain/` is framework-free (so the SSE codec and activity fold test in Node with no `TestBed`), `shared/` is presentational only, `features/` is route-owned with its own stores. Path aliases `@core/* @domain/* @shared/* @features/* @testing/*`. Components use the v21 convention — `foo.ts`/`foo.html`/`foo.scss`, no `.component` suffix. No barrel `index.ts` files.
- **Runtime config** — no `environments/`, no `fileReplacements`. `main.ts` fetches `config.json` (`cache: 'no-store'`, resolved against `document.baseURI`), validates it with a narrow assertion, and provides `APP_CONFIG` **before** `bootstrapApplication`. A missing or invalid config renders the static fatal shell in `index.html` and does not bootstrap. `public/config.json` is the committed dev copy and is replaced per environment.
- **Errors** — `core/errors/` mirrors the API's twelve problem types verbatim and normalizes every failure into one discriminated `AppError` (16 arms) through `toAppError` (sync) and `toAppErrorFromResponse` (async, for the raw-fetch stream path). `serverMessagesFor` maps the API's PascalCase `errors` keys onto camelCase form fields using .NET's own camel-casing algorithm. `authErrorDecision` is the single source of truth for whether a failure may trigger a token refresh — only a bare 401 may, and the exhaustive `satisfies` table makes adding an arm without deciding a compile error. `retryInterceptor` retries GETs only, on 502/503/504 and transport failures, with full-jitter backoff, and never on 409 or an app-typed 503.
- **State** — the five composable store features live in `core/state/` (`withRequestStatus`, `withOffsetPagination`, `withPendingIds`, `withClientQuery`, `withResetOnSignOut`); US-104 built them and every store composes them. `withResetOnSignOut` **must be composed last** — it snapshots `getState` during construction, and a state-contributing feature after it would survive sign-out; a dev-only `onInit` guard throws naming the offending keys. Cross-store coordination goes through the `@ngrx/signals/events` plugin — a deliberate opt-in: `sessionEvents.signedOut` and `conversationEvents.updated`/`.deleted` (US-304/305/306 — server-confirmed facts only, with one bounded exception where the favourite endpoint's 204 carries no body; optimistic patches and rollbacks stay inside `ConversationListStore`) live in `core/events/`, beside `adminEvents` — three user events carrying a DTO (US-1201–1204) plus two `type<void>()` catalog events, US-1207's `modelCatalogChanged` (a save that sets `isDefault` demotes a row no response describes) and US-1208's `mcpCatalogChanged` (a 204 carries no `dateDeactivated`, and a rename also renames the server's linked permission) — each with two observers, one route-scoped and one root, and both refetching — beside `turnEvents` (US-409). Clearing state is not cancelling work: a store that loads asynchronously must also `takeUntil(injectSignedOut())`.
- **Bundle budget** — the production build warns above **680 kB** initial raw and fails above **720 kB**, with a `styles` budget at 66/80 kB. MSAL is ~249 kB of the initial graph and none of it is deferrable, because `main.ts` must `initialize()` the instance before `bootstrapApplication`. The rule to preserve is which libraries stay off the graph: the markdown stack (`ngx-markdown`, `marked`, `prismjs`, `dompurify`) rides the lazy chat chunk, and `mermaid`, `katex` and `@microsoft/applicationinsights-web` are reached only through `await import(…)` behind DI tokens and `config.json` flags, in lazy chunks of their own. `check-initial-chunk.mjs` fails the build if a static import from `main.ts` — or from the chat chunk, for the two flagged libraries — reaches any of them, naming the import path that did it. It also asserts mermaid and katex exist *somewhere*, since every other check passes when a library is simply gone. Re-baseline only when a dependency genuinely has to be initial — check first whether it does.

### `docs/`

Engineer-facing reference, indexed by `docs/README.md`. `architecture/` holds the cross-cutting docs every other one links up into — `overview.md`, `backend.md`, `frontend.md`, `data-model.md`, `auth-and-permissions.md`. Subsystems then sit in their own folders: `conversations/` (turn lifecycle, the SSE contract and its client, transcripts, the usage audit trail, export), `documents/` (ingestion, retrieval, sheet query, summarization, downloads), `tools/` (MCP servers, the File Agent), `models/` (catalog, providers), `frontend/` (state, errors, design system, answer rendering, layout), `admin/` (users and permissions, projects), `observability/` (telemetry, KQL cookbook), `operations/` (configuration, runbooks) and `development/testing-and-ci.md`. `operations/runbooks.md` is the one file holding operator procedures, including the destructive Cosmos transcript cutover. These docs carry no story ids, no release history and no rejected-alternative narratives — that is deliberate, so keep new writing to the same rule.

## Communication & comments (always)

**Responses**

- Lead with the answer or the change. No preamble, no filler, no restating the request.
- Do not re-summarize a plan or diff the user already saw; report only what changed or went wrong.
- Match length to substance — a one-line answer is a complete answer.

**Code comments**

Write for a senior engineer who knows the language and can read the code. A comment supplies what the code cannot — nothing else.

- **Default to no comment.** Add one only where a competent reader would still ask _why_. No per-function or per-member quota.
- **One or two lines.** Three needs a reason; past six the content belongs in `docs/` with a one-line pointer from the code.
- **Never reference PRD story ids, epics, or design frames** — no `US-1402`, no `FR-11`, no "PRD decision 1", no "frame `2f`". Traceability belongs in the PR description and `docs/`; in source it goes stale and says nothing about the code in front of the reader. Test names describe behaviour, not story ids.
- **Never explain the language, framework, or API** — not what `linkedSignal` is for, not how `dragleave` fires, not what a primary constructor does.
- **Never narrate.** No "added X", "now uses Y". No restating the statement below it.
- **Do write**: why the non-obvious choice beat the obvious one, an invariant a caller must hold, a workaround with its link or issue, a spec or vendor constraint. State the cause once — not the argument, the alternatives weighed, or the history.
- **Compress rather than delete.** A real _why_ buried in a paragraph becomes one sentence; it does not disappear.

```ts
// Bad — narrates, teaches the platform, cites a story
/**
 * US-204 asks for two different things and they need two different mechanisms. "No
 * attach control renders" is `@if` in the template — the same answer US-203 gave the
 * Admin nav entry, and it stays that way. "Drag-and-drop is inert" cannot be: the
 * prompt box still has to render, it just must not react. That is this directive.
 */

// Good — the one thing the code cannot say
/** Depth, not a flag: `dragleave` fires on every child crossing. */
```

## C# coding standards (always)

Full standards live in `.claude/rules/csharp.md` (auto-applies to `*.cs`). The always-on core:

- Target the latest C# version (currently **C# 14**); prefer pattern matching, switch expressions, and `nameof(...)`.
- Prefer **primary constructors**; capture each injected dependency into a `private readonly` `_camelCase` field and use the field in method bodies.
- Prefer **collection expressions** (`[]`, `[1, 2, 3]`, `[.. items]`) over `new List<T>()`, `new T[] { }`, or `Array.Empty<T>()`.
- PascalCase for types, methods, and public members; `_camelCase` private fields; camelCase locals and parameters; `I`-prefixed interfaces.
- Declare variables non-nullable; validate `null` at entry points only; use `is null` / `is not null` — **never** `== null` / `!= null`.
- Suffix async methods with `Async`; **never** block with `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`; no `async void` outside event handlers; flow a `CancellationToken` through long-running operations (see the `csharp-async` skill). `ConfigureAwait(false)` is applied in hosted-service, cache, lock, and MCP-client paths — follow the surrounding file.
- **Never log PII or secrets**; prefer `DefaultAzureCredential` + Azure Key Vault / Managed Identity over secrets in code or config.
- **XML docs** — a one-sentence `<summary>` on public API surface; interfaces carry the docs and implementations use `/// <inheritdoc />`. `<param>`/`<returns>` only where they add what the signature does not (never "The resource API key" on `ApiKey`). `<remarks>` is for a caveat a caller must know, two sentences at most — not rationale, not history, not alternatives weighed. `<example>`/`<code>` only where correct usage is genuinely non-obvious. `internal` and test types are not API surface: comment them only where a _why_ exists. **This overrides the `csharp-docs` skill wherever the two disagree** — the skill describes .NET's framework-reference house style, which is not this codebase's.
- xUnit **v3** tests under `enterprise-gpt-api/tests/` in `Enterprise.Gpt.Unit.Test` and `Enterprise.Gpt.Integration.Test`, named `MethodName_Scenario_ExpectedBehavior`; Arrange-Act-Assert structure but **no** `// Arrange` / `// Act` / `// Assert` comments; plain xUnit `Assert` (no FluentAssertions — v8+ is commercially licensed); isolate with **NSubstitute** (see the `csharp-xunit` skill).
- Unit tests: services taking `EnterpriseGptDbContext` run on SQLite in-memory via `SqliteDbContextFixture` (a model customizer neutralizes the rowversion and vector columns SQLite cannot handle); each test class news up its own fixture rather than using `IClassFixture`. Integration tests: `WebApplicationFactory<Program>` + Testcontainers **SQL Server 2025** (`CustomWebApplicationFactory` + `TestAuthHandler`, header `X-Test-User`); they need Docker and carry both `[Collection("Integration")]` and `[Trait("Category", "Integration")]`.
- When reviewing, make only **high-confidence** suggestions; comment on _why_ a non-obvious design decision was made.

## Standards vs. the current codebase

The rules files set the target. These are the places the code has not migrated yet — write new code the "for new code" way and do **not** bulk-convert existing files unless asked.

| Rule (target)                                  | Codebase today                                                                                                                                                                 | For new code                                                                                                                                                                                                                                            |
| ---------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| File-scoped namespaces                         | 0 file-scoped vs 54 block-scoped in Api+Service; only `tests/` is file-scoped                                                                                                  | File-scoped in new files; leave existing ones alone                                                                                                                                                                                                     |
| Structured logging (Serilog)                   | No Serilog package; plain `ILogger<T>`                                                                                                                                         | `ILogger<T>` with structured templates; do not add Serilog casually                                                                                                                                                                                     |
| `.editorconfig` formatting                     | None exists under `enterprise-gpt-api/`                                                                                                                                        | Follow the surrounding file; no tooling enforces style                                                                                                                                                                                                  |
| Schema via migrations                          | `Migrations/` holds a single `Initial`; `Migrate()` runs at startup; tests use `EnsureCreated()`                                                  | Add a normal `dotnet ef migrations add` migration for a schema change — the first-migration decision is already made                                                                                                                                    |
| API versioning                                 | None; routes are plain `api/{resource}`                                                                                                                                        | Match the existing routes                                                                                                                                                                                                                               |
| Zoneless Angular                               | Zoneless throughout — v21's default, plus an explicit `provideZonelessChangeDetection()`; no `zone.js` anywhere                                                                | Assume zoneless semantics; nothing re-renders without a signal write, `markForCheck`, or `AsyncPipe`                                                                                                                                                    |
| `OnPush` everywhere                            | The only component so far is `OnPush`; `angular.json` sets it as the schematic default                                                                                         | `OnPush` on new components                                                                                                                                                                                                                              |
| `rxMethod` / `withEntities` / `protectedState` | US-104's five composable features are in `core/state/`; `ConversationListStore` (`core/conversations/`) is US-302's reference list store and the shape the remaining ones copy | Copy `ConversationListStore` rather than re-deriving: `withEntities` + the composable features, `withResetOnSignOut` **last**, and one `rxMethod` wired in `onInit` to a signal computation so loading is declarative and `switchMap` handles the races |
| Frontend lint (ESLint, bundle + safety gates)  | Installed by US-108 and green                                                                                                                                                  | Run `npm run lint` before handing work back; do not add a second lint mechanism beside it                                                                                                                                                               |

## Angular / NgRx state (always)

- Standalone components, `ChangeDetectionStrategy.OnPush`, signals for state, signal-based `input()` / `output()` and `inject()`, and the built-in control flow (`@if` / `@for` / `@empty`) — all already the norm here.
- Non-trivial state belongs in an **NgRx Signal Store** (`@ngrx/signals`) — invoke the `ngrx-signal-store` skill rather than hand-rolling a `BehaviorSubject` service.
- Keep `protectedState` on; write state only via `patchState` with standalone updaters.
- Use `rxMethod` (not `signalMethod`) whenever requests can overlap — `switchMap` is what prevents a stale response overwriting a fresh one. One store per entity type; `withEntities` for keyed collections.
- This is **not** classic NgRx: no actions, reducers, or effects unless the Events plugin is a deliberate choice.
- **Forms are Signal Forms** (`@angular/forms/signals`): `form()` over a `signal(model)`, schema rules (`required`, `maxLength`, `validate`, …), `[formField]` bindings — a product-owner decision (2026-08-11, first applied in US-304). Never introduce `ReactiveFormsModule`/`FormsModule` in new code. The API is `@experimental` in Angular 21.2, so re-check it against the installed types on every Angular upgrade; `@angular/forms/signals/compat` is the bridge if an integration ever demands an `AbstractControl`. Server validation maps through `serverMessagesFor` (`core/errors/`) fed to a declarative `validate` rule; the `AbstractControl` counterpart `applyServerErrors` was deleted once it had no call sites left.
- **TSDoc** follows the comment standard above: a one-line `/** */` where the exported name is not enough, and nothing on a symbol the name already explains. No multi-paragraph headers on components, directives, or stores — the design rationale for a screen belongs in `docs/`, not in its class doc.
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

- **PRDs are not part of this repository.** `docs/` holds engineer-facing reference only, and `docs/prd/` no longer exists. The `prd-generator` agent and the `prd` skill remain installed for a user who explicitly asks for one; nothing routes to them by default, and a PRD written that way is a scratch artifact rather than something to check in.
- **After implementing or modifying C# code**, delegate a quality review to `csharp-code-reviewer`. It reports findings; it does not edit files.
- **After implementing or modifying Angular code**, delegate a quality review to `angular-code-reviewer`. It reports findings; it does not edit files.
- **After writing or modifying GitHub Actions workflows** (`.github/workflows/*.yml` or composite actions), delegate a review to `github-actions-reviewer`. It reports findings; it does not edit files. Two workflows, same shape: **`ui-ci.yml`** — a `changes` job, then `build` (the UI gates) and `contract` (one .NET restore checking the vendored Andes contract) — and **`api-ci.yml`**, the .NET one — `changes`, then `unit` (1633 tests, no Docker) and `integration` (377 tests, Testcontainers SQL Server 2025 + the Cosmos vNext emulator). `.github/dependabot.yml` keeps their SHA-pinned actions current and tracks **only** the `github-actions` ecosystem. **Neither carries a `paths:` filter any more**: a workflow skipped by a path filter never reports, while a job skipped by a conditional reports Success, so the filter lives in the `changes` job and the dependents' `if:` is written **fail-open** (`!cancelled() && … != 'false'`). Both are therefore requirable — but branch protection must list **all three** job names per workflow, `Detect …` included, or a `changes` job that dies after writing `false` hands out a green check over untested code. See `docs/development/testing-and-ci.md`.
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

Both sides have CI (`.github/workflows/api-ci.yml`, `ui-ci.yml`); see `docs/development/testing-and-ci.md`. The dev client needs `dotnet dev-certs https --trust` — until then every call to `https://localhost:7045` fails while _looking_ like a CORS error.

## Maintenance

- `/ngrx-signals-sync` — check the official NgRx Signals docs for drift and refresh the `ngrx-signal-store` skill. Cheap and silent when nothing changed; when something did, it leaves the diff in the working tree for review rather than committing.
- `/repo-audit` — **inert in this repository.** It shells out to `node scripts/repo-audit.mjs` and audits `.claude/` ↔ `.github/` parity; neither `scripts/` nor a `.github/` harness tree exists here.
