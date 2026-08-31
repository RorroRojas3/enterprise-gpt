# Conversation Transcript Cutover — Operator Runbook

> **This cutover destroys every conversation transcript that exists today, and none of it is recoverable.** The new build reads and writes a different Cosmos DB container with a different partition key and a different document shape, and it does not migrate, backfill, dual-read or lazily convert anything from the old one. The moment the new build serves traffic, every conversation opens with an empty message list, and the only copy of what was said in it is whatever still sits in the old container — which nothing reads, and which the last step of this runbook tells you to delete. Take an export of the old container first if anybody might ask for it later; after that step there is nothing to ask for.

Audience: the **platform operator** who deploys this release. This is US-207's deliverable, the one story in the transcript storage and tokenization PRD (not checked into this repository) that can lose data. It is deliberately its own deployment phase and should never be bundled with a feature deploy.

Companion documents: [Transcript Storage and Tokenization](transcript-storage.md) for what the new shape is and why, and [Conversation Usage and Favourites](usage-and-favorites.md) §6 for the SQL columns this release adds.

## 1. What is destroyed, and what survives

The relational database is untouched by the cutover. Only the transcript — the message text itself — is lost.

| Data | Where it lives | Cutover effect |
| --- | --- | --- |
| Conversation rows: name, dates, favourite, project, `ModelId`, token counters | `Core.Conversation` | **Survives**, and gains a `ContextTokens` column defaulted to `0` |
| The usage audit trail and its tool-call trees | `Core.ConversationUsage`, `…McpServer`, `…ToolCall` | **Survives**, and gains `ContextTokens` plus two price columns, all null on existing rows |
| Uploaded documents and their chunks and embeddings | `Core.ConversationDocument`, `…Chunk`, blob storage | **Survives** — untouched by this release |
| The model catalog | `[Core.Ref].[Model]` | **Survives**, and gains two nullable price columns |
| **Every message of every conversation** | The old Cosmos container, partitioned on `/userId` | **Destroyed in effect immediately**, and on disk when you run §7 |

What a user sees afterwards: their conversation list is intact, every conversation still has its name, star and project, and opening one shows an empty transcript rather than an error. The first turn taken in a pre-cutover conversation rebuilds its transcript header and **reseeds the system message**, so the assistant's standing instructions come back on their own — a conversation is usable again from its next prompt, it just has no memory of what came before.

`Core.ConversationUsage.AssistantMessageId` on pre-cutover rows now points at message documents that no longer exist. That is expected and needs no cleanup; nothing joins the two stores.

## 2. Prerequisites

Confirm all four before you start. Three of them fail the deployment at boot rather than at the first request, which is deliberate — a half-configured deployment that starts is a deployment that looks healthy while answering every transcript read with an empty list.

| Prerequisite | Why | How it fails if missing |
| --- | --- | --- |
| **SQL Server 2025 or later** | `EnterpriseGptDbContext` pins `UseCompatibilityLevel(170)`. Pre-2025 engines and LocalDB reject it outright | Startup throws on the first connection; the migration never runs |
| A Cosmos DB account you can create a container in | Startup provisions the transcript container itself | Startup fails in `CosmosBootstrapper.EnsureProvisionedAsync` |
| The database's migration history is initialized | `Database.Migrate()` runs at startup and needs `__EFMigrationsHistory` | See §3 — a database that predates the migration history must be baselined, or `Migrate()` tries to create tables that already exist |
| A window in which chatting can stop | The old and new builds cannot share a transcript container | — |

Optional, and decided later rather than now: whether the `DeleteAllItemsByPartitionKey` capability is enabled on the target account (§6). Leave `CosmosDb:UsePartitionKeyDelete` at its default of `false` until you have confirmed it.

## 3. Step 1 — the SQL migration

`Enterprise.Gpt.Repository/Migrations/` now holds **four** migrations, applied in order by `Database.Migrate()` in `Program.cs` at startup — skipped only in the `Testing` environment, where both test harnesses build the schema from the EF model instead.

| Migration | Carries |
| --- | --- |
| `20260811024339_InitialCreate` | The whole schema plus the `HasData` seeds |
| `20260813233326_AddModelIsReasoningEnabled` | `[Core.Ref].[Model].IsReasoningEnabled` |
| `20260814031023_AddAzureAIFoundryProvider` | The Azure AI Foundry provider row |
| **`20260815202757_AddContextTokensAndModelPricing`** | **New in this release** — both of this project's schema changes in one file |

