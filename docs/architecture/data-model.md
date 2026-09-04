# Data Model

Two stores. SQL Server holds everything structured and queryable; Cosmos DB holds conversation
transcripts, which are append-heavy, read by partition, and never joined.

## SQL Server

`EnterpriseGptDbContext` (`Enterprise.Gpt.Repository/EnterpriseGptDbContext.cs`) on EF Core 10,
pinned to `UseCompatibilityLevel(170)` — SQL Server 2025. Earlier engines and LocalDB reject it.

Tables live in two schemas: `Core` for data, `Core.Ref` for reference/catalog rows
(`Core.Ref.Model`, `Core.Ref.McpServer`). The Data Protection key ring is the one table in `dbo`.

### Conventions

| Convention | Detail |
| --- | --- |
| Base types | `BaseEntity` supplies `Id`, `DateCreated`, `DateDeactivated`, `Version`. `BaseModifiedEntity` adds `DateModified`; `BaseModifiedByEntity` adds `CreatedById` / `ModifiedById`. |
| Soft delete | Nullable `DateDeactivated` on `BaseEntity`, and **no `HasQueryFilter`** — every query filters manually. Reading a row without that predicate is the standard bug here. |
| Concurrency | Optimistic, via `[Timestamp] byte[] Version`. |
| Foreign keys | Every FK is forced to `DeleteBehavior.NoAction`. Cascades are the application's job. |
| Configuration | The 26 `IEntityTypeConfiguration` classes in `Repository/Configurations/` are applied **explicitly**, not by assembly scan — a new configuration that is not registered silently does nothing. |

### Entity families

**Identity and access** — `User` (keyed by the Entra object id), `Permission`, `UserPermission`,
and `UserMcpCredential` (a user's own encrypted credential for one MCP server, behind a
`(UserId, McpServerId)` unique index filtered `[DateDeactivated] IS NULL`).

**Catalog** — `Provider` and `Model` in `Core.Ref`, plus `McpServer`.

**Conversations** — `Conversation` holds metadata only; the messages are in Cosmos.
`MessageFeedback` records a rating against a message id.

**Documents** — two parallel families with a shared base, one rooted at a conversation and one at a
project:

```
BaseDocument        -> ConversationDocument        / ProjectDocument
BaseDocumentChunk   -> ConversationDocumentChunk   / ProjectDocumentChunk
BaseDocumentSummary -> ConversationDocumentSummary / ProjectDocumentSummary
BaseDocumentSheet(+Column, +Row)  -> Conversation… / Project…
```

Chunks carry the embedding vector that `document_search` queries; sheet tables carry the structured
form of a spreadsheet that `sheet_query` reads. A summary row is unique per document, enforced by an
index filtered on `[DateDeactivated] IS NULL`.

`ConversationDocument` carries an `Uploaded` / `Generated` discriminator, which is what keeps files
the assistant produced out of retrieval. See [../documents/downloads.md](../documents/downloads.md).

**Usage** — an append-only audit trail: `ConversationUsage` (one row per model call),
`ConversationUsageTurn` (per-turn token detail), `ConversationUsageToolCall`, and
`ConversationUsageMcpServer`. Prices are snapshotted onto the row rather than joined, so a later
catalog edit cannot rewrite history. See
[../conversations/usage-and-reporting.md](../conversations/usage-and-reporting.md).

### Migrations

`Repository/Migrations/` holds the `Initial` migration — the whole schema plus the `HasData` seeds,
including the provider rows — plus each schema or seed change made since, added the normal
`dotnet ef migrations add` way.

`Database.Migrate()` runs at startup and is **skipped in the `Testing` environment**, so a database
built from empty is migrated and seeded automatically. Unit tests use SQLite in-memory and
integration tests use `EnsureCreated()` against a Testcontainers SQL Server, so neither replays the
migration chain.

Add a schema change as a normal `dotnet ef migrations add` migration. One convention the schema
carries: a store-generated `bool` default is silently dropped from EF's `HasData` seed differ, so a
new bool column that must backfill is configured `HasDefaultValue(x).ValueGeneratedNever()`.

`Migrate()` expects `__EFMigrationsHistory` to hold the `Initial` row and nothing else. A database
this application did not build from empty must be baselined before `Migrate()` can run against it.

## Cosmos DB

The conversation transcript: **one document per message**, plus one `type`-discriminated header per
conversation, all under a synthetic partition key `/pk` holding `{userId}:{conversationId}`.

Access is through the raw `CosmosClient` SDK with a System.Text.Json serializer — not an EF Core
provider — behind `Service/Transcripts/ITranscriptStore`. **Every member requires a partition key.**

| Document | `type` | Notes |
| --- | --- | --- |
| Header | `conversation` | One per conversation: name, counters, the dates the client reads. Shares the partition key with its messages so a turn writes both in one transactional batch. |
| Message | `message` | One per message, carrying role, content, and attachments. |

Both carry a schema version. Version 2 added attachments, which read back as empty on a version 1
document, so nothing migrates and both vintages are readable by the same code.

Two shape constraints that are load-bearing:

- Dates are serialized **fixed-width UTC**, because `ORDER BY dateCreated` is a lexicographic string
  compare in Cosmos.
- The `type` discriminator stays in every read predicate, so a third document kind added later
  cannot silently enter queries written before it existed.

The container id key is `CosmosDb:TranscriptContainerId`. The former `CosmosDb:ContainerId` now
fails startup by design — see [../operations/runbooks.md](../operations/runbooks.md), which covers
the cutover and destroys every existing transcript.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Repository/EnterpriseGptDbContext.cs` | DbSets and explicit configuration registration |
| `enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/` | One configuration per mapped entity |
| `enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/` | The 20-migration chain |
| `enterprise-gpt-api/Enterprise.Gpt.Entity/BaseEntity.cs` | Soft delete and concurrency columns |
| `enterprise-gpt-api/Enterprise.Gpt.Entity/Transcripts/TranscriptDocuments.cs` | Cosmos document shapes and the type discriminator |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Transcripts/TranscriptStore.cs` | Batches, paging, partition purge |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/TestInfrastructure/SqliteDbContextFixture.cs` | The SQLite fixture and its rowversion customizer |

## Related

- [../conversations/transcripts.md](../conversations/transcripts.md)
- [../documents/ingestion.md](../documents/ingestion.md)
- [../conversations/usage-and-reporting.md](../conversations/usage-and-reporting.md)
