# Architecture Overview

Enterprise GPT is an internal AI chat platform. Users sign in with Microsoft Entra ID, hold
streaming conversations with large language models, attach documents that become searchable
context, and — with the right permission — have the assistant generate files. Administrators manage
the model catalog, the MCP tool servers, and user permissions.

The repository holds two applications:

| | |
| --- | --- |
| `enterprise-gpt-api/` | .NET 10 minimal-API backend, six projects plus two test projects |
| `enterprise-gpt-ui/` | Angular 21 single-page client, zoneless, NgRx Signals |

## External dependencies

| Service | Role |
| --- | --- |
| SQL Server 2025 | Everything except transcripts: users, permissions, conversations metadata, projects, documents and their chunks, the usage audit trail. Also hosts vector search and the Data Protection key ring. |
| Azure Cosmos DB | Conversation transcripts — one document per message, partitioned per user + conversation. |
| Azure Blob Storage | Original uploaded files and generated artifacts. |
| Microsoft Entra ID | Authentication, and on-behalf-of tokens for MCP servers that accept them. |
| Azure OpenAI | The default chat provider and every document embedding. Required. |
| Azure AI Foundry / Amazon Bedrock / Anthropic | Optional additional chat providers, each behind an `:Enabled` flag. |
| Azure Document Intelligence | Text extraction for formats the in-process extractors cannot read. |
| Azure Key Vault | Secrets, and the key that wraps the Data Protection key ring. |
| Application Insights | Server and browser telemetry. |

The compatibility level is pinned to SQL Server 2025 (`UseCompatibilityLevel(170)`); earlier engines
and LocalDB reject it.

## The three paths worth tracing

**A chat turn.** The composer posts to `POST api/conversations/{id}/stream`. The endpoint takes a
per-conversation lock, resolves the model to a keyed `IChatClient`, assembles the prompt (history
from Cosmos, project instructions, attached tools), and streams the provider's output back as
server-sent events. Response headers are written lazily on the first fragment, so a failure before
the first token still returns `application/problem+json` rather than a half-open stream. The client
decodes frames in a framework-free codec and folds them into a turn snapshot. When the turn settles,
the messages are written to Cosmos and a usage row to SQL.
See [conversations/turn-lifecycle.md](../conversations/turn-lifecycle.md) and
[conversations/streaming.md](../conversations/streaming.md).

**A document upload.** The file is validated and stored in blob storage, then a background job
extracts its text, splits it into chunks, embeds each chunk, and writes them to SQL. The client
polls a job-status endpoint. Once ingested, the document is reachable by the `document_search` tool
during a turn. See [documents/ingestion.md](../documents/ingestion.md).

**An admin write.** The route carries `PermissionEndpointFilter.Require(...)`, which reads the
in-process permission cache rather than the database. The service mutates the row, then invalidates
that user's cache entry. See [auth-and-permissions.md](auth-and-permissions.md).

## Where a subsystem is documented

| Subsystem | Doc |
| --- | --- |
| Project and endpoint layout, DI, error handling | [backend.md](backend.md) |
| Angular layers, bootstrap, routing, build gates | [frontend.md](frontend.md) |
| SQL schema, migrations, Cosmos documents | [data-model.md](data-model.md) |
| Sign-in, permissions, secrets | [auth-and-permissions.md](auth-and-permissions.md) |
| Turns, streaming, transcripts, usage, export | [../conversations/](../conversations/) |
| Upload, retrieval, sheets, summarization, downloads | [../documents/](../documents/) |
| MCP servers and the File Agent | [../tools/](../tools/) |
| Model catalog and providers | [../models/](../models/) |
| Client state, errors, design system, rendering, layout | [../frontend/](../frontend/) |
| Users, permissions and projects as screens | [../admin/](../admin/) |
| Logging, telemetry, queries | [../observability/](../observability/) |
| Configuration and operator procedures | [../operations/](../operations/) |
| Tests, gates and CI | [../development/testing-and-ci.md](../development/testing-and-ci.md) |
