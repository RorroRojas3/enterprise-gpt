# Operator Runbooks

Procedures executed against a deployed environment. Everything else under `docs/` describes how the
software behaves; this file describes what an operator does.

---

# Registering an MCP tool server

One procedure, two worked examples. The mechanics — the registry, the auth types, the failure
contract — are in [../tools/mcp-servers.md](../tools/mcp-servers.md).

## Procedure

1. Satisfy the server's own prerequisites (below).
2. Register it: **Admin → MCP servers → Add**, supplying name, URL, auth type, and scope or headers.
   Registration creates a permission named for the server.
3. Grant that permission to whoever should use it: **Admin → Users → Edit permissions**.
4. Verify: sign in as a granted user, open the composer's Tools menu, and confirm the server appears.
   Select it and take a turn.

## Example A — Azure DevOps (`EntraIdOnBehalfOf`)

### Prerequisites

Confirm all three before opening the dialog. Getting any wrong surfaces the same way — a 502 the
moment someone selects the server on a turn.

| Prerequisite | Why |
| --- | --- |
| The organization is Microsoft Entra-backed | Standalone Microsoft account organizations are not supported for the remote server at all — there is no tenant behind them for the rest to attach to |
| The "Azure DevOps MCP" enterprise application exists in your tenant | App id `2a72489c-aab2-4b65-b93a-a91edccf33b8`, identifier URI `https://mcp.dev.azure.com`. First-party and usually already present |
| This deployment's app registration holds a **delegated** permission to that resource, with **tenant-wide admin consent** | See below |

If the enterprise application is genuinely absent — confirm first, it usually is not:

```bash
az rest --method get --url "https://graph.microsoft.com/v1.0/servicePrincipals(appId='2a72489c-aab2-4b65-b93a-a91edccf33b8')"
# a 404 Request_ResourceNotFound confirms it
az ad sp create --id 2a72489c-aab2-4b65-b93a-a91edccf33b8
```

That creates the service principal only; it grants nothing.

### Why admin consent, specifically

The token is acquired through an on-behalf-of exchange requesting
`https://mcp.dev.azure.com/.default`. The `.default` scope is a **static** request: it resolves to
whatever permissions are already configured and consented on the calling app's registration, and it
cannot be combined with a dynamic per-permission scope. So there is no runtime prompt this flow
could show a user even if one were wanted — the permission has to already be sitting there,
consented, before the first call.

A registration missing this fails every call for every user with `AADSTS65001`, which from the
outside reads identically to a genuinely unreachable server.

**Do not substitute the Azure DevOps REST API resource.** `499b84ac-1321-427f-aa17-267ca6975798`
(`user_impersonation`) is a different, older audience — the one the classic REST API and the
`az devops` CLI authenticate against — and a token issued for it is not accepted by
`https://mcp.dev.azure.com`. The two are easy to conflate because both are "Azure DevOps".

### Discovering the scope

Ask the server rather than taking it on faith:

```bash
curl https://mcp.dev.azure.com/.well-known/oauth-protected-resource/{organization}
```

```json
{
  "resource": "https://mcp.dev.azure.com/{organization}",
  "authorization_servers": ["https://login.microsoftonline.com/{tenant-id}/v2.0"],
  "scopes_supported": ["https://mcp.dev.azure.com/.default"]
}
```

## Example B — GitHub (`UserApiKey`)

### Prerequisites

Each user needs their own GitHub personal access token with the scopes their work requires. Nothing
is shared and nothing is deployment-wide.

### After registration

Holding the permission is necessary but not sufficient. Until the user stores a key, the Tools menu
renders the row as a plain item with a "Key required" dot rather than a switch; clicking it opens
the key dialog. Once stored, the row behaves like any other, with a sibling item showing the token's
last four characters.

Nothing here is administrator-visible — the admin shape carries no per-user credential fields.

### Changing the URL or auth type discards every stored key

A stored token is consent to send it to **one** endpoint. If you are renaming or re-registering
rather than truly repointing, expect every user to be asked for their key again — that is the safe
behaviour, not a defect.

## Failure modes

