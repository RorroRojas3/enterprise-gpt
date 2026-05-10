# multi-gpt

Multi-model AI chat platform with two subprojects:

- **`ai-chat-ui/`** — Angular 21 SPA frontend (standalone components, signals, `@ngrx/signals`, MSAL, Bootstrap 5, SCSS).
- **`RR.AI-Chat/`** — .NET 10 Web API backend, layered solution: `RR.AI-Chat.Api` → `RR.AI-Chat.Service` → `RR.AI-Chat.Repository` → `RR.AI-Chat.Entity`, with shared `RR.AI-Chat.Dto` and `RR.AI-Chat.Common` projects.

## Per-folder instructions

When working in **`ai-chat-ui/`**, follow [`ai-chat-ui/CLAUDE.md`](ai-chat-ui/CLAUDE.md). It is auto-loaded for that subdirectory and is the source of truth for Angular conventions in this repo (verified against the official Angular 21 best practices via the `angular-cli` MCP).

When working in **`RR.AI-Chat/`**, follow [`RR.AI-Chat/CLAUDE.md`](RR.AI-Chat/CLAUDE.md). It is auto-loaded for that subdirectory and is the source of truth for .NET 10 / C# 14 conventions in this repo (verified against official Microsoft Learn docs via the `microsoft-learn` MCP).

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
