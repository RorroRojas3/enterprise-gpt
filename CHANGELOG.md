# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-08-30

First release of Enterprise GPT: a .NET 10 API and an Angular 21 client for enterprise AI chat with
document grounding, tool servers, and administration.

### Added

- **Conversations and streaming.** Server-sent-event chat turns with an activity timeline, reasoning
  display, Stop with partial output, and resumable conversation history. Transcripts are stored in
  Cosmos DB, one document per message.
- **Documents.** Upload of eight formats into a conversation or a project, with text extraction,
  chunking and embedding on a background job queue; hybrid vector and keyword retrieval
  (`document_search`); deterministic spreadsheet aggregation (`sheet_query`); and hierarchical
  map-reduce summarization (`document_summarize`).
- **File Agent.** A sandboxed agent that writes and runs code to produce documents, behind its own
  permission and its own pinned model.
- **MCP tool servers.** An administered registry with three authentication modes, including
  on-behalf-of Entra tokens and per-user API keys encrypted with ASP.NET Core Data Protection.
- **Model catalog and providers.** Four chat providers — Azure OpenAI (required), Azure AI Foundry,
  Amazon Bedrock and Anthropic — selected per model row, with per-model reasoning and context
  settings.
- **Projects.** Conversation grouping with standing instructions applied at chat time and a shared
  document set.
- **Export.** Conversation download as Markdown, JSON, HTML, Word or PDF, from one shared block
  model, with fonts shipped for containerized deployments.
- **Administration.** User directory and provisioning, permission grants, the model catalog, the MCP
  registry, and a usage report over an append-only billing audit trail.
- **Authentication and authorization.** Microsoft Entra ID sign-in, on-behalf-of token acquisition,
  and permission-gated routes served from an in-process cache with per-user invalidation.
- **Observability.** Structured request logging with query redaction and optional body capture,
  OpenTelemetry through Azure Monitor, browser telemetry, and health and readiness probes.
- **Frontend platform.** Zoneless standalone Angular with NgRx Signal Stores, runtime configuration
  fetched before bootstrap, a total error-normalization layer mirroring the API's problem types, a
  token-driven design system with light and dark themes, and build gates for bundle composition,
  icons, design tokens and forbidden APIs.
- **Documentation.** Engineer-facing reference under [`docs/`](docs/README.md), indexed by
  [`docs/README.md`](docs/README.md).
- **CI.** Two GitHub Actions workflows covering unit tests, integration tests against SQL Server 2025
  and the Cosmos emulator, lint, unit and accessibility test suites, and the production build.

[Unreleased]: https://github.com/RorroRojas3/enterprise-gpt/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/RorroRojas3/enterprise-gpt/releases/tag/v1.0.0
