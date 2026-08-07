# CLAUDE.md

Reusable project memory for **C#/.NET back ends and Angular front ends**. It loads automatically every session and governs how Claude Code works in this project. Drop it into a repository and follow it for all C# and Angular work.

Layout: detailed standards in `.claude/rules/` auto-apply by file path; skills, subagents, and the `/ngrx-signals-sync` command live under `.claude/`; `settings.json` pins the model and reasoning effort (`"effortLevel": "xhigh"`); the root `CHANGELOG.md` is the running record of changes, owned by the `se-technical-writer` subagent.

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

- Target the latest C# version (currently **C# 14**); honor `.editorconfig`; prefer pattern matching, switch expressions, and `nameof(...)`.
- Prefer **primary constructors**; capture each injected dependency into a `private readonly` `_camelCase` field and use the field in method bodies.
- Prefer **collection expressions** (`[]`, `[1, 2, 3]`, `[.. items]`) over `new List<T>()`, `new T[] { }`, or `Array.Empty<T>()`.
- PascalCase for types, methods, and public members; `_camelCase` private fields; camelCase locals and parameters; `I`-prefixed interfaces.
- Declare variables non-nullable; validate `null` at entry points only; use `is null` / `is not null` — **never** `== null` / `!= null`.
- Suffix async methods with `Async`; **never** block with `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`; no `async void` outside event handlers; flow a `CancellationToken` through long-running operations (see the `csharp-async` skill).
- Centralize error handling; return errors as Problem Details (RFC 9457). **Never log PII or secrets**; prefer `DefaultAzureCredential` + Azure Key Vault / Managed Identity over secrets in code or config.
- XML doc comments on all public APIs (see the `csharp-docs` skill).
- xUnit tests in a `[ProjectName].Tests` project, named `MethodName_Scenario_ExpectedBehavior`; Arrange-Act-Assert structure but **no** `// Arrange` / `// Act` / `// Assert` comments (see the `csharp-xunit` skill).
- When reviewing, make only **high-confidence** suggestions; comment on _why_ a non-obvious design decision was made.

## Angular / NgRx state (always)

- Standalone components, `ChangeDetectionStrategy.OnPush`, signals for state. Assume zoneless.
- Non-trivial state belongs in an **NgRx Signal Store** (`@ngrx/signals`) — invoke the `ngrx-signal-store` skill rather than hand-rolling a `BehaviorSubject` service.
- Keep `protectedState` on; write state only via `patchState` with standalone updaters.
- Use `rxMethod` (not `signalMethod`) whenever requests can overlap — `switchMap` is what prevents a stale response overwriting a fresh one. One store per entity type; `withEntities` for keyed collections.
- This is **not** classic NgRx: no actions, reducers, or effects unless the Events plugin is a deliberate choice.
- For Angular questions that are not about state, use the `angular-developer` skill and the `angular-cli` MCP server instead of relying on memory.

## Skills

The skill roster (names + descriptions) is always in context; routing notes the roster lacks:

- `ngrx-signal-store` is the source of truth for Angular state management — prefer it over `angular-developer` there.
- The three `github-actions-*` skills are the lanes preloaded by the `github-actions-reviewer` subagent; each is also invokable on its own.
- `prd` is preloaded by `prd-generator`; `technical-writing` (document-type templates) by `se-technical-writer`.
- `microsoft-agent-framework` is in public preview — ground its advice in live docs.

## Detailed standards — `.claude/rules/`

Rules auto-load when you edit a matching file; the globs live in each rule's frontmatter (`csharp`, `aspnet-rest-apis`, `azure-functions-csharp`, `blazor-wasm`, `csharp-mcp-server`, `terraform`). When reviewing or planning without editing, `Read` the matching rule directly.

## MCP servers — see `@.mcp.json`

`.claude/settings.json` sets `enableAllProjectMcpServers: true`, so the servers configured in `@.mcp.json` are available. Use them when relevant:

