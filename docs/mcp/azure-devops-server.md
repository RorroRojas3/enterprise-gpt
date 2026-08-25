# Registering the Remote Azure DevOps MCP Server — Operator Runbook

Audience: the **platform operator** registering an MCP tool server through the admin area. This is the reference case for [the Headers field](../ui/administration.md#1132-headers-a-textarea-not-a-row-editor-and-why-it-can-never-become-a-secret-store) added to MCP server registrations — a remote server that configures itself through request headers rather than through anything this application's registry form would otherwise have a place for. Read [Administration §11](../ui/administration.md#11-the-mcp-server-registry-us-1208-us-1210) first for the registry screen itself; this page is what to type into it for this one server, and the Entra ID setup that has to exist before you do.

Companion: [Permission Cache §5](../permissions/permission-cache.md#5-invalidation) for what deactivating this server later does to everyone who was granted it.

## 1. Why this exists

Azure DevOps ships a [remote MCP Server](https://learn.microsoft.com/azure/devops/mcp-server/remote-mcp-server) — hosted by Azure DevOps itself, reached over streamable HTTP, with no process for anyone to install or keep running. That is what makes it worth registering here rather than standing up the local, `stdio`-based alternative: nothing about it depends on where this API happens to run.

**Left unconfigured, it is generous by default.** A registration that sends no extra headers gets the server's full toolset — every category it exposes, work items and wikis alongside pull-request creation, work-item updates and pipeline runs, on the order of 45 tools. Every one of those tools becomes available to every turn that selects this server, for every user holding the permission `McpServerService` creates alongside it. The two headers this runbook registers are the only way to narrow that from this application: `X-MCP-Readonly` and `X-MCP-Toolsets`, read by the remote server itself on every request, not enforced by anything on this side of the wire.

## 2. Entra ID prerequisites

Confirm all of these before opening the admin dialog. Getting any of them wrong surfaces the same way — a 502 the moment someone selects the server on a turn — so it is worth confirming them here rather than diagnosing them there (§6).

| Prerequisite | Why |
| --- | --- |
| **The Azure DevOps organization is Microsoft Entra–backed** | Standalone Microsoft account (MSA) organizations are not supported for the remote server at all — there is no Entra tenant behind them for any of the rest of this section to attach to |
| **The "Azure DevOps MCP" enterprise application exists in your tenant** | App id `2a72489c-aab2-4b65-b93a-a91edccf33b8`, identifier URI `https://mcp.dev.azure.com`. First-party, and usually already present; if [Entra ID → Enterprise applications](https://portal.azure.com) does not list it, an Application Administrator has to create the service principal by hand (§2.1) before there is anything to grant a permission to |
| **This deployment's own app registration holds a delegated permission to that resource, with tenant-wide admin consent** | See §2.2 for why `.default` in an on-behalf-of exchange needs this, specifically, rather than any per-user consent |

### 2.1 If the enterprise application is missing

Confirm it is actually absent before running this — it usually is not:

```bash
az rest --method get --url "https://graph.microsoft.com/v1.0/servicePrincipals(appId='2a72489c-aab2-4b65-b93a-a91edccf33b8')"
```

A `404 Request_ResourceNotFound` confirms it. An Application Administrator, Cloud Application Administrator or Global Administrator then creates the service principal:

```bash
az ad sp create --id 2a72489c-aab2-4b65-b93a-a91edccf33b8
```

Re-run the `GET` above; expect `200` with `"displayName": "Azure DevOps MCP"`. This step only creates the service principal — it grants nothing and consents to nothing on its own (§2.2 next).

### 2.2 The delegated permission and admin consent

Add a delegated permission to the Azure DevOps MCP resource on **this deployment's own app registration** — the one `enterprise-gpt-api` authenticates as — and grant tenant-wide admin consent for it.

This has to be admin consent, not the per-user consent a caller could otherwise grant themselves on first use, and the reason is in how the token is acquired rather than in anything this application chooses: `McpToolProvider.AcquireAccessTokenAsync` requests `server.Scope` — here, `https://mcp.dev.azure.com/.default` — through an [on-behalf-of exchange](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow). The [`.default` scope](https://learn.microsoft.com/entra/identity-platform/scopes-oidc#the-default-scope) is a **static** request: it resolves to whatever permissions are already configured and consented on the calling app's registration, and it cannot be combined with a dynamic, per-permission scope in the same request. So there is no runtime prompt this flow could show a user even if one were wanted — the permission has to already be sitting there, consented, before the first on-behalf-of call is ever made. A registration missing this fails every call for every user with `AADSTS65001` (§6), which reads identically to a genuinely unreachable server from the outside.

**Do not substitute the Azure DevOps REST API resource.** `499b84ac-1321-427f-aa17-267ca6975798` (`user_impersonation`) is a different, older Azure DevOps audience — the one the classic REST API and the `az devops` CLI authenticate against — and a token issued for it is not accepted by `https://mcp.dev.azure.com`. The two are easy to conflate because both are "Azure DevOps," and only one of them is the resource this server actually is.

## 3. Discover the required scope

Rather than take the scope on faith, ask the server itself. Every remote MCP server publishes an [RFC 9728](https://datatracker.ietf.org/doc/html/rfc9728) protected-resource-metadata document at a well-known path, and the Azure DevOps one is organization-scoped:

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

`authorization_servers` names **your tenant's** token endpoint — confirming the consent from §2.2 was granted in the tenant the organization actually belongs to — and `scopes_supported` is the exact string to put in the registration's Scope field next.

## 4. Register the server

In the admin area's MCP servers tab ([Administration §11](../ui/administration.md#11-the-mcp-server-registry-us-1208-us-1210)), **Add server**, and fill it in as:

| Field | Value |
| --- | --- |
| Name | `Azure DevOps` (or whatever your registry's naming convention prefers — this is also what names the permission it creates) |
| Description | Something naming what it reaches, e.g. "Work items, pull requests, wikis and pipelines for `{organization}`, read-only" |
| URL | `https://mcp.dev.azure.com/{organization}` |
| Auth type | **Entra ID (on behalf of)** |
| Scope | `https://mcp.dev.azure.com/.default` |
| Headers | `X-MCP-Readonly: true` and `X-MCP-Toolsets: repos,wit,wiki` — one per line |

Or the equivalent `POST api/mcps`:

```bash
curl -X POST https://localhost:7045/api/mcps \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
        "name": "Azure DevOps",
        "description": "Work items, pull requests, wikis and pipelines for contoso, read-only.",
        "url": "https://mcp.dev.azure.com/contoso",
        "authType": 2,
        "scope": "https://mcp.dev.azure.com/.default",
        "headers": {
          "X-MCP-Readonly": "true",
          "X-MCP-Toolsets": "repos,wit,wiki"
        }
      }'
```

`authType: 2` is `EntraIdOnBehalfOf` — see [Administration §11.3](../ui/administration.md#113-the-dialog-binds-seven-fields-still-not-the-boards-linked-permission) for why the two auth types travel as numbers rather than named values.

### 4.1 Why these two headers, specifically

Neither is required for the connection to work — omit them and the server still connects, on its full default toolset. They exist to narrow that down to what this deployment actually wants exposed:

- **`X-MCP-Readonly: true`** strips every tool that can write to Azure DevOps — no `create_pull_request`, no `update_work_item`, no `run_pipeline` — leaving only tools that read. Set this unless a turn genuinely needs to make changes on someone's behalf.
- **`X-MCP-Toolsets`** narrows further, to a comma-separated list of the categories below. `repos,wit,wiki` is a reasonable starting set for a chat assistant answering questions about code, work items and documentation; add `pipelines`, `work` or `testplan` only for the categories you actually want a conversation to reach.

| Toolset value | Included tools |
| --- | --- |
| `all` (default) | Every category except one requiring explicit opt-in (`elm`) |
| `repos` | Repository and pull request tools |
| `advsec` | Advanced Security alert tools |
| `wit` | Work item tools, including work item search |
| `pipelines` | Pipeline and build tools |
| `wiki` | Wiki tools, including wiki search |
| `work` | Iteration and capacity tools |
| `testplan` | Test plan tools |
| `elm` | Enterprise Live Migration tools — private preview; the header alone is not sufficient, the organization must also be enrolled |

Both headers are ordinary, case-sensitive HTTP header names as far as the remote server is concerned — nothing here is secret, and nothing in this registration masks them (see [Administration §11.3.2](../ui/administration.md#1132-headers-a-textarea-not-a-row-editor-and-why-it-can-never-become-a-secret-store) for why that is deliberate). **Editing this row later and leaving the Headers field blank clears both restrictions and restores the server's full default toolset** — the same full-representation rule every other field on this dialog already follows.

## 5. Verify

There is no dry-run check, no tool-surface preview and no health signal for a registered server anywhere in this application ([Administration §15](../ui/administration.md#15-deliberately-not-here)) — the only way to confirm a registration behaves as configured is to use it and read the log line it produces.

1. Grant yourself (or a test account) the permission this registration created, select the server on a conversation, and send a turn that would exercise it — "list my assigned work items" is enough.
2. Check the API log for the connection this produced:

   ```
   Connected to MCP server <id> and listed <N> tools with headers X-MCP-Readonly, X-MCP-Toolsets
   ```

   The header **names** are logged — never their values — as the one in-application signal that a restriction was actually applied; nothing else records it. `Authorization` is filtered out of that list even though the on-behalf-of bearer travels as a header on the same request, because the log line reports the reserved names as `McpServerHeaderRules.IsReserved` sees them — the bearer is never one of *your* configured headers, so it never appears there regardless. Two things to check: only `X-MCP-Readonly` and `X-MCP-Toolsets` in that list (not `Authorization`, and not anything you did not configure), and a tool count meaningfully smaller than the server's full default (currently around 45) if you configured `X-MCP-Toolsets` to something narrower than `all`.

## 6. Failure modes

Any problem connecting to the server, listing its tools, or acquiring its on-behalf-of token surfaces identically to the calling client: **502 `/problems/mcp-server-unavailable`**, carrying a `serverName` extension naming this registration. The detail message is always the same generic sentence — `MCP server 'Azure DevOps' is unavailable.` — deliberately: these servers are consented tenant-wide by an administrator, so from a signed-in user's point of view a token-acquisition failure here is a broken registration, not something they can act on by re-authenticating. Whatever MSAL actually raised — including the `AADSTS…` code — is written to the API's error log as part of the full exception, never onto the wire.

| `AADSTS` code you find in the log | Likely cause | Where to look |
| --- | --- | --- |
| `AADSTS65001` | Admin consent for the delegated permission was never granted, or was revoked | §2.2 |
| `AADSTS700016` | The calling application is not found in the token-issuing tenant | Confirm the organization's Entra tenant matches the one the app registration and consent live in |
| `AADSTS50105` | The signed-in user is not assigned to the Azure DevOps MCP application (if your tenant requires user assignment on enterprise applications) | Assign the user, or disable assignment-required on that enterprise application |
| `AADSTS50076` / an MFA challenge | Conditional Access requiring interactive multi-factor authentication | Not resolvable through an on-behalf-of exchange at all — a policy scoped to this application's service principal has to be relaxed or excluded |

The client offers **Retry** on this 502, the same as on a transient network failure — but retrying against a registration problem just reproduces the same failure, so treat a `502` that repeats across users and time as this section rather than as noise.

## 7. Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| Every user gets 502 `/problems/mcp-server-unavailable` on this server, immediately | Admin consent missing or revoked (`AADSTS65001` in the log) | §2.2 |
| 502 only for one or two users, others work | Not a registration problem — check that user's own Azure DevOps access (project membership, access level) rather than this runbook | — |
| The assistant creates pull requests or updates work items when you only wanted it reading | `X-MCP-Readonly` is missing, or was cleared by an edit that omitted the Headers field | §4.1, and re-check the field the next time this row is edited |
| A whole category of tools (e.g. pipelines) is unexpectedly available | `X-MCP-Toolsets` omits a category you meant to exclude, or is missing entirely | §4.1 |
| `X-MCP-Toolsets: elm` set but ELM tools still absent | The header alone is not sufficient — the organization must also be enrolled in the ELM private preview | Contact Microsoft, or drop `elm` from the toolset list |
| 400 on save, naming `Headers` | A reserved name (`Authorization` and the rest of [Administration §11.3.2](../ui/administration.md#1132-headers-a-textarea-not-a-row-editor-and-why-it-can-never-become-a-secret-store)'s set), over 8 headers, or over the per-name/per-value/serialized limits | Read the message — it names the offending header — and adjust the Headers field |
| `X-MCP-Toolsets` or `X-MCP-Readonly` appears in the log line but the remote server seems to ignore it | Microsoft's own guidance is that the remote server matches these header **names** case-sensitively — a header registered as `x-mcp-toolsets` is a different name to it than `X-MCP-Toolsets` | Re-check the exact casing against §4 and §4.1 |

## 8. Key files

| Concern | File |
| --- | --- |
| The shared header rules and limits | [`Enterprise.Gpt.Dto/Actions/Mcp/McpServerHeaderRules.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Actions/Mcp/McpServerHeaderRules.cs) |
| Building the transport headers and writing the bearer last | [`Enterprise.Gpt.Service/McpToolProvider.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/McpToolProvider.cs) (`BuildTransportHeaders`, `AcquireAccessTokenAsync`) |
| The `Headers` column and its value converter | [`Enterprise.Gpt.Repository/Configurations/McpServerConfiguration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/McpServerConfiguration.cs), [`Migrations/20260825050920_AddMcpServerHeaders.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260825050920_AddMcpServerHeaders.cs) |
| The admin dialog's Headers field | [`enterprise-gpt-ui/src/app/core/catalog/mcp-server-form.ts`](../../enterprise-gpt-ui/src/app/core/catalog/mcp-server-form.ts), [`features/admin/mcps/mcp-server-form-dialog.html`](../../enterprise-gpt-ui/src/app/features/admin/mcps/mcp-server-form-dialog.html) |
| Related reference | [Administration §11.3.2](../ui/administration.md#1132-headers-a-textarea-not-a-row-editor-and-why-it-can-never-become-a-secret-store), [Permission Cache](../permissions/permission-cache.md), [Microsoft: Set up the remote Azure DevOps MCP Server](https://learn.microsoft.com/azure/devops/mcp-server/remote-mcp-server), [Microsoft: Troubleshoot the remote Azure DevOps MCP Server](https://learn.microsoft.com/azure/devops/mcp-server/remote-mcp-server-troubleshooting) |