The new migration carries the two independent schema stories together, which is why they cannot be deployed apart:

- **US-306, the context-token columns.** `Core.Conversation.ContextTokens` (`bigint NOT NULL DEFAULT 0`) and `Core.ConversationUsage.ContextTokens` (`bigint NULL`). Existing conversation rows get `0` — the true weight of a conversation with no transcript — and existing usage rows get `null`, because null means "not transcribed" where `0` would claim a transcript that weighed nothing.
- **US-701 and US-703, the price columns.** `InputPricePerMillionTokens` and `OutputPricePerMillionTokens` as `decimal(18, 6) NULL` on both `[Core.Ref].[Model]` and `Core.ConversationUsage`. Null everywhere on existing rows, and deliberately not zero: an unpriced model must stay distinguishable from a free one.

Every column is additive and nullable-or-defaulted, so a rolled-back application build tolerates the migrated schema. The back-out is the migration's own `Down`, which drops the six columns.

**If the target database predates the migration history**, it has no `__EFMigrationsHistory` table and `Migrate()` will attempt `InitialCreate` against objects that already exist. Baseline it first — generate the script, apply only the new migration by hand, and record all four rows in `__EFMigrationsHistory`:

```bash
# in enterprise-gpt-api/
dotnet ef migrations script 20260814031023_AddAzureAIFoundryProvider 20260815202757_AddContextTokensAndModelPricing \
  --project Enterprise.Gpt.Repository --startup-project Enterprise.Gpt.Api --output baseline.sql
```

Run `baseline.sql` with `SET QUOTED_IDENTIFIER ON` and `SET ANSI_NULLS ON` — the surrounding schema has filtered indexes, and `sqlcmd` connects with `QUOTED_IDENTIFIER OFF`. Then insert the history rows for the three earlier migrations so `Migrate()` treats them as applied. Expected result: the application starts and logs no migration activity on the next boot.

## 4. Step 2 — re-key the Cosmos configuration

The Cosmos section is now bound to a typed `CosmosOptions` and validated with `ValidateOnStart()` (US-101), so every mistake here is a boot failure with a message naming the setting rather than a runtime surprise. Messages never echo the connection string, which carries an account key.

| Setting | Required | Default | Notes |
| --- | --- | --- | --- |
| `CosmosDb:ConnectionString` | yes | — | Unchanged. Still an account key; moving to `DefaultAzureCredential` is deliberately out of scope for this release |
| `CosmosDb:DatabaseId` | yes | — | Unchanged |
| **`CosmosDb:TranscriptContainerId`** | **yes** | — | **New.** Must name a container that either does not exist yet or is already partitioned on `/pk` |
| `CosmosDb:PageSize` | no | `100` | Documents per query page, 1–1000 |
| `CosmosDb:UsePartitionKeyDelete` | no | `false` | Leave off until §6 says otherwise |
| **`CosmosDb:ContainerId`** | **must be absent** | — | **Rejected.** Present in any configuration source, it fails startup by design |

**Removing `CosmosDb:ContainerId` is not optional.** The key is not bound to any property and it is not ignored: startup asserts it is absent and refuses to boot while it is set, naming this runbook in the failure message. That is the cutover gate. A deployment that still names the pre-cutover container is a deployment nobody has migrated, and letting it start would answer every transcript read with an empty result — which reads as data loss rather than as a setting somebody forgot to delete.

Remember every configuration source, not just `appsettings.json`: App Service application settings, container environment variables (`CosmosDb__ContainerId`, double underscore), Key Vault references, user secrets on a developer machine, and any pipeline that writes them. The committed development configuration is already re-keyed:

```jsonc
"CosmosDb": {
  "ConnectionString": "…",
  "DatabaseId": "enterprise-gpt",
  "TranscriptContainerId": "enterprise-gpt-transcript"
}
```

**Point `TranscriptContainerId` at a new container id, not the old one.** A container's partition key path is fixed at creation, and the old container is partitioned on `/userId`. Startup reads the path back and fails with an error naming both the expected `/pk` and the actual path rather than writing documents into a partition no read path can address.

## 5. Step 3 — deploy, and verify

Deploy the new build with chatting stopped. On first start it will, in this order: apply the migration (§3), create the database if it is absent, and create the transcript container with the `/pk` partition key path and an explicit indexing policy that excludes `/content/*`, `/htmlContent/*`, `/usage/*` and `_etag` (US-103). The pre-cutover container is **never opened** — not even to check it exists — so an account where somebody has already deleted it provisions cleanly.

