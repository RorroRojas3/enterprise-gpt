# Enterprise GPT — Engineering Documentation

Reference for engineers working on this codebase: what each part of the application is, how it is
built, and the constraints that are not obvious from the source.

**New here?** Read [architecture/overview.md](architecture/overview.md), then the two structure docs
for whichever side you are working on, then the subsystem you are touching.

## Architecture

| Doc | Covers |
| --- | --- |
| [overview.md](architecture/overview.md) | The two applications, the external services, and three traced request paths |
| [backend.md](architecture/backend.md) | Project graph, endpoint modules, filters, problem details, `Program.cs` |
| [frontend.md](architecture/frontend.md) | Angular layers, bootstrap order, routing, styling chain, build gates |
| [data-model.md](architecture/data-model.md) | SQL schema and conventions, migrations, Cosmos transcript documents |
| [auth-and-permissions.md](architecture/auth-and-permissions.md) | Entra ID, on-behalf-of, the permission filter and its cache, secrets |

## Conversations

| Doc | Covers |
| --- | --- |
| [turn-lifecycle.md](conversations/turn-lifecycle.md) | Send to settled transcript entry, Stop, reopening |
| [streaming.md](conversations/streaming.md) | The SSE wire contract and the client that reads it |
| [transcripts.md](conversations/transcripts.md) | Cosmos storage, token estimation, the context budget |
| [usage-and-reporting.md](conversations/usage-and-reporting.md) | The append-only audit trail, favourites, the usage report |
| [export.md](conversations/export.md) | The five export formats, the block model, composed email |

## Documents

| Doc | Covers |
| --- | --- |
| [ingestion.md](documents/ingestion.md) | Upload to searchable chunks, and the background job that runs it |
| [retrieval.md](documents/retrieval.md) | `document_search` — hybrid vector plus keyword search |
| [sheet-query.md](documents/sheet-query.md) | `sheet_query` — deterministic spreadsheet aggregation |
| [summarization.md](documents/summarization.md) | `document_summarize` — map-reduce with a collapse loop |
| [downloads.md](documents/downloads.md) | Signed download links, and files the assistant generates |

## Tools

| Doc | Covers |
| --- | --- |
| [mcp-servers.md](tools/mcp-servers.md) | The registry, the three auth types, per-user credentials |
| [file-agent.md](tools/file-agent.md) | The sandboxed agent that writes files |

## Models

| Doc | Covers |
| --- | --- |
| [catalog.md](models/catalog.md) | `Core.Ref.Model`, its invariants, and the admin surface |
| [providers.md](models/providers.md) | The four keyed chat clients and how reasoning is requested |

## Frontend

| Doc | Covers |
| --- | --- |
| [state.md](frontend/state.md) | Signal-store conventions, the five composable features, cross-store events |
| [errors.md](frontend/errors.md) | `AppError` normalization, retry policy, the refresh decision |
| [design-system.md](frontend/design-system.md) | Tokens, theming, typography, icons, accessibility contracts |
| [answer-rendering.md](frontend/answer-rendering.md) | The markdown pipeline, streaming split, diagrams and math |
| [layout-and-navigation.md](frontend/layout-and-navigation.md) | Breakpoints, the three modes, the shell, overlays |

## Administration

| Doc | Covers |
| --- | --- |
| [users-and-permissions.md](admin/users-and-permissions.md) | Provisioning paths, grants, the lockout invariants |
| [projects.md](admin/projects.md) | Projects, membership, standing instructions, delete semantics |

## Observability

| Doc | Covers |
| --- | --- |
| [telemetry.md](observability/telemetry.md) | The access log, OpenTelemetry, the browser SDK |
| [kql-cookbook.md](observability/kql-cookbook.md) | Runnable queries against this app's telemetry shape |

## Operations

| Doc | Covers |
| --- | --- |
| [configuration.md](operations/configuration.md) | Every section a deployment sets, and Key Vault |
| [runbooks.md](operations/runbooks.md) | Registering an MCP server; the destructive transcript cutover |

## Development

| Doc | Covers |
| --- | --- |
| [testing-and-ci.md](development/testing-and-ci.md) | Test projects and fixtures, the gate scripts, both workflows |

## Conventions

These docs describe the system as it is now. They do not carry release history, story or epic
identifiers, or the reasoning behind alternatives that were rejected — those belong in pull requests
and the changelog. Where a non-obvious constraint exists it is stated once, as a constraint.

Every subsystem doc ends with a **Key files** table: repo-root-relative paths, one line each, and at
least one test file. That is the fastest route from a doc back into the source.
