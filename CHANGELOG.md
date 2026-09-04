# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Conversation documents can now be deleted individually
  (`DELETE api/documents/conversations/{conversationId}/{documentId}`), matching the delete that
  already existed for project documents. Only an uploaded file can be removed this way; a file the
  assistant generated is not, since its chip is also referenced from the conversation transcript.
- Uploads can now be cancelled mid-flight: `DELETE api/documents/upload-status/{jobId}` stops a
  queued or running ingestion and removes whatever it had already produced, so a cancelled upload
  always leaves nothing behind. The composer's Stop control uses it for any file still on its way.

### Changed

- Document summaries (`document_summarize`) are now detailed records instead of short gists: length
  scales with the source, every part of it must be covered, and light markdown structure (headings,
  bullets) is used where the source itself has it. A cross-document digest stays deliberately brief
  by contrast — a pointer to the right document, not a second copy of its detail. Every summary
  already cached is regenerated once, at cost, the next time it is requested. A completion cut off at
  the model's output cap is now kept and reported rather than passed off as an ordinary success, and
  the pinned summarization model's output cap was raised to give the longer summaries headroom.

### Fixed

- The composer's attachment chips no longer show a `×` and a Stop control at once while a file
  uploads. Stop now cancels both a running turn and an in-progress upload; a file attached before
  its conversation exists keeps its own `×`, since there is nothing yet for Stop to cancel.
- Cancelling a file upload after the server had already accepted it (past the 202) now stops the
  ingestion itself instead of letting it run unseen to completion before deleting its result — see
  the cancel route above.
- Attaching a file to an already-open conversation no longer uploads it immediately. Every composer
  now waits for Send before posting an attachment, matching the behaviour a brand-new conversation
  already had; previously only its first turn deferred the upload, and every attachment after that
  uploaded on the drop.
- A file attached through a project's composer is now uploaded as a document of the conversation the
  prompt creates, not as a document of the project. Only the project's Files tab adds to the
  project's own document set.
- The File Agent read a format mention like `.docx`, or an output name such as `report-v2.docx`, as a
  missing source file and refused the request unrun. A create now runs whatever the conversation
  holds, and an edit may name the file it writes. A request naming a source the conversation does not
  have still refuses.
- Collapsing or expanding the sidebar left its chevron's tooltip stuck open until the next click
  elsewhere. Icon-button tooltips (the sidebar chevron, and menu triggers that carry a hint) now
  appear on hover and on keyboard focus, rather than on any focus — so a click that moves focus
  programmatically no longer leaves a flyout behind for the pointer to clear.

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
