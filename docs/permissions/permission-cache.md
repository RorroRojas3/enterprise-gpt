# Permission Cache

Reference for how the API authorizes a request against a user's permission grants: the endpoint filter that gates routes, the in-process cache the filter reads, and the invalidation rules that keep it honest. Audience: engineers adding gated endpoints, changing grant-mutating services, or operating a scaled-out deployment.

## 1. Why this exists

Every gated route in this API authorizes off rows in `Core.UserPermission`. Before the cache, that meant **one database round trip per gated request, per filter** — and because the check ran inside an endpoint filter, it also forced an `EnterpriseGptDbContext` into existence on requests that would otherwise never touch SQL Server. Admin screens that fan out to several endpoints paid that cost on every call.

Grants change rarely and are read constantly, which is the textbook shape for a cache. [`IUserPermissionCache`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Caching/UserPermissionCache.cs) holds each signed-in user's active permission ids in memory, so a warm request performs **zero permission queries** and constructs **no EF context** for authorization.

The trade is staleness, and authorization is the worst possible place to be casually stale. Section 5 is the design's answer: invalidation is exact and immediate on the instance that served the mutation, and the entry lifetime exists only to bound the case that eviction cannot reach.

## 2. Quick start — gating an endpoint

Attach [`PermissionEndpointFilter.Require(...)`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Filters/PermissionEndpointFilter.cs) to the route and declare the 403 in the OpenAPI document. You do not touch the cache yourself:

```csharp
group.MapPost("conversations/{conversationId:guid}", CreateConversationDocumentAsync)
    .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.UploadFile))
    .ProducesProblem(StatusCodes.Status403Forbidden);
```

Admin-only routes are the same call with the built-in Administrator id:

```csharp
group.MapDelete("{id:guid}", DeactivateModelAsync)
    .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound);
```

Three things to know before you use it:

- **`Require` is AND, not OR.** `Require(a, b)` admits only callers holding an active grant of *both*. There is no built-in "any of these" form; a route that needs one would have to compose it explicitly.
- **Administrators are not implicitly granted everything.** `PermissionIds.Administrator` gates admin routes and nothing else. An administrator who lacks `Upload File` gets a 403 from the upload route, exactly like anyone else.
- **Ids must be named.** Every id you pass must have an entry in [`PermissionIds.Names`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/PermissionIds.cs) (see §3).

### 2.1 The denial response

