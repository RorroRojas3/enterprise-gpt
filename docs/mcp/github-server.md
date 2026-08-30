# Registering the Remote GitHub MCP Server — Operator Runbook

Audience: the **platform operator** registering an MCP tool server through the admin area, and the **user** who has to supply their own credential before the server does anything. This is the reference case for `McpAuthTypes.UserApiKey` — the third auth type, where each user brings their own bearer credential rather than the deployment holding one on everyone's behalf. Read [Administration §11](../ui/administration.md#11-the-mcp-server-registry-us-1208-us-1210) first for the registry screen itself; this page is what to type into it for this one server, and what a GitHub personal access token needs to look like before a user hands it over.

Companion: [Azure DevOps Server](azure-devops-server.md), the reference case for the *other* auth type — `EntraIdOnBehalfOf`, one deployment-wide credential the API acquires on the user's behalf. The two runbooks are deliberately different shapes for that reason: this one has no Entra ID section, because there is no tenant-wide consent here to grant, and it has a section that one doesn't, because a working registration is only half of what has to happen before anyone can use the server.

## 1. Why this exists

GitHub's [remote MCP server](https://github.com/github/github-mcp-server/blob/main/docs/remote-server.md) is hosted by GitHub itself at `https://api.githubcopilot.com/mcp/`, reached over streamable HTTP — the same shape as the Azure DevOps server, and registered the same way. What is different is *whose* credential it runs on. Azure DevOps is provisioned once, tenant-wide, by an administrator who consents on everyone's behalf; GitHub has no equivalent tenant-wide grant to give; a token that reaches a user's repositories has to be that user's own. Before this feature the only credential shape this application had was the deployment's own on-behalf-of token or nothing at all — there was no way to register a server that needs a *personal* one.

