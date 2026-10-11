# Data Model

Two stores. SQL Server holds everything structured and queryable; Cosmos DB holds conversation
transcripts, which are append-heavy, read by partition, and never joined.

## SQL Server

`EnterpriseGptDbContext` (`Enterprise.Gpt.Repository/Sql/DbContexts/EnterpriseGptDbContext.cs`) on EF Core 10,
pinned to `UseCompatibilityLevel(170)` — SQL Server 2025. Earlier engines and LocalDB reject it.

Tables live in two schemas: `Core` for data, `Core.Ref` for reference/catalog rows
(`Core.Ref.Model`, `Core.Ref.McpServer`). The Data Protection key ring is the one table in `dbo`.

### Conventions

| Convention | Detail |
| --- | --- |
| Base types | Entities derive from the generic bases in `Entity/Base/`. `BaseEntity<TKey>` supplies `Id` and `DateDeactivated`; `BaseCreatedEntity<TKey>` adds `DateCreated` (a write-once row); `BaseModifiedEntity<TKey>` adds non-null `DateModified` and the `Version` rowversion; `BaseModifiedByEntity<TKey>` adds `CreatedById` / `ModifiedById`. The `BaseDocument*<TKey>` family shares the document shape. Entities carry no attributes, and column lengths live in `Common/Constants/<Feature>/<Entity>Limits.cs`, which both the configuration and the DTO validator read. |
| Soft delete | The named `SoftDelete` query filter (`QueryFilterNames.SoftDelete`) on `DateDeactivated`. It covers every entity except `User`, `Model`, `Provider` and `McpServer`, which history and reports reach through required navigations. `ExecuteUpdate` and `ExecuteDelete` honour the filter too. `SoftDeleteInterceptor` turns a tracked `Remove` into a `DateDeactivated` stamp; `ExecuteDeleteAsync` bypasses the tracker and stays a hard delete. |
| Reading deactivated rows | Only through `IgnoreQueryFilters([QueryFilterNames.SoftDelete])`, in a method whose name says so (`…IncludingDeactivatedAsync`); tests use `.IncludingDeactivated()`. A required navigation into a filtered principal throws at model build (`PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning`), and `SoftDeleteModelTests` pins the reviewed filtered-to-filtered list. |
| Timestamps | `AuditTimestampInterceptor` stamps `DateCreated` and `DateModified` from the injected `TimeProvider`, once per save, and only on a real change. Services never stamp tracked entities; an `ExecuteUpdateAsync` call stamps `DateModified` from the same `TimeProvider`. |
| Concurrency | Optimistic, via the `Version` rowversion on `BaseModifiedEntity`. `SaveChangesOrConflictAsync` (`Service/Extensions/`) turns a lost race into a `ConflictException`, a 409. `Version` never travels to the client, so this detects races between concurrent requests only. The append-only ledger tables (`Provider` and the `ConversationUsage*` family) carry no rowversion. |
| Foreign keys | Every FK is forced to `DeleteBehavior.NoAction`. Cascades are the application's job. |
| Configuration | Each of the 26 entity configurations derives from an abstract base in `Sql/Configurations/Base/` (`ConfigureBase` for the inherited members, `ConfigureEntity` for the rest) and is found by `ApplyConfigurationsFromAssembly`, so a new configuration needs no registration. |

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

`Repository/Sql/Migrations/` holds the `Initial` migration — the whole schema plus the `HasData` seeds,
including the provider rows — plus each schema or seed change made since, added the normal
`dotnet ef migrations add` way.

`Database.Migrate()` runs at startup and is **skipped in the `Testing` environment**, so a database
built from empty is migrated and seeded automatically. Unit tests use SQLite in-memory and
integration tests use `EnsureCreated()` against a Testcontainers SQL Server, so neither replays the
migration chain.

Add a schema change from `enterprise-gpt-api/`:

```bash
dotnet ef migrations add <VerbSubject> --project Enterprise.Gpt.Repository --startup-project Enterprise.Gpt.Api --output-dir Sql/Migrations
dotnet ef migrations has-pending-model-changes --project Enterprise.Gpt.Repository --startup-project Enterprise.Gpt.Api
```

One convention the schema carries: a store-generated `bool` default is silently dropped from EF's
`HasData` seed differ, so a new bool column that must backfill is configured
`HasDefaultValue(x).ValueGeneratedNever()`.

`Migrate()` expects `__EFMigrationsHistory` to record the migrations whose schema the database
already has, starting with `Initial`. A database this application did not build from empty must be
baselined before `Migrate()` can run against it.

## Cosmos DB

The conversation transcript: **one document per message**, plus one `type`-discriminated header per
conversation, all under a synthetic partition key `/pk` holding `{userId}:{conversationId}`.

Access is through the raw `CosmosClient` SDK with a System.Text.Json serializer — not an EF Core
provider — behind `ITranscriptStore` (`Repository/Cosmos/CosmosTranscriptStore.cs`). **Every member requires a partition key.**

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
| `enterprise-gpt-api/Enterprise.Gpt.Repository/Sql/DbContexts/EnterpriseGptDbContext.cs` | DbSets, `ApplyConfigurationsFromAssembly`, forced `NoAction` keys |
| `enterprise-gpt-api/Enterprise.Gpt.Repository/Sql/Configurations/` | `Base/` abstract configurations, then one per mapped entity by feature |
| `enterprise-gpt-api/Enterprise.Gpt.Repository/Sql/Migrations/` | `Initial` and the incremental migrations |
| `enterprise-gpt-api/Enterprise.Gpt.Entity/Base/` | The generic entity bases: key, soft delete, timestamps, rowversion |
| `enterprise-gpt-api/Enterprise.Gpt.Repository/Sql/Interceptors/` | `SoftDeleteInterceptor` and `AuditTimestampInterceptor` |
| `enterprise-gpt-api/Enterprise.Gpt.Repository/Sql/Constants/QueryFilterNames.cs` | The `SoftDelete` filter name |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Extensions/DbContextConflictExtensions.cs` | `SaveChangesOrConflictAsync`, a lost race as 409 |
| `enterprise-gpt-api/Enterprise.Gpt.Entity/Transcripts/Models/` | Cosmos document shapes and the type discriminator |
| `enterprise-gpt-api/Enterprise.Gpt.Repository/Cosmos/CosmosTranscriptStore.cs` | Batches, paging, partition purge |
| `enterprise-gpt-api/test/Enterprise.Gpt.Unit.Test/TestInfrastructure/SqliteDbContextFixture.cs` | The SQLite fixture and its rowversion customizer |

## Related

- [../conversations/transcripts.md](../conversations/transcripts.md)
- [../documents/ingestion.md](../documents/ingestion.md)
- [../conversations/usage-and-reporting.md](../conversations/usage-and-reporting.md)