Verify in this order. Each check fails in a different place, which is what makes the sequence worth following.

1. **The application starts.** No `OptionsValidationException`, no partition-key mismatch. If it does not start, §8 has the four messages you are likely to see.
2. **The container exists with the right key.** In the portal or with `az cosmosdb sql container show`, confirm the new container's partition key path is `/pk` and that `/content/*` and `/htmlContent/*` are excluded paths.
3. **A pre-cutover conversation opens.** Sign in as a user who had conversations, open one: the name is right, the transcript is empty, and no error is shown. The API logs one informational line per such read — "has no transcript; returning an empty one" — which is expected, not a fault.
4. **A new turn works end to end.** Send a prompt in that same conversation. It should answer normally, and afterwards the transcript should show the exchange. Behind it, one transactional batch creates four documents — the header, the reseeded system message, the user message and the assistant message — because a pre-cutover conversation has no header to patch.
5. **The stored documents look right.** Query the new container for that conversation:

   ```sql
   SELECT c.type, c.role, c.tokens, c.tokenAccuracy, c.dateCreated
   FROM c WHERE c.conversationId = "<conversation-id>" ORDER BY c.dateCreated
   ```

   Expect `role` as a **string** (`"system"`, `"user"`, `"assistant"`), `tokens` greater than zero on non-empty content, `tokenAccuracy` of `"Estimated"`, and `dateCreated` values that are distinct and fixed-width UTC (`2026-08-15T20:27:57.0000000Z`). A `dateCreated` that is not fixed-width means the serializer configuration did not take, and transcript ordering is not safe — stop and investigate, because `ORDER BY c.dateCreated` is a lexicographic string comparison ([storage §5](transcript-storage.md#5-serialization-and-why-the-date-format-is-load-bearing)).
6. **SQL picked up the new columns.** After that turn, `Core.ConversationUsage` should have a row whose `ContextTokens` is non-null, and `Core.Conversation.ContextTokens` should have moved by the same amount plus the reseeded system message.

## 6. Enabling delete-by-partition-key

Purging a conversation's transcript is one delete per partition. There are two ways to do it, and the setting picks between them:

- **`false` (default)** — page the document ids in the partition and delete them in transactional batches of ten. Provable, works everywhere, costs more request units and more round trips.
- **`true`** — one call to `DeleteAllItemsByPartitionKeyStreamAsync`. Cheaper and a single round trip, and it only works if the capability is enabled on the account.

Three things make the default the right one until you have verified otherwise. The feature is an **Azure public-preview** capability, offered without an SLA. It **cannot be enabled through ARM or Bicep** — the Azure CLI is the only route, which means an infrastructure-as-code deployment does not carry it and a rebuilt account silently loses it. And it is **absent from the Cosmos DB Linux emulator**, so no test in this repository can prove the path works; the batched fallback is the one the integration tests exercise.

Confirm the capability before turning the setting on. The capability list is replaced wholesale, so it must name every capability you want the account to keep:

```bash
az cosmosdb show --resource-group <rg> --name <account> --query "capabilities"
```

If `DeleteAllItemsByPartitionKey` is not in that list, enable it — including any capability already there — and re-check:

```bash
az cosmosdb update --resource-group <rg> --name <account> \
  --capabilities DeleteAllItemsByPartitionKey
```

Only then set `CosmosDb:UsePartitionKeyDelete` to `true`. Turning it on optimistically is survivable but noisy: a refused call is logged as a warning and the purge falls through to the batched path rather than throwing, so the documents are still deleted. That warning names the setting, and it is the signal to turn it back off.

References: [Delete items by partition key value (preview)](https://learn.microsoft.com/azure/cosmos-db/how-to-delete-by-partition-key), and Microsoft's confirmation that [the capability is not supported through ARM templates](https://learn.microsoft.com/answers/a/2054122).

## 7. Step 4 — delete the old container, later

**This is a separate step, deliberately taken days after the deployment rather than with it.** Nothing in the running application reads or writes the pre-cutover container: it is not opened at startup, no code path names it, and the configuration key that used to name it now fails the boot. Leaving it in place therefore costs storage and nothing else — no request units, no correctness risk, no chance of a stale read.

That gap is the only reversibility this cutover has, and it is worth spending. Until the container is deleted you can still roll the application back to the previous build (§9), and you can still extract the old transcripts if somebody asks for something they turn out to need. Once it is deleted, neither is possible.

When the retention window you have chosen has passed:

```bash
az cosmosdb sql container delete --resource-group <rg> --account-name <account> \
  --database-name <database> --name <old-container-id>
```

Expected result: nothing changes for any user, and no application log mentions it. If deleting the container breaks anything at all, something is still configured to read it and step 2 was incomplete.

## 8. Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| Startup throws `OptionsValidationException` naming `CosmosDb:ContainerId` | The pre-cutover key is still set somewhere — often a container environment variable `CosmosDb__ContainerId` or an App Service setting, not the file you edited | Remove it everywhere (§4). This is the cutover gate working |
| Startup throws naming `CosmosDb:TranscriptContainerId` | The new key is missing or empty | Set it (§4) |
| Startup logs "is partitioned on /userId but this application requires /pk" and fails | `TranscriptContainerId` points at the old container, or at any container created with another key path | Point it at a new container id. A partition key path cannot be changed after creation |
| Startup fails on the migration with "There is already an object named …" | The database predates the migration history | Baseline it (§3) |
| Startup fails with a compatibility-level error | The engine is below SQL Server 2025, or it is LocalDB | Move to SQL Server 2025 or later. `UseCompatibilityLevel(170)` is not negotiable in this build |
| Every conversation opens empty, including new ones | Expected for pre-cutover conversations only. If a conversation created *after* the cutover also opens empty, the transcript write is failing | Search the logs for "Writing the transcript for conversation … failed", which names the failing batch operation |
| The transcript reads in the wrong order | `dateCreated` values are not fixed-width UTC, so the lexicographic sort is not chronological | Confirm the Cosmos client is built with `UseSystemTextJsonSerializerWithOptions` from `CosmosSerialization.CreateOptions()`; a document written by another writer with a different format is the other cause |
| A purge logs "Delete-by-partition-key returned 400 …; falling back to batched deletes" | `UsePartitionKeyDelete` is on and the capability is not enabled on the account | Enable the capability (§6) or clear the setting. The documents were still deleted |
| A turn fails with a 400 saying the prompt does not fit the context window | Not a cutover failure. The context budget refuses to send a prompt the model will reject | See [storage §9](transcript-storage.md#9-the-context-budget); check the model's `ContextWindowSize` and `MaxOutputTokens` in the catalog |

## 9. Rollback

**The schedule is reversible; the data is not.** Nothing in this runbook restores a transcript.

| Element | Back-out |
| --- | --- |
| The application build | Redeploy the previous build **and** restore `CosmosDb:ContainerId` — the old build reads no other key. Only possible while the old container still exists (§7) |
| The SQL migration | `dotnet ef database update 20260814031023_AddAzureAIFoundryProvider`, which runs the `Down` and drops the six columns. Costs the context roll-up and the price snapshots; the per-message token counts live in Cosmos and survive |
| The context budget | Not a deployment. Setting a model's `ContextWindowSize` to `0` in the catalog makes it unbounded again, restoring the pre-release replay behaviour for that model |
| Pricing | Clear the two price columns on the catalog rows. Usage rows written while prices were set keep their snapshots, which is the point of snapshotting them |

A build rolled back after users have taken turns on the new build loses those turns' transcripts too, because the old build cannot read the new container's documents. Transcripts written on the new build are not recoverable by rolling back, only by rolling forward again.

## 10. Key files

| Concern | File |
| --- | --- |
| Options and startup validation | [`Service/Settings/CosmosOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/CosmosOptions.cs), [`Api/Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) (the `AddOptions<CosmosOptions>()` block) |
| Container provisioning and the key-path check | [`Api/Startup/CosmosBootstrapper.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Startup/CosmosBootstrapper.cs) |
| The migration | [`Repository/Migrations/20260815202757_AddContextTokensAndModelPricing.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260815202757_AddContextTokensAndModelPricing.cs) |
| Purge, and the delete-by-partition-key fallback | [`Service/Transcripts/TranscriptStore.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Transcripts/TranscriptStore.cs) |
| The empty-transcript path for pre-cutover conversations | [`Service/ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs) (`ReadEmptyTranscriptAsync`, and the header-absent branch of `PersistTurnAsync`) |
| Design and rationale | [Transcript Storage and Tokenization](transcript-storage.md), PRD §5, §7 |
