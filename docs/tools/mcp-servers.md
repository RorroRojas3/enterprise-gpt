# MCP Tool Servers

Remote Model Context Protocol servers whose tools the model can call during a turn: the registry,
how a caller is authorized to one, how the connection is authenticated, and how failures surface.

## The registry

An administrator registers a server in `Core.Ref.McpServer`: name, URL, auth type, optional headers,
and an icon key. Registration also **creates a permission named for the server**, so per-server
access is granted with the same mechanism as everything else.

| Route | Access |
| --- | --- |
| `GET api/mcps` | The caller's permitted servers, with per-caller credential state |
| `GET api/mcps/all`, `GET api/mcps/{id}` | Admin |
| `POST api/mcps`, `PUT api/mcps/{id}`, `DELETE api/mcps/{id}` | Admin |
| `PUT api/mcps/{id}/credential`, `DELETE api/mcps/{id}/credential` | The caller, for their own key |

`GET api/mcps/all` returns `McpServerDto`, the administrative shape, which carries **no per-user
credential fields at all**. `hasUserApiKey` and `apiKeyHint` exist only on the picker's `McpDto`,
answering "does the caller have one", never "who has one".

Deactivating a server cascades to its permission **and** to every stored credential — a server
nobody can select should not go on holding tokens for it.

## Authentication

```csharp
public enum McpAuthTypes
{
    None = 1,
    EntraIdOnBehalfOf = 2,
    UserApiKey = 3
}
```

- **`None`** — no credential.
- **`EntraIdOnBehalfOf`** — a token acquired on behalf of the signed-in user through the OBO flow.
  These servers are consented tenant-wide, which is why a token acquisition that comes back
  *needing user interaction* is treated as a broken registration rather than a user-actionable
  state.
- **`UserApiKey`** — a bearer credential each user issues and supplies themselves, such as a GitHub
  personal access token. Stored encrypted per user, and returned by no route.

### The per-user key flow

Holding the server's permission is necessary but not sufficient. `GET api/mcps` reports two more
things per caller: `requiresUserApiKey`, and `hasUserApiKey` for *this* caller. Until both are true
the composer's Tools menu renders the row as a plain menu item with a "Key required" status dot
rather than a switch; clicking it opens the key dialog.

Once `PUT api/mcps/{id}/credential` stores a key the row behaves like any other server's, with a
small sibling item showing the token's last four characters that reopens the dialog to replace or
remove it. On the next turn the API decrypts that user's token and sends it as
`Authorization: Bearer <token>` — the same connection every other server gets, authenticated with
the caller's own credential instead of the deployment's.

Keys are encrypted with ASP.NET Core Data Protection, whose key ring lives in the application
database wrapped by a Key Vault key.

### What makes a stored key stop being usable

- **Revoked, expired, or missing a scope.** `McpToolProvider.Classify` recognizes a 401 or 403 from
  the connection — flattened across `AggregateException`, because the SDK reports its Streamable
  HTTP attempt and its SSE fallback together — and raises `McpCredentialRejectedException` rather
  than treating it as an unreachable server. The row is stamped `DateRejected`, which is what makes
  `hasUserApiKey` read false on the next `GET api/mcps` so the picker asks for a new key instead of
  repeating the same failed turn.
- **The key ring cannot open the payload.** The row reads as though nothing were stored.
- **An administrator repointed the URL, or changed the auth type away from `UserApiKey`.** Both
  discard **every** stored credential for the server.
- **The server was deactivated.**

### Why repointing discards every key

`UpdateMcpServerAsync` compares the incoming request against the row it is about to overwrite and
discards every active credential when the auth type moves away from `UserApiKey`, or when the URL
changes at all — an ordinal, case-insensitive comparison, so even a trailing slash counts.

A stored token is consent to send it to **one** endpoint. Editing that endpoint without discarding
what was consented to would forward every user's token to wherever the row now points, with nothing
asked and nothing shown. Renaming or re-registering a server therefore asks every user for their key
again; that is the safe behaviour, not a defect in the edit.

## Acquiring tools for a turn

`McpToolProvider` is scoped and resolves per request: it reads the caller's permitted servers,
acquires a connection for each selected one, and leases its tools.

**Every leased tool is renamed** to `{sanitizedServer}_{tool}` before it reaches the tracking
wrapper, and the same sanitized server name is passed as the activity `source` — deliberately,
rather than letting the wrapper read the server's self-advertised name, which need not agree. If
they diverged the prefix would no longer be strippable by the client. See
[../conversations/streaming.md](../conversations/streaming.md).

`McpClientCache` holds live connections, single-flight via `Lazy<Task<T>>` with a configured
lifetime under `Mcp:Cache`.

`McpToolProvider.AcquireToolsAsync` and `McpServerService.GetPermittedMcpServersAsync` deliberately
query the database rather than the permission cache. Read the comments there before changing that.

## Failure modes

| Status | `type` | When | Retryable |
| --- | --- | --- | --- |
| 428 | `mcp-credential-required` | The caller selected a `UserApiKey` server and has no usable key | no |
| 428 | `mcp-credential-rejected` | The server answered 401 or 403 to the caller's stored key | no |
| 502 | `mcp-server-unavailable` | Unreachable, or an OBO token could not be acquired | yes |

Both 428s carry `mcpServerId` and `serverName` extensions so a client can open the key dialog for
the right server rather than only naming it in a banner. They are separated from the 502 on purpose:
a 502 offers **Retry**, because retrying can plausibly get past a transient failure, and neither 428
can — the only way past either is supplying a working key, so the client shows the reason inline and
sends the reader to the dialog.

## Configuration

| Key | Purpose |
| --- | --- |
| `Mcp:Cache` | Connection cache lifetime and bounds |

Per-server URL, auth type and headers are catalog data, not configuration.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Service/McpServerService.cs` | Registry CRUD, permission linkage, credential discard rules |
| `enterprise-gpt-api/Enterprise.Gpt.Service/McpToolProvider.cs` | Per-turn acquisition, renaming, failure classification |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Caching/McpClientCache.cs` | Live connections, single-flight |
| `enterprise-gpt-api/Enterprise.Gpt.Service/UserMcpCredentialService.cs` | Per-user key storage |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Security/UserSecretProtector.cs` | Data Protection wrapper |
| `enterprise-gpt-ui/src/app/core/catalog/mcp-credential-store.ts` | The key dialog's state |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Services/` | Discard rules and failure classification |

## Related

- [../operations/runbooks.md](../operations/runbooks.md) — registering the GitHub and Azure DevOps servers
- [../architecture/auth-and-permissions.md](../architecture/auth-and-permissions.md)
- [../conversations/streaming.md](../conversations/streaming.md)