> **Trust gate:** since Claude Code v2.1.196, a checked-in `.claude/settings.json` cannot approve its own repo's MCP servers while the folder is **untrusted** — the key is ignored and servers sit at "Pending approval" until the workspace trust dialog is accepted. To have these servers auto-approve in every repo (even before trusting), add a name-based list to your **user-level** `~/.claude/settings.json`: `"enabledMcpjsonServers": ["microsoft-learn", "terraform", "angular-cli", "context7"]`. If a server shows **Rejected**, a stale per-project choice is cached — run `claude mcp reset-project-choices` in that repo.

- **`microsoft-learn`** — ground version-specific .NET/Azure answers in official docs (`microsoft_docs_search` → `microsoft_code_sample_search` → `microsoft_docs_fetch`) instead of memory.
- **`angular-cli`** — ground Angular answers in the installed version (`list_projects` → `get_best_practices` → `search_documentation` → `find_examples`).
- **`terraform`** — infrastructure-as-code.
- **`context7`** — docs outside learn.microsoft.com (VS Code, GitHub, Aspire); resolve the library ID first, then query.

## Delegation rules

Every subagent is pinned to extra-high reasoning effort; model, tools, and preloaded skills live in each agent's frontmatter. Reviewer loops are capped: apply Critical/High findings, re-review only the changed files, at most two rounds, then surface anything still open to the user.

- **When the user asks to write a PRD, spec a feature, define requirements, or break a feature into epics/user stories**, delegate to `prd-generator` — do not write PRDs inline. Its report always starts with a `PRD-STATUS:` line. If it starts `PRD-STATUS: NEEDS-INPUT`, show its questions to the user verbatim (do not answer them yourself) and re-invoke the agent with the answers. It only creates GitHub issues when re-invoked with a statement that the user explicitly approved issue creation for the PRD path. `docs/prd/` is owned by `prd-generator`; a PRD is a pre-implementation artifact — writing one gets no `se-technical-writer` delegation and no changelog entry. Implementation plans for a feature that has a PRD should reference its story IDs (`US-xxx`).
- **After implementing or modifying C# code**, delegate a quality review to `csharp-code-reviewer`. It reports findings; it does not edit files.
- **After implementing or modifying Angular code**, delegate a quality review to `angular-code-reviewer`. It reports findings; it does not edit files.
- **After writing or modifying GitHub Actions workflows** (`.github/workflows/*.yml` or composite actions), delegate a review to `github-actions-reviewer`. It reports findings; it does not edit files.
- **After a feature is implemented and the relevant reviewer verdict passes**, ALWAYS delegate to `se-technical-writer` to author or update Markdown docs under `docs/` (create the folder if it does not exist) **and** add the entry to the root `CHANGELOG.md` under `[Unreleased]`. Also delegate to it whenever implementation details need documenting on their own.

## Changelog & feature tracking

Root `CHANGELOG.md`, [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format:

- One entry per PR under `## [Unreleased]`, in the matching subsection (`### Added`, `### Changed`, `### Fixed`, `### Removed`, `### Deprecated`, `### Security`) — concise, reader-facing phrasing, not a commit list.
- `se-technical-writer` owns it; routine cleanups with no behavior change still get a one-line entry, even when they need no docs.
- On release, `[Unreleased]` is renamed to the version and date, and a fresh `[Unreleased]` section is started.

## Common commands

```bash
dotnet build     # compile
dotnet test      # run xUnit tests
dotnet format    # apply .editorconfig formatting
```

## Maintenance

- `/ngrx-signals-sync` — check the official NgRx Signals docs for drift and refresh the `ngrx-signal-store` skill. Cheap and silent when nothing changed; when something did, it leaves the diff in the working tree for review rather than committing.
- `/repo-audit` — verify the two harness trees still mirror each other (skills, rules↔instructions, agent twins and model parity, registries) and lint config cost hygiene. Cheap and silent when clean; report-only by default; `--fix` applies only mechanical repairs and leaves the diff for review.