| Symptom | Cause |
| --- | --- |
| 502 on every turn, every user | Registration fault: missing consent, wrong scope, wrong resource, or unreachable URL |
| 428 `mcp-credential-required` | This caller has no usable key stored |
| 428 `mcp-credential-rejected` | The server answered 401/403 to the caller's key — revoked, expired, or missing a scope |
| Server absent from the Tools menu | The caller does not hold the server's permission, or the server is deactivated |

---

# Cosmos transcript cutover

> **This cutover destroys every conversation transcript that exists today, and none of it is
> recoverable.** The build reads and writes a different Cosmos container with a different partition
> key and document shape, and it does not migrate, backfill, dual-read or lazily convert anything.
> Take an export of the old container first if anybody might ask for it later.

It is deliberately its own deployment phase and should never be bundled with a feature deploy.

## What is destroyed, and what survives

The relational database is untouched. Only the message text is lost.

| Data | Effect |
| --- | --- |
| Conversation rows: name, dates, favourite, project, model, counters | **Survives** |
| The usage audit trail and its tool-call trees | **Survives** |
| Uploaded documents, chunks, embeddings, blobs | **Survives** |
| The model catalog | **Survives** |
| **Every message of every conversation** | **Destroyed** |

What a user sees afterwards: the conversation list is intact, every conversation keeps its name,
star and project, and opening one shows an empty transcript rather than an error. The first turn in
a pre-cutover conversation rebuilds its header and reseeds the system message, so a conversation is
usable again from its next prompt — it just has no memory of what came before.

`ConversationUsage.AssistantMessageId` on pre-cutover rows points at documents that no longer exist.
That is expected and needs no cleanup; nothing joins the two stores.

## Prerequisites

Three of these fail at boot rather than at the first request, which is deliberate — a
half-configured deployment that starts is one that looks healthy while answering every transcript
read with an empty list.

| Prerequisite | Fails how |
| --- | --- |
| SQL Server 2025 or later | Startup throws on the first connection; the migration never runs |
| A Cosmos account you can create a container in | Startup fails in `CosmosBootstrapper.EnsureProvisionedAsync` |
| The migration history is initialized | `Database.Migrate()` needs `__EFMigrationsHistory` — see below |
| A window in which chatting can stop | The old and new builds cannot share a container |

Leave `CosmosDb:UsePartitionKeyDelete` at `false` until you have confirmed the
`DeleteAllItemsByPartitionKey` capability is enabled on the account.

## Steps

1. **Apply the SQL migrations.** `Database.Migrate()` runs at startup, skipped only in the `Testing`
   environment.

   `Migrate()` expects `__EFMigrationsHistory` to hold the `Initial` row and nothing else. A
   database this application did not build from empty — one with no history at all, or one whose
   history records migrations the project no longer contains — makes `Migrate()` attempt `Initial`
   against objects that already exist. Baseline it first: generate the script below, confirm the
   schema already matches it, then leave exactly one `Initial` row in `__EFMigrationsHistory`.

   ```bash
   # in enterprise-gpt-api/
   dotnet ef migrations script --project Enterprise.Gpt.Repository --startup-project Enterprise.Gpt.Api
   ```

2. **Stop traffic.**

3. **Re-key the configuration.** Set `CosmosDb:TranscriptContainerId` and **remove
   `CosmosDb:ContainerId` from every source**, Key Vault included. Startup refuses to run while the
   legacy key appears anywhere in merged configuration — that refusal is this gate working, not a
   bug.

4. **Start the new build.** It provisions the transcript container itself.

5. **Verify.** Sign in, open an existing conversation (expect an empty transcript, not an error),
   take a turn, and confirm the message persists across a reload. Check `/health/ready` returns
   `Healthy`.

6. **Delete the old container** once you are satisfied — and once any export you wanted has been
   taken.

## Rollback

The schema changes are additive and nullable-or-defaulted, so a rolled-back application build
tolerates the migrated schema. The transcripts do not come back.

## Related

- [../tools/mcp-servers.md](../tools/mcp-servers.md)
- [../conversations/transcripts.md](../conversations/transcripts.md)
- [configuration.md](configuration.md)
