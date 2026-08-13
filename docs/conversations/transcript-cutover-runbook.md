# Transcript Cutover Runbook

**This cutover destroys every conversation transcript in the environment you run it against, and there is no way to get them back.** The new storage shape cannot read documents written by the old one, nothing migrates them, and the application never opens the old container again. What survives, completely untouched, is everything in SQL Server: every `Core.Conversation` row with its name, its token counters, its `ProjectId` and its `IsFavorite` flag, every `Core.ConversationUsage` row and its tool-call tree, every project, and every uploaded document. After the cutover a user still sees their conversations listed exactly as before — the message history inside them is gone. Read §7 before you schedule this, because "gone" behaves in a specific way that users will notice.

Audience: the platform operator running the deployment. This is US-207 of [the conversation storage and tokenization PRD](../prd/conversation/conversation-storage-and-tokenization.md), and it is the only step of that project that is irreversible. The storage shape it moves to is documented in [Conversation Transcript Storage and Tokenization](transcript-storage-and-tokenization.md).

## 1. What the cutover actually is

There is **no purge command, no migration job and no operator switch**. The cutover is a **configuration-key rename**:

| Before | After |
|---|---|
| `CosmosDb:ContainerId` = the old container | `CosmosDb:TranscriptContainerId` = a **new, empty** container |

The gate is a startup validation that **rejects** `CosmosDb:ContainerId` outright. A deployment that still configures it has not cut over, and refuses to start rather than running a live system against data it would misinterpret (§6.1). Transcripts are not deleted by anything you run; they are abandoned in a container nothing opens.

The new container does not have to exist beforehand. On first start the application creates it, partitioned on `/userId` then `/conversationId`, with message bodies excluded from indexing.

## 2. Preconditions