`McpAuthTypes.UserApiKey` is that third shape. Each user who wants to use the server supplies their own token, stored **encrypted at rest** ([`UserSecretProtector`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Security/UserSecretProtector.cs), ASP.NET Core Data Protection — see [Key Vault Configuration §10](../configuration/key-vault.md#10-wrapping-the-data-protection-key-ring-per-user-mcp-credentials) for what wraps the key ring that protects it) and sent as `Authorization: Bearer <token>` on every request the API makes to GitHub on that user's behalf. Nobody but that user — not an administrator, not another user of the same server — can read it back; no route returns it, and the only trace left in the UI is its last four characters.

## 2. Prerequisites: a GitHub personal access token

Registering the server (§4) needs nothing from GitHub at all — it is a URL and an auth type, exactly like any other server. What needs a GitHub token is **each user**, the first time they try to use it (§5), and that token is theirs to create and theirs to manage; there is nothing for an administrator to provision on their behalf.

### 2.1 Which token types work

| Token type | Prefix | How the server treats it |
| --- | --- | --- |
| **Classic personal access token** | `ghp_` | The server reads the token's OAuth scopes off the `X-OAuth-Scopes` response header and hides tools that scope does not cover — a `repo`-scoped token gets repository and pull-request tools but nothing under `admin:org`. Requires restarting nothing on this side: the check runs per connection |
| **Fine-grained personal access token** | `github_pat_` | GitHub does not return `X-OAuth-Scopes` for these, so the server shows every tool up front and lets GitHub's own API enforce the token's actual repository and permission scoping at call time — a tool this token cannot use fails when it is called, not before |
| GitHub App installation token, server-to-server token | `ghs_` and others | Not a realistic choice here: these are minted for an app or a service, not typed in by a person, and this dialog has nowhere to put the extra plumbing that would issue one |

Either of the first two is a reasonable choice. A fine-grained token scoped to a handful of repositories with read-only Issues, Pull requests and Contents permissions is the narrower, more auditable option, at the cost of the tool list not reflecting that narrowing until a call actually fails; a classic token with `repo` is simpler to reason about and the tool list itself reflects what it can and cannot do. Either way, **the scopes on the token are what actually govern what the assistant can reach** — this application has no opinion about that and enforces nothing beyond storing and returning the value the user supplied. See [GitHub: Managing your personal access tokens](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens) for creating one, and [github-mcp-server: scope filtering](https://github.com/github/github-mcp-server/blob/main/docs/scope-filtering.md) for exactly how the server reads a classic token's scopes.

### 2.2 The shape this application accepts

`SaveMcpCredentialActionDtoValidator` (`Enterprise.Gpt.Dto/Actions/Mcp/McpCredentialActions.cs`) refuses a value before it is ever sent to GitHub:

| Rule | Value |
| --- | --- |
| Minimum length | 8 characters — anything shorter is a typo, not a token |
| Maximum length | 512 characters |
| Shape | Printable ASCII, no whitespace — a token pasted with a trailing newline or wrapped in quotes is refused with a message that says so, rather than being sent to GitHub and rejected there |

A real GitHub token of either kind comfortably fits inside this. The 512-character ceiling is generous headroom, not a GitHub-specific number — it is what the encrypted payload's storage column was sized against, not a limit GitHub itself documents.

## 3. Register the server

In the admin area's MCP servers tab ([Administration §11](../ui/administration.md#11-the-mcp-server-registry-us-1208-us-1210)), **Add server**:

| Field | Value |
| --- | --- |
| Name | `GitHub` (or your registry's own convention — this also names the permission it creates) |
| Description | Something naming what it reaches, e.g. "Issues, pull requests and repository content, one API key per user" |
| URL | `https://api.githubcopilot.com/mcp/` |
| Auth type | **API key (per user)** |
| Scope | Left blank — `UserApiKey` carries no scope field, the same way `None` does not (§3.1) |
| Icon | `github` |
| Headers | Optional — see §3.2 |

Or the equivalent `POST api/mcps`:

```bash
curl -X POST https://localhost:7045/api/mcps \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
        "name": "GitHub",
        "description": "Issues, pull requests and repository content, one API key per user.",
        "url": "https://api.githubcopilot.com/mcp/",
        "authType": 3,
        "scope": null,
        "iconKey": "github",
        "headers": null
      }'
```

`authType: 3` is `UserApiKey` — the third member of `McpAuthTypes`, added beside `None` (1) and `EntraIdOnBehalfOf` (2). This is the only field that differs in kind from every other server this application registers: it names *how* the caller authenticates without the row itself carrying anything that authenticates — no scope, no stored secret, nothing an administrator has to protect.

### 3.1 Why there is no scope field to fill in

`requiresScope` — both the client's copy in `core/catalog/mcp-server-form.ts` and the server's own validator — is `true` for exactly one auth type, `EntraIdOnBehalfOf`. A scope is what the on-behalf-of exchange asks for; a user-supplied bearer token carries whatever GitHub already granted it, decided when the token was created (§2), not by anything this registration states. Leaving it blank is not an oversight to fill in later — it is refused outright if you try.

### 3.2 The optional headers, and why they are unaffected by this feature

`X-MCP-Toolsets` and `X-MCP-Readonly` are the same two headers documented for [Azure DevOps §4.1](azure-devops-server.md#41-why-these-two-headers-specifically) — non-secret configuration the *remote* server reads off every request, unrelated to how the connection authenticates. GitHub's own remote server happens to read the same two header names for the same purpose:

| Header | Effect |
| --- | --- |
| `X-MCP-Toolsets` | Comma-separated toolset categories to enable — `repos`, `issues`, `pull_requests`, `actions`, `discussions`, `orgs`, `code_security`, and others GitHub documents; an unrecognised name is silently ignored rather than refused |
| `X-MCP-Readonly` | `true` restricts the server to read-only tools; anything reading as false (`false`, `f`, `no`, `n`, `0`, `off`, or blank) leaves write tools available |

The default endpoint (`https://api.githubcopilot.com/mcp/`, the one this runbook registers) already serves GitHub's own curated default toolset rather than everything the server can do; `X-MCP-Toolsets` narrows *within* that default, and appending `x/all` to the URL instead of setting the header is GitHub's own way to opt into every toolset. Neither header is required, and — the same rule Administration §11.3.2 states for Azure DevOps's headers — **these carry no credential and are unaffected by the auth type**: whether this server is `None`, `EntraIdOnBehalfOf` or `UserApiKey`, the Headers field is read and applied identically, because it configures the third party's own behaviour, not this application's authentication to it. Values render unmasked in the admin dialog for the same reason they do on every other server: there is nothing here to hide.

## 4. Grant the permission

`McpServerService` creates a permission named after the server the moment it is registered — `GitHub`, if that is what you named it — the same as every other MCP server. Grant it to whoever should see the server in their Tools menu, through the existing users tab or `PUT api/users/{id}/permissions` ([Administration §7](../ui/administration.md#7-edit-permissions-us-1203)). Holding the permission is what makes the server appear in the picker at all; it says nothing about whether that user has supplied a working GitHub token yet — that is §5.

## 5. What a user sees on first use

Holding the permission is necessary but not sufficient. `GET api/mcps` reports two more things about this server, per caller: `requiresUserApiKey` (true for any `UserApiKey` server) and `hasUserApiKey` (whether *this* caller currently has a usable one stored). Until both are true, the composer's Tools menu renders the row differently from every toggleable server:

1. **No key stored yet.** The row renders as a plain menu item — not a switch — carrying a "Key required" status dot. It cannot be toggled on; clicking it opens a dialog asking for an access token, with a one-line explanation that the token is stored encrypted and shown again only as its last four characters.
2. **Saving a key.** `PUT api/mcps/{id}/credential` stores it and the dialog closes; the row now behaves like any other server's — a switch the user can turn on for the next turn — with a second small menu item beside it reading `••••` followed by the token's last four characters. Clicking that item reopens the dialog to replace or remove the key.
3. **Using it.** Once selected and toggled on, the API decrypts the stored token for that user on the next turn and sends it as `Authorization: Bearer <token>` when it connects to GitHub — the same connection every other server gets, just authenticated with the caller's own credential instead of the deployment's.

Nothing here is administrator-visible. `McpServerDto` — the administrative shape `GET api/mcps/all` returns — carries no per-user credential fields at all; `hasUserApiKey` and `apiKeyHint` exist only on the picker's own `McpDto`, answering "does the *caller* have one," never "who has one."

## 6. Failure modes

Two problem types exist for this auth type specifically, both **428 Precondition Required**, both carrying `mcpServerId` and `serverName` extensions so a client can open the key dialog for the right server rather than only naming it in an error banner:

| Status | `type` | When |
| --- | --- | --- |
| `428` | `/problems/mcp-credential-required` | The user selected this server for a turn and has no usable key stored — never saved one, removed it, or the stored payload no longer decrypts (§6.1) |
| `428` | `/problems/mcp-credential-rejected` | GitHub answered `401` or `403` to a connection made with the user's stored key |

Both differ from the `502 /problems/mcp-server-unavailable` every other MCP failure produces, deliberately: a 502 offers **Retry**, because retrying can plausibly get past a transient failure. Neither of these can — the only way past either is supplying a working key, so the client does not offer Retry on them at all, and shows the reason inline rather than as a toast (the key dialog is where the reader is sent to fix it).

### 6.1 What makes a stored key stop being usable

- **The token was revoked, expired, or never carried a scope the request needed.** `McpToolProvider.Classify` recognizes a `401` or `403` reported by the connection — flattened across `AggregateException`, because the SDK reports its Streamable HTTP attempt and its SSE fallback together — and raises `McpCredentialRejectedException` rather than treating it as an unreachable server. The row is stamped `DateRejected` at that moment, which is what makes `hasUserApiKey` read `false` on the caller's next `GET api/mcps` and the picker ask for a new one instead of repeating the same failed turn.
- **The Data Protection key ring cannot open the stored payload.** See [Key Vault Configuration §10](../configuration/key-vault.md#10-wrapping-the-data-protection-key-ring-per-user-mcp-credentials) — a row that fails to decrypt reads as though nothing were stored, the same 428 as never having saved one.
- **An administrator repointed the URL, or changed the auth type away from `UserApiKey`.** Both discard **every** stored credential for the server, deliberately (§6.2) — every affected user sees `mcp-credential-required` on their next turn, exactly as if they had never saved a key.
- **The server was deactivated.** `DELETE api/mcps/{id}` cascades to every stored credential the same way it cascades to the permission — a server nobody can select any more should not go on holding tokens for it either.

### 6.2 Repointing the URL or the auth type discards every stored key

`UpdateMcpServerAsync` compares the incoming request against the row it is about to overwrite, and discards every active `Core.UserMcpCredential` for the server when either is true: the auth type is changing away from `UserApiKey`, or the URL is changing at all (an ordinal, case-insensitive string comparison — even a trailing-slash difference counts as a change). Neither is a bug to route around; a stored token is consent to send it to **one** endpoint, and editing that endpoint without discarding what was consented to would forward every user's token to wherever the row now points, with nothing asked and nothing shown. If you are renaming or re-registering this server rather than truly repointing it, expect every user to be asked for their key again — that is the safe behaviour, not a defect in the edit.

## 7. Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| The server does not appear in a user's Tools menu at all | They hold no grant of the permission this registration created | §4 |
| The row shows "Key required" and cannot be toggled on | No usable key stored for that user yet — never supplied one, or it was rejected/discarded (§6) | The user opens the row and supplies a token (§2, §5) |
| `mcp-credential-required` on every turn even after saving a key | The save itself may have failed silently on the client, or the row was edited since (§6.2) — check whether the key dialog still shows a hint | Re-open the dialog and save again |
| `mcp-credential-rejected` right after a fresh save | The token is real but lacks the scope the requested tool needs, or was revoked between creation and use | Check the token's scopes against §2.1, and that it has not been revoked or expired on GitHub's side |
| The assistant can read but never write (or the reverse) to GitHub | `X-MCP-Readonly` is set (or a classic token's own scopes do not include write access) | §3.2 for the header, §2.1 for how a classic token's scopes are read |
| A whole category of tools is unexpectedly unavailable | `X-MCP-Toolsets` narrows to a set that excludes it, or a classic token's scopes do not cover it | §3.2, §2.1 |
| One user's key works and another's does not, same server | This is per-user by design — check that specific user's token rather than the registration | §2, §5 |
| Every user was asked to re-supply their key after an edit to this row | The URL or auth type changed on that edit | §6.2 — expected, not a defect |

## 8. Key files

| Concern | File |
| --- | --- |
| The third auth type | [`Enterprise.Gpt.Common/Enums/McpAuthTypes.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Common/Enums/McpAuthTypes.cs) |
| Encrypting and decrypting the stored token | [`Enterprise.Gpt.Service/Security/UserSecretProtector.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Security/UserSecretProtector.cs) |
| Saving, removing and marking a credential rejected | [`Enterprise.Gpt.Service/UserMcpCredentialService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/UserMcpCredentialService.cs) |
| `PUT`/`DELETE api/mcps/{id}/credential` | [`Enterprise.Gpt.Api/Endpoints/McpEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/McpEndpoints.cs) |
| Resolving the caller's key and classifying a 401/403 as a rejection | [`Enterprise.Gpt.Service/McpToolProvider.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/McpToolProvider.cs) (`AcquireLeaseAsync`, `Classify`) |
| Discarding credentials on deactivate or a repointing edit | [`Enterprise.Gpt.Service/McpServerService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/McpServerService.cs) |
| The `Core.UserMcpCredential` table | [`Migrations/20260829225848_AddUserMcpCredentialStorage.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260829225848_AddUserMcpCredentialStorage.cs) |
| The composer's "Key required" row and masked-key menu item | [`enterprise-gpt-ui/src/app/shared/composer/tools-menu.html`](../../enterprise-gpt-ui/src/app/shared/composer/tools-menu.html) |
| The key dialog | [`enterprise-gpt-ui/src/app/features/shell/mcp-credential/`](../../enterprise-gpt-ui/src/app/features/shell/mcp-credential/) |
| Related reference | [Administration §11](../ui/administration.md#11-the-mcp-server-registry-us-1208-us-1210), [Key Vault Configuration §10](../configuration/key-vault.md#10-wrapping-the-data-protection-key-ring-per-user-mcp-credentials), [the Azure DevOps runbook](azure-devops-server.md) (the other auth type), [GitHub: github-mcp-server remote server docs](https://github.com/github/github-mcp-server/blob/main/docs/remote-server.md), [GitHub: scope filtering](https://github.com/github/github-mcp-server/blob/main/docs/scope-filtering.md), [GitHub: managing personal access tokens](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens) |
