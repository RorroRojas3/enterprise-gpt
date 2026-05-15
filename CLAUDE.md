# multi-gpt

Multi-model AI chat platform with two subprojects:

- **`enterprise-ui/`** — Angular 21 SPA frontend (standalone components, signals, `@ngrx/signals`, MSAL, Bootstrap 5, SCSS).
- **`enterprise-gpt-api/`** — .NET 10 Web API backend, layered solution: `Enterprise.Gpt.Api` → `Enterprise.Gpt.Service` → `Enterprise.Gpt.Repository` → `Enterprise.Gpt.Entity`, with shared `Enterprise.Gpt.Dto` and `Enterprise.Gpt.Common` projects.

## Per-folder instructions

When working in **`enterprise-ui/`**, follow [`enterprise-ui/CLAUDE.md`](enterprise-ui/CLAUDE.md). It is auto-loaded for that subdirectory and is the source of truth for Angular conventions in this repo (verified against the official Angular 21 best practices via the `angular-cli` MCP).

When working in **`enterprise-gpt-api/`**, follow [`enterprise-gpt-api/CLAUDE.md`](enterprise-gpt-api/CLAUDE.md). It is auto-loaded for that subdirectory and is the source of truth for .NET 10 / C# 14 conventions in this repo (verified against official Microsoft Learn docs via the `microsoft-learn` MCP).

## Skills (backend)

The `.claude/skills/` folder contains user-invokable skills for backend work. Invoke via the Skill tool or slash commands:

- **`csharp-async`** — async/await + cancellation best practices
- **`csharp-docs`** — XML doc comment standards
- **`csharp-xunit`** — xUnit testing patterns (use when adding the first test project)

## MCP servers

`.mcp.json` registers two servers (project-scoped — must be approved or enabled via `enableAllProjectMcpServers`):

- **`angular-cli`** (`@angular/cli mcp`) — Angular 21 best practices, docs search, project metadata.
- **`microsoft-learn`** — Microsoft/Azure documentation.

For frontend Angular tasks, prefer `mcp__angular-cli__get_best_practices` and `mcp__angular-cli__search_documentation` over generic web search.

## Feature documentation convention

Every non-trivial feature gets a software-engineering doc at:

```
docs/{area}/{feature-name}.md
```

- `{area}` — the broad part of the app the feature belongs to (e.g. `documents`, `conversations`, `auth`, `mcp`, `ui`).
- `{feature-name}` — kebab-case name of the specific feature or workflow (e.g. `upload-workflow`, `streaming`, `multi-provider-routing`).

The doc should explain the **end-to-end** behavior across UI + API: contracts, sequence, key files (linked with relative markdown links), failure modes, configuration, and extension points. Audience is a software engineer onboarding to or extending the feature — not an end user.

When you add a new feature or substantially change an existing one, create or update the matching doc. Reference [`docs/documents/upload-workflow.md`](docs/documents/upload-workflow.md) as the worked example.
