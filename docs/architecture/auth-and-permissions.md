# Authentication and Permissions

How a caller is identified, and how a route decides whether to serve them.

## Sign-in

The client authenticates against Microsoft Entra ID with MSAL (`msal-browser` / `msal-angular`).
MSAL is created and initialized in `main.ts` before `bootstrapApplication`, because `initialize()`
is async and no synchronous DI factory can await it.

The API validates the bearer token with `AddMicrosoftIdentityWebApi` bound to the `AzureAd`
configuration section, with explicit audience validation.
`EnableTokenAcquisitionToCallDownstreamApi` turns on the on-behalf-of flow, which is how MCP servers
registered with `EntraIdOnBehalfOf` receive a token for the signed-in user.

`POST api/users/me` is the sign-in handshake. It provisions or refreshes the caller's `User` row and
**primes the permission cache**. The client runs it from `sessionGuard`, which is nested inside
`authGuard` rather than listed beside it, because guards in one array run concurrently and this one
must not run before a token exists.

Users are keyed by their Entra object id, so identity is stable across renames.

## Permissions

A permission is a row in `Core.Permission`; a grant is a row in `Core.UserPermission`. Three
permissions are built in and seeded:

| Name | Purpose |
| --- | --- |
| `Administrator` | Gates the admin routes and nothing else |
| `Upload File` | Gates document upload |
| `Generate Files` | Gates the File Agent |

MCP servers create a permission per server at registration time; those are authorized through
`McpServers` queries rather than through the endpoint filter.

### Gating a route

Attach the filter and declare the 403:

```csharp
group.MapPost("conversations/{conversationId:guid}", CreateConversationDocumentAsync)
    .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.UploadFile))
    .ProducesProblem(StatusCodes.Status403Forbidden);
```

Three rules:

- **`Require` is AND, not OR.** `Require(a, b)` admits only a caller holding an active grant of
  *both*. There is no built-in "any of these" form.
- **Administrators are not implicitly granted everything.** `PermissionIds.Administrator` gates
  admin routes only.
- **Ids must be named.** Every id passed must have an entry in `PermissionIds.Names`, a
  `FrozenDictionary<Guid, string>`. `Require` validates its arguments **at map time**, so an unknown
  id throws during startup rather than serving a nameless 403 months later.

Names come from that static map rather than from `Core.Permission`, because reading them from the
database would reintroduce exactly the query the cache exists to remove. When seeding a new built-in
permission, add it to `PermissionIds.Names` in the same change.

### The denial

A caller missing any required grant gets a 403 of type `/problems/permission-required`:

```json
{
  "type": "/problems/permission-required",
  "title": "Permission required",
  "status": 403,
  "detail": "Administrator permission is required.",
  "instance": "/api/users",
  "traceId": "00-a1b2c3d4e5f60718293a4b5c6d7e8f90-1122334455667788-01",
  "permissions": ["Administrator"]
}
```

`detail` and the `permissions` extension name the **whole requirement**, not the missing part, so
the response does not vary with which grants the caller happens to hold and a probing client learns
nothing about its own partial state.

## The permission cache

Authorization reads the singleton `IUserPermissionCache`
(`Service/Caching/UserPermissionCache.cs`), not the database. Grants change rarely and are read
constantly, which is the textbook shape for a cache; before it, every gated request cost a round
trip and forced an `EnterpriseGptDbContext` into existence on requests that would never otherwise
touch SQL.

- Entries are written at sign-in and loaded lazily on a miss, single-flight via
  `Lazy<Task<T>>` — the same shape as `McpClientCache`.
- A user with zero grants caches the **empty set**. Treating that as a miss would restore the
  per-request query.
- Invalidation is **per user, never a full reload**, and is raised from `PermissionService`,
  `UserService`, and `McpServerService.DeactivateMcpServerAsync`.
- Entries carry an absolute `Permissions:Cache:EntryLifetime` (default 5 minutes). That exists
  purely as the scale-out staleness bound, since invalidation only reaches the instance that served
  the mutation.

Two read paths deliberately stay on the database: `McpToolProvider.AcquireToolsAsync` and
`McpServerService.GetPermittedMcpServersAsync`. Read the comments there before moving them onto the
cache.

## Secrets

`AddEnterpriseKeyVault()` is the first line of `Program.cs`, so configuration rebuilds — or fails —
before anything reads a secret. Key Vault secret names use `--` where the configuration key uses
`:`. Prefer `DefaultAzureCredential` and managed identity over secrets in configuration.

Per-user MCP credentials are encrypted with ASP.NET Core Data Protection. The key ring lives in
`dbo.DataProtectionKeys` in the application database and is wrapped by a Key Vault key, so a
database copy alone does not disclose them.

Signed document download links are short-lived bearer credentials, not identifiers — treat a leaked
link as a leaked file. See [../documents/downloads.md](../documents/downloads.md).

## Known limit

Deactivating a user does not revoke their session. An issued access token stays valid until it
expires; the next `POST api/users/me` is what fails.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs` | JWT bearer, on-behalf-of, Key Vault, Data Protection |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Filters/PermissionEndpointFilter.cs` | The AND filter and its map-time validation |
| `enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/PermissionIds.cs` | Built-in ids and the static name map |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Caching/UserPermissionCache.cs` | The cache and its invalidation |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Security/UserSecretProtector.cs` | Data Protection wrapper for user credentials |
| `enterprise-gpt-ui/src/app/core/auth/` | MSAL instance, guards, bearer interceptor |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Services/` | Cache invalidation and grant-diff tests |

## Related

- [../admin/users-and-permissions.md](../admin/users-and-permissions.md)
- [../operations/configuration.md](../operations/configuration.md)
- [../tools/mcp-servers.md](../tools/mcp-servers.md)