- [ ] **You are running this against a non-production environment first.** Everything below is identical in both; do it somewhere recoverable at least once so the verification in §5 is familiar before it matters.
- [ ] **Users are told.** Their conversation history disappears. Nothing in the product announces it.
- [ ] **Anyone who wants a transcript kept has taken it out first.** After the cutover there is nothing to export. Note that `GET api/conversations/{id}/export` is part of *this* release, so on the environment being cut over it does not exist yet — a pre-cutover export means reading `GET api/conversations/{id}/messages` on the currently deployed build, or reading the old container directly.
- [ ] **You know the name of the old container**, so you can delete it later (§8) and so you do not accidentally reuse it as the new one. Reusing it fails startup with the partition-key error in §6.2, which is the check working, but it is a confusing way to find out.
- [ ] **The new container name is decided.** The committed development default is `enterprise-gpt-transcripts`.
- [ ] **Throughput is understood.** The bootstrap creates the container **without specifying throughput**, so it takes the database's shared throughput. If the database has none, or you want dedicated RU/s, pre-create the container yourself with exactly `/userId` then `/conversationId` as its partition key paths — startup will then verify it instead of creating it.
- [ ] **This deploy also applies a SQL migration.** `Database.Migrate()` runs at startup and this release adds `20260813040917_AddTokenPrices`. If the target database was built out of band and has no `__EFMigrationsHistory` table, startup will try to apply the baseline `InitialCreate` migration against objects that already exist and **fail**. Settle that before you touch the Cosmos configuration — see [usage §6.5](usage-and-favorites.md#65-applying-the-schema--read-this-before-deploying).

## 3. The configuration change

Remove `ContainerId`. Add `TranscriptContainerId`. Nothing else in the `CosmosDb` section changes.

```jsonc
// before
"CosmosDb": {
  "ConnectionString": "AccountEndpoint=https://…;AccountKey=…;",
  "DatabaseId": "enterprise-gpt",
  "ContainerId": "enterprise-gpt-container"
}

// after
"CosmosDb": {
  "ConnectionString": "AccountEndpoint=https://…;AccountKey=…;",
  "DatabaseId": "enterprise-gpt",
  "TranscriptContainerId": "enterprise-gpt-transcripts"
}
```

In App Service, Container Apps or anywhere else configuring through environment variables, that is:

```bash
# delete this one — leaving it set fails startup (§6.1)
CosmosDb__ContainerId

# set this one
CosmosDb__TranscriptContainerId=enterprise-gpt-transcripts
```

**Deleting the old key is mandatory, not tidiness.** An environment where both are set does not start.

The application settings the API ships with are already cut over ([`appsettings.json`](../../enterprise-gpt-api/Enterprise.Gpt.Api/appsettings.json)); only per-environment overrides need editing.

## 4. Deploy

1. Apply the configuration change from §3 to the target environment.
2. Deploy the release.
3. Watch the startup logs. Three lines matter, in order:

```text
info: Database migrations applied successfully
info: Created Cosmos transcript container enterprise-gpt-transcripts partitioned on /userId, /conversationId
info: Now listening on: https://…
```

The middle line appears **once**, on the first start after the cutover. On every later start the container already exists and is verified silently. If it never appears and the app started anyway, the container already existed and matched — which is fine, and expected on a second replica or a restart.

A `Warning` about the indexing policy is not a failure; see §6.3.

## 5. Verify

Run these as a normal user against the deployed environment. Each one exercises a different half of the new storage shape.

**5.1 — A new conversation writes both document kinds.** Create a conversation and send one prompt. Then read it back:

```http
GET /api/conversations/{id}/messages
```

```json
{
  "totalMessageCount": 3,
  "hasMore": false,
  "messages": [
    { "sequence": 1, "role": 3, "text": "…", "htmlContent": "<p>…</p>\n", "tokens": 7,   "tokenAccuracy": "Estimated" },
    { "sequence": 2, "role": 2, "text": "…", "htmlContent": "<p>…</p>\n", "tokens": 412, "tokenAccuracy": "Estimated" }
  ]
}
```

Four things to confirm, because each proves a different part of the release:

- `totalMessageCount` is **3** for a one-turn conversation — the seeded system message occupies position `0` and is never returned.
- `sequence` values are present and ascending.
- `htmlContent` is non-null — server-side rendering is running.
- `tokens` is greater than zero — estimation is running.

**5.2 — A second turn continues the sequence.** Send another prompt in the same conversation and confirm `totalMessageCount` reaches 5 and the new messages are `sequence` 3 and 4. This is the sequence allocator working against a header that already existed.

**5.3 — Export produces a file.**

```http
GET /api/conversations/{id}/export?format=html
GET /api/conversations/{id}/export?format=json
```

Both should download as attachments. The HTML one should render the answer's formatting, which is the stored `htmlContent` being read rather than re-rendered.

**5.4 — A pre-cutover conversation behaves as §7 describes.** Open one of the conversations that existed before the deploy. Expect a `404`. Confirm that it is still listed in the sidebar and that `GET /api/conversations/{id}` still returns its name, `modelId` and `isFavorite` — that is the SQL side proving it survived.

**5.5 — Deleting a pre-cutover conversation works.** Delete one. It should succeed: the soft delete patches the header document and tolerates the header being absent.

## 6. Failure modes

### 6.1 The old key is still configured

Startup fails during options validation, before the app listens, with:

```text
CosmosDb:ContainerId is retired and names a transcript shape this application cannot read. Remove it and configure CosmosDb:TranscriptContainerId with the new container.
```

**Cause.** `CosmosDb:ContainerId` is set — most often left behind in an environment-variable override, App Service application setting, or a Key Vault-backed configuration source that the `appsettings.json` edit did not touch. Configuration is layered, so removing it from one source is not enough if another still supplies it.

**Fix.** Remove the setting from **every** configuration source and restart. Do not "fix" it by pointing `ContainerId` at the new container; the key is rejected on its name, not on its value.

**Why it is fatal rather than ignored.** The old container holds one document per conversation with an embedded `messages[]` array. This build cannot read that shape. A deployment that still names it has not cut over, and starting would put a live system in front of data it would misinterpret.

### 6.2 The configured container has the wrong partition key

Startup fails with an `InvalidOperationException`, logged just before it as an Error, with:

```text
Cosmos container 'enterprise-gpt-container' is partitioned on [/userId] but this application requires [/userId, /conversationId]. Configure CosmosDb:TranscriptContainerId with a container that has the required partition key paths.
```

**Cause.** `TranscriptContainerId` names a container that already exists with different partition key paths — almost always the old container, pointed at by the new key. A container's partition key cannot be changed after creation.

**Fix.** Point `TranscriptContainerId` at a **different, new** container name and restart. The application will create it.

**Why it is fatal.** With the wrong key paths this application cannot address its own documents. Reads would not error — they would silently match nothing, which is far worse than refusing to start.

### 6.3 The indexing policy could not be applied — a warning, not a failure

```text
warn: Could not update the indexing policy of Cosmos container enterprise-gpt-transcripts; message bodies stay indexed, which costs request units but not correctness
```

**This never fails startup, and it is not something to escalate.** Indexing drift costs request units and storage, not correctness — `/content/*` and `/htmlContent/*` are never filtered on. Some backends, including the Linux emulator, accept a custom indexing policy without applying it, so treating this as fatal would make every local run unstartable.

If you see it against a real Azure Cosmos DB account, the container is working; the excluded paths can be applied by hand from the portal afterwards, at leisure.

## 7. What users see afterwards

This is the part worth reading twice, because the implementation is stricter than "old conversations look empty".

A pre-cutover conversation has a SQL row but **no header document** in the new container. The transcript read path point-reads the header first and treats its absence as the conversation not existing. So:

| Action on a pre-cutover conversation | Result |
|---|---|
| Appears in the sidebar listing | **Yes** — the listing is SQL |
| `GET api/conversations/{id}` (name, `modelId`, `mcpServerIds`, `isFavorite`) | **Works** — SQL |
| `GET api/conversations/{id}/messages` | **404** `/problems/resource-not-found` |
| `GET api/conversations/{id}/export` | **404** `/problems/resource-not-found` |
| Sending a new turn into it | **404** — the stream fails before it starts, as ordinary problem JSON |
| Renaming it | Works; the SQL row is updated and the header patch is tolerated as a no-op |
| Deleting it | **Works** — soft delete on the SQL row, header patch tolerated as a no-op |
| Its `Core.ConversationUsage` rows and reports | **Untouched** — every historical token and cost figure is intact |

So a pre-cutover conversation is not an empty conversation the user can carry on with; it is a conversation that errors when opened and can only be deleted. Two ways to handle that, both operator choices rather than product behaviour:

- **Do nothing.** Users hit an error on old conversations and delete them as they notice. Simple, and noisy in the logs — each attempt logs an Error and returns a `404`.
- **Soft-delete them for everyone, ahead of the users finding them.** Marking every pre-cutover conversation deactivated removes it from the listing, which is exactly what the product's own delete does. Do this **after** the deploy is verified, and record the cutover timestamp first:

  ```sql
  UPDATE [Core].[Conversation]
  SET    DateDeactivated = SYSDATETIMEOFFSET(),
         DateModified    = SYSDATETIMEOFFSET()
  WHERE  DateDeactivated IS NULL
    AND  DateCreated < @cutoverUtc;
  ```

  This touches only the listing flag. It removes nothing, and every `Core.ConversationUsage` row stays exactly where it is, so reporting is unaffected.

## 8. Deleting the old container — a separate, later, deferrable step

**Do not delete the old container as part of the cutover.** It is a separate operator action with no deadline.

Once `TranscriptContainerId` names the new container, **nothing in the application opens the old one**. There is no code path to it: the client is built against one container id, resolved once at startup from the validated options. Leaving it in place costs **storage and, if it has dedicated throughput, its provisioned RU/s** — nothing else. It cannot be read by the application, it cannot be written to, and it cannot interfere with the new one.

That is what makes this step reversible in scheduling even though the cutover is not reversible in effect. Keeping the old container buys nothing back — this build cannot read it — but it does keep the raw documents available to a hand-written script if someone later decides a specific transcript was worth recovering.

When you do delete it:

1. Confirm the new container has been serving traffic for long enough that you would have heard about problems.
2. Confirm no other application or tool reads it.
3. Delete the container (not the database — the new container lives in the same one).
4. If it had dedicated throughput, confirm the RU/s are released.

## 9. Rollback

**The data is not recoverable.** What *is* recoverable is the deployment.

Reverting to the previous release means putting `CosmosDb:ContainerId` back and removing `CosmosDb:TranscriptContainerId`, since the previous build reads the old key. Provided §8 has not been done, the old container is still there and the previous build resumes on it with every pre-cutover transcript intact — **but every conversation held between the cutover and the rollback is then invisible**, because those transcripts are in the new container, which the old build cannot read. Their SQL rows and usage records survive either way.

Rolling forward again after that puts you back on the new container with those transcripts still present. The two containers each hold the turns written while they were live, and nothing merges them.

## 10. Key files

| Concern | File |
|---|---|
| The cutover gate and the other Cosmos validations | [`Enterprise.Gpt.Api/Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) |
| Options and the retired key | [`Enterprise.Gpt.Service/Settings/CosmosOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/CosmosOptions.cs) |
| Container creation, verification and indexing convergence | [`Enterprise.Gpt.Service/CosmosContainerBootstrap.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/CosmosContainerBootstrap.cs) |
| Committed defaults | [`Enterprise.Gpt.Api/appsettings.json`](../../enterprise-gpt-api/Enterprise.Gpt.Api/appsettings.json) |
| The storage shape being moved to | [Conversation Transcript Storage and Tokenization](transcript-storage-and-tokenization.md) |
| The SQL migration in the same deploy | [Usage and Favourites §6.5](usage-and-favorites.md#65-applying-the-schema--read-this-before-deploying) |