A caller missing any required grant gets a 403 [Problem Details](https://www.rfc-editor.org/rfc/rfc9457) body of type `/problems/permission-required`:

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

`detail` and the `permissions` extension name the **whole requirement**, not the missing part, so the response does not vary with which grants the caller happens to hold — a probing client learns nothing about its own partial state. With two requirements the prose joins them: `"Administrator and Upload File permissions are required."`

## 3. Permission names are resolved from a static map, not the database

The filter takes ids only, but the 403 body needs display names. Reading them from `Core.Permission` would reintroduce exactly the query the cache exists to remove, so names come from [`PermissionIds.Names`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/PermissionIds.cs), a `FrozenDictionary<Guid, string>` covering the built-in permissions:

| Id | Name |
|---|---|
| `a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d` | `Administrator` |
| `b1c2d3e4-f5a6-4b7c-8d9e-0f1a2b3c4d5e` | `Upload File` |

`Require` validates its arguments against that map **at map time**, not at request time. Pass an id with no entry and the application throws `ArgumentException` during startup rather than serving a nameless 403 months later. Passing no ids at all throws `ArgumentOutOfRangeException`; duplicates are collapsed and named once.

The map covers exactly the ids usable in a filter. MCP-derived permissions are created per server at runtime and are never named in a `Require` call, so they are deliberately absent — they are authorized through `McpServers` queries instead (§6). When you seed a new built-in permission, add it to `PermissionIds.Names` in the same change, and keep the string in step with the seeded `Core.Permission` row.

## 4. How a request reads the cache

```
Client ──► RequireAuthorization (JWT) ──► PermissionEndpointFilter.Require(ids)
       ──► IUserGrantReader.GetGrantsAsync(oid)
       ──► IUserPermissionCache.GetOrLoadAsync(oid)
              hit  ──► compare ids in memory ──► handler   (no DbContext, no query)
              miss ──► load every active grant for the oid ──► cache ──► compare
```

**`IUserGrantReader` (`Service/Caching/UserGrantReader.cs`) is the one seam every caller goes through**, endpoint filter or not. It exists because a permission check stopped being only an endpoint concern once document summarization needed to ask "does this user hold `Upload File`" from inside `ConversationService.CreateChatOptionsAsync` — service code with no `HttpContext` or endpoint filter to sit in. `PermissionEndpointFilter` used to carry its own private grant-loading query in parallel with whatever a service might need; that duplication is gone, and the filter now resolves `IUserGrantReader` and calls it like everything else. There is exactly one grant-loading query in the codebase. See [Document Summarization: Tool, Persistence and Billing §8](../summarization/tool-integration.md#8-the-shared-permission-grant-reader) for why the consolidation also closed a latent scope-lifetime bug, not just a style one.

The mental model is one entry per signed-in user, holding the **complete** set of permission ids they actively hold:

- **Keyed by Entra object id.** `User.Id` *is* the oid, so the loader queries `Core.UserPermission` directly with no join to `Core.User`.
- **The whole grant set is loaded, never a single id.** One query serves every filter a request passes through and every gated route for the rest of that user's session.
- **Loads are single-flight.** Concurrent misses for the same user share one loader (`ConcurrentDictionary<Guid, Lazy<Task<…>>>` — the same shape as [`McpClientCache`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Caching/McpClientCache.cs)), so a burst of parallel requests on a cold entry produces one query, not one per request.
- **Failures are never cached.** A transient database error propagates and the next request runs a fresh loader. Caching a failure would lock a user out for a whole entry lifetime over a blip.
- **A user with zero grants caches the empty set, and that is a hit.** Treating "no permissions" as a miss would put every unprivileged caller back on a per-request query — the exact cost the cache removes — so the empty set is a first-class cached value.

### 4.1 Warm-fill at sign-in

The Angular shell posts `POST /api/users/me` on every authenticated bootstrap, and [`UserService.GetOrCreateCurrentUserAsync`](../../enterprise-gpt-api/Enterprise.Gpt.Service/UserService.cs) already reads the caller's grants to build the profile response. It publishes that set into the cache on the way out, so the entry is warm before the UI hits its first gated route. The lazy load in §4 is the fallback, not the normal path.

That write is ordered against concurrent evictions by a **global invalidation stamp**. The service captures `CaptureInvalidationStamp()` *before* reading grants and passes it to `Set(userId, stamp, permissionIds)`; if any eviction happened in between, the write is skipped. Without it, a sign-in that read its grants a moment before a revoke committed would republish the pre-revoke snapshot and hand back the revoked access for a full entry lifetime. A skipped write costs the next request one load, so it fails safe.

The stamp is global rather than per user, so an unrelated user's eviction can also skip a `Set`. That keeps the check to a single comparison, and the worst case is one extra load.

## 5. Invalidation

**Invalidation is always per user and never a full reload.** Every service that mutates grants evicts exactly the affected users after its `SaveChangesAsync` commits:

| Trigger | Evicts | Notes |
|---|---|---|
| `PermissionService.GrantPermissionAsync` | the grantee | |
| `PermissionService.RevokePermissionAsync` | the target user | `ExecuteUpdateAsync` bypasses the change tracker, so a non-zero row count is the only signal anything changed |
| `PermissionService.DeactivatePermissionAsync` | **every holder of that permission** | ids collected from the already-`Include`d grant graph — the cascade costs no extra query |
| `UserService.CreateUserAsync` | the pre-created / revived user | evicted rather than `Set`, because the revive path assembles its set by diffing; it also clears any empty-set entry a probing request cached before the row existed |
| `UserService.UpdateUserAsync` | the edited user | covers the whole permission diff in one eviction |
| `UserService.DeactivateUserAsync` | the deactivated user | half of what removes their access; the grant cascade is the other half |
| `McpServerService.DeactivateMcpServerAsync` | **every holder of that server's permission** | same already-included-graph collection as `DeactivatePermissionAsync` |

Two of these are cascades that evict a set of users at once. Both read the affected ids off the EF graph they already loaded to perform the soft delete, so a cascade over hundreds of grants still costs zero additional queries.

### 5.1 `EntryLifetime` — the scale-out staleness bound

Each entry also carries an absolute TTL, evaluated **lazily on read**. There is no background sweep: a stale entry is noticed when someone reads it, and entries for users who never return are held until the process recycles.

`EntryLifetime` is **a safety net, not the primary invalidation mechanism.** Eviction is exact and immediate — but only on the instance that served the mutation. The cache is per process, so `EntryLifetime` bounds the one case eviction cannot reach: **an instance that did not serve the mutation.**

> **Operational caveat — read this before scaling out.** In a multi-instance deployment, a revoked grant, a deactivated user, or a deactivated administrator can retain access on *other* instances for up to `EntryLifetime` (default **5 minutes**). Single-instance deployments converge immediately and are unaffected. This does not weaken Entra ID sign-in control — disabling the account in the directory still cuts off token issuance — but it does mean application-side revocation is eventually consistent across instances.

Practical guidance:

- Shorten `EntryLifetime` before scaling out widely; lengthen it only for a single-instance deployment.
- For an emergency offboarding, disable or delete the account in Entra ID. That is the immediate control; application-row deactivation is not an emergency stop (see [User Management §8](../users/user-management.md#8-limitation--deactivation-is-not-session-revocation)).
- If sub-TTL convergence is ever required, the intended fix is to fan `Invalidate(userId)` out across instances over Redis pub/sub or Azure Service Bus, then reduce the lifetime to a backstop. The interface is already shaped for it — the eviction call sites need no change.

### 5.2 Configuration

Bound from the `Permissions:Cache` section into [`UserPermissionCacheOptions`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/UserPermissionCacheOptions.cs):

```json
{
  "Permissions": {
    "Cache": {
      "EntryLifetime": "00:05:00"
    }
  }
}
```

| Setting | Type | Default | Meaning |
|---|---|---|---|
| `EntryLifetime` | `TimeSpan` | `00:05:00` | Absolute time an entry is served before it is reloaded on the next read |

The value is validated with `ValidateOnStart()` in [`Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) and must be greater than zero: a non-positive lifetime would silently degrade the cache back into a per-request query, which is a performance regression no test would catch. The cache is registered as a **singleton**; entries do not survive a restart or a deployment, and the first request after either pays one load.

## 6. Two read paths deliberately still query the database

Both of these look like obvious candidates for the cache. Both were left alone on purpose — check the comment in the source before "optimizing" either.

| Path | Why it stays on the database |
|---|---|
| [`McpToolProvider.AcquireToolsAsync`](../../enterprise-gpt-api/Enterprise.Gpt.Service/McpToolProvider.cs) | A tool call runs code on the user's behalf against a **third-party** server, so a revoked grant must take effect on the next request rather than at TTL expiry. The check is one query next to establishing an MCP session — the cost is noise. |
| [`McpServerService.GetPermittedMcpServersAsync`](../../enterprise-gpt-api/Enterprise.Gpt.Service/McpServerService.cs) | It has to query `McpServers` regardless, so the cache saves no round trip — only an `EXISTS` over an indexed foreign key — while making a **user-visible list** eventually consistent. |

The rule this encodes: cache the authorization check when the alternative is a query you would not otherwise run, and skip it when the query is already on the path or when the blast radius of stale access reaches outside the application.

## 7. API reference

### `IUserGrantReader`

| Member | Purpose |
|---|---|
| `Task<IReadOnlySet<Guid>> GetGrantsAsync(Guid userId, CancellationToken)` | Returns the user's active permission ids — a real, empty set for a user with no grants, never a miss. Backed by `IUserPermissionCache.GetOrLoadAsync` below, with its own loader that opens a fresh `IServiceScopeFactory` scope on a miss rather than closing over the caller's own scoped `EnterpriseGptDbContext`. |

### `IUserPermissionCache`

| Member | Purpose |
|---|---|
| `Task<IReadOnlySet<Guid>> GetOrLoadAsync(Guid userId, Func<CancellationToken, Task<IReadOnlyCollection<Guid>>> loader, CancellationToken)` | Returns the user's active permission ids, running `loader` on a miss or an expired entry. Exactly one loader runs per user under concurrency. |
| `long CaptureInvalidationStamp()` | Reads the current stamp, for pairing with `Set`. Capture it **before** reading grants. |
| `void Set(Guid userId, long stamp, IEnumerable<Guid> permissionIds)` | Replaces the entry and restarts its lifetime. Skipped if any eviction happened since `stamp` was captured. |
| `void Invalidate(Guid userId)` | Drops one user's entry. No-op when nothing is cached. |
| `void Invalidate(IEnumerable<Guid> userIds)` | Drops several entries, for the cascades in §5. Duplicates are harmless. |
| `void Clear()` | Drops every entry. **No production path calls this** — it exists for the integration fixture (§8). |

`loader` may be shared with concurrent callers from other requests, so it must not capture state that cannot outlive the calling request. The cached set is a `FrozenSet<Guid>` copied on the way in, so a reader holding a reference always sees a consistent snapshot even while the entry is being replaced, and a loader that mutates its own collection afterwards cannot corrupt the cache.

Cancellation is per caller: a caller that gives up stops waiting without disturbing co-waiters on the same in-flight load. Under sustained churn — an entry repeatedly replaced or abandoned by other requests — `GetOrLoadAsync` gives up after five attempts with an `InvalidOperationException` rather than spinning.

## 8. Testing

| Suite | File | Covers |
|---|---|---|
| Cache unit tests | [`Caching/UserPermissionCacheTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Caching/UserPermissionCacheTests.cs) | single-flight loading, the empty-set hit, non-cached failures, cancellation races, expiry at and across the boundary, stamp-ordered `Set`, per-user and bulk invalidation |
| Filter unit tests | [`Filters/PermissionEndpointFilterTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Filters/PermissionEndpointFilterTests.cs) | AND semantics, duplicate ids, denial body shape, map-time argument validation, and that a cache hit never resolves the `DbContext` |

The cache takes an injected `TimeProvider`, so TTL behaviour is tested by advancing [`MutableTimeProvider`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/TestInfrastructure/MutableTimeProvider.cs) rather than by sleeping. Never assert expiry with a wall-clock delay.

**Adding integration tests?** The cache is a singleton shared across the whole `[Collection("Integration")]` run. [`IntegrationTestFixture`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/TestInfrastructure/IntegrationTestFixture.cs) therefore calls `IUserPermissionCache.Clear()` in the five helpers that write grants straight through the `DbContext` — `ResetMcpServersAndPermissionsAsync`, `AddUserPermissionAsync`, `ResetUsersAsync`, `ResetConversationsAndDocumentsAsync`, and `RevokePermissionAsync`. Those helpers bypass the services that evict, so without the clear a later test would authorize against a stale grant set. If you add a helper that writes grants outside `PermissionService` or `UserService`, clear the cache in it too.

## 9. Key files

| Concern | File |
|---|---|
| Cache interface + implementation | [`Enterprise.Gpt.Service/Caching/UserPermissionCache.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Caching/UserPermissionCache.cs) |
| Shared grant reader (the seam every caller uses) | [`Enterprise.Gpt.Service/Caching/UserGrantReader.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Caching/UserGrantReader.cs) |
| Options | [`Enterprise.Gpt.Service/Settings/UserPermissionCacheOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/UserPermissionCacheOptions.cs) |
| Endpoint filter | [`Enterprise.Gpt.Api/Filters/PermissionEndpointFilter.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Filters/PermissionEndpointFilter.cs) |
| Built-in ids + display names | [`Enterprise.Gpt.Dto/Enums/PermissionIds.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/PermissionIds.cs) |
| Registration + options validation | [`Enterprise.Gpt.Api/Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) |
| Warm-fill at sign-in | [`Enterprise.Gpt.Service/UserService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/UserService.cs) |
| Grant mutations + eviction | [`Enterprise.Gpt.Service/PermissionService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/PermissionService.cs) |
| MCP cascade eviction | [`Enterprise.Gpt.Service/McpServerService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/McpServerService.cs) |
| Problem type for the 403 | [`Enterprise.Gpt.Api/Problems/ProblemTypes.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Problems/ProblemTypes.cs) |

## 10. Known gaps and extension points

- **No cross-instance invalidation.** The headline limitation (§5.1). Fan `Invalidate(userId)` out over Redis pub/sub or Service Bus when the deployment scales past one instance and 5 minutes of skew is unacceptable.
- **No `Require`-any form.** `Require` is AND only. An OR gate would need a second factory; nothing needs one today.
- **No eviction on memory pressure.** The key space is one entry per signed-in user and entries hold a small `FrozenSet<Guid>`, so no size cap is enforced. Expired entries for users who never come back linger until the process recycles. Revisit only if the signed-in population grows by orders of magnitude.
- **`PermissionIds.Names` is maintained by hand.** It must stay in step with the seeded `Core.Permission` rows. The map-time check catches an id with no name; it cannot catch a name that has drifted from the seeded row.
- **No cache metrics.** Evictions are logged, hits and misses are not. If the hit rate ever becomes a question, add counters via `System.Diagnostics.Metrics` alongside the existing `UseOpenTelemetry()` wiring.
