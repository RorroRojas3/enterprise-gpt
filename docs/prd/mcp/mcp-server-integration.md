# PRD: MCP Server Integration Hardening

## 1. Overview

**Problem.** MCP server integration already ships. `Enterprise.Gpt.Common.Enums.McpAuthTypes` supports `None` (unauthenticated) and `EntraIdOnBehalfOf` (per-user Entra ID delegated OAuth) servers; `McpServerService` gives administrators full CRUD over the server catalog, co-creating a gating `Permission` row with every server; `McpEndpoints` exposes it all under `api/mcps`; and `McpToolProvider` resolves a user's selected servers at chat time, acquiring an on-behalf-of (OBO) token via `ITokenAcquisition` for `EntraIdOnBehalfOf` servers and pinning it onto the MCP transport. `McpClientCache` leases the resulting clients with refcounted disposal and expiry tracking. Microsoft Learn and Context7 already register and work today as `AuthType.None` servers. This document is **production hardening of that baseline**, not new capability — no reader should mistake it for a greenfield MCP integration.

Six gaps keep the existing integration from being production-ready:

1. **Consent is a dead end.** `McpAuthorizationRequiredException` carries only `ServerName`; the 403 `/problems/mcp-authorization-required` body carries only `serverName`. A user who has not consented has no way to learn what to consent to, so they can never finish, and the Conditional Access claims challenge (`MsalUiRequiredException.Claims`) is discarded outright.
2. **OBO tokens are not durable.** `AddInMemoryTokenCaches()` means every token is lost on restart and is never shared across instances.
3. **A server can request only one OAuth scope.** `McpServer.Scope` is a single `nvarchar(512)` column passed as a one-element array to `ITokenAcquisition`.
4. **Registration is blind.** Nothing validates a candidate server at save time — no reachability check, no RFC 9728 protected-resource-metadata comparison — so a broken registration surfaces as an opaque 502 the first time a user tries to use it.
5. **The bearer token is baked into the transport at client creation.** A long, tool-heavy turn can outlive the token it started with.
6. **MCP activity is unmeasured.** Connect, list-tools, and token acquisition emit no telemetry; `UseOpenTelemetry()` covers the chat client only.

**Solution.** Close all six gaps with backend-only changes across four capability epics — registration-time validation, consent completion, token durability, and operational visibility — while holding the existing constraints fixed: Entra ID only (no generic OAuth 2.1, DCR, or CIMD), per-user delegated OBO only (never a shared service identity), and remote HTTP (Streamable HTTP/SSE) transport only. Tool governance (allowlists, drift detection, invocation caps) and any frontend work are explicitly out of scope.

**Success criteria.**

- A user who has not consented to an Entra-protected server completes consent and runs a tool in the same session with no administrator involvement: `mcp.token.acquisition{outcome="consent_required"}` does not recur for the same (user, server) pair within 5 minutes of the first occurrence.
- Mid-stream MCP authorization failures: 0 over a rolling 7-day window, measured by `mcp.tool.invocation{outcome="unauthorized"}`.
- At least 95% of registrations that would fail on first use are instead rejected at save time, measured by `mcp.registration.validation{outcome="rejected"}` compared against the `/problems/mcp-server-unavailable` first-use rate.
- After a rolling restart, at least 99% of MCP tool calls in the following 5 minutes take a token from the distributed cache rather than re-challenging the user, measured by `mcp.token.acquisition{source="cache"}`.
- p95 connect + `ListToolsAsync` latency stays at or below 1500 ms, measured by `mcp.client.connect.duration`.

Today, a user who lacks consent for an Entra-protected MCP server hits a 403 with nothing to act on and has to find an administrator; after this work, the same 403 carries the exact scopes to request and any Conditional Access claims challenge, so the user's client can drive an interactive consent flow and retry inside the same turn — while an administrator, registering that server in the first place, learns about a broken scope or an unreachable endpoint before ever saving it, not from a support ticket after the first user hits it.

## 2. Goals & non-goals

**Goals.**

- Let a user who lacks consent for an Entra-protected server complete consent unaided, by carrying the missing scopes and any Conditional Access claims challenge on the existing 403 problem.
- Eliminate mid-stream MCP authorization failures by checking consent before the SSE stream's headers are written and by validating a server's reachability and scopes at registration time.
- Make OBO tokens durable across restarts and shared across API instances via a SQL Server-backed distributed token cache.
- Keep a long, tool-heavy turn's MCP calls authenticated for their full duration by refreshing the bearer token per request instead of baking it into the transport once.
- Make MCP connect, token-acquisition, and tool-invocation activity observable through the API's existing OpenTelemetry pipeline.

**Non-goals.**

- **Generic OAuth 2.1 authorization servers, Dynamic Client Registration (DCR), or Client ID Metadata Documents (CIMD).** Entra ID supports neither DCR nor CIMD — every client id is pre-registered in the tenant regardless of what this PRD does, so building support for either would have no server to talk to.
- **stdio transport.** It means arbitrary command execution on the API host and does not scale across instances; the existing integration is remote HTTP (Streamable HTTP/SSE) only, and this PRD does not change that.
- **Shared service-principal credentials.** A shared identity loses the per-user audit trail that OBO gives for free; every token acquisition stays tied to the calling user.
- **Tool governance** — allowlists, drift/rug-pull detection, invocation caps. A deliberate scope cut; tracked as a follow-on, not attempted here.
- **Any frontend work.** This is a backend-only PRD; Appendix A hands off the API contract a future UI PRD would consume.
- **Changing the `/problems/mcp-authorization-required` type URI.** Problem types are opaque identifiers clients match verbatim (`Api/Problems/ProblemTypes.cs:19-22`); every change here is an additive extension member on the existing type, never a new or renamed one.

## 3. Users & access

**Personas.**

- **End user**: holds a grant (via a runtime `Permission` row) on one or more MCP servers and selects them for a chat turn.
- **Administrator**: holds the built-in `Administrator` permission (`PermissionIds.Administrator`, `a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d`) and manages the server catalog — registration, validation, deactivation.
- **Operator**: runs the deployment; consumes the telemetry this PRD adds to diagnose MCP incidents and to decide when to roll back the distributed token cache.

**Role-based access.**

- **End user**: can list the servers they are permitted to use (`GET api/mcps`), see their own consent state, and select permitted servers for a turn. Cannot register, edit, validate, or deactivate a server.
- **Administrator**: everything an end user can do, plus full CRUD over the server catalog and the new dry-run validation and tool-surface routes — all gated by `PermissionEndpointFilter.Require(PermissionIds.Administrator)`, matching every existing mutation on `api/mcps`.

MCP access itself is **not** modeled through `PermissionIds` at all: `McpServerService.CreateMcpServerAsync` creates a same-named `Permission` row per server at runtime, deliberately absent from `PermissionIds.Names` (the frozen dictionary is scoped to ids usable at *map* time). A runtime MCP permission can therefore never appear in a `PermissionEndpointFilter.Require(...)` call — enforcement for tool access stays exactly where it is today, inside `McpToolProvider.AcquireToolsAsync`, which checks the grant against the database (deliberately not `IUserPermissionCache`, so a revoked grant takes effect on the next request rather than when a cache entry expires).

## 4. Functional requirements

| ID | Requirement | Priority | Epic(s) |
| --- | --- | --- | --- |
| FR-1 | An administrator can dry-run validate a candidate MCP server registration — reachability, RFC 9728 protected-resource-metadata comparison, tool count — without persisting anything | P0 | EP-1 |
| FR-2 | `POST`/`PUT api/mcps` run the same validation and reject a broken registration with a 400/422, with an explicit administrator override for a server legitimately unreachable from the API host at save time | P0 | EP-1 |
| FR-3 | An MCP server can be registered with more than one OAuth scope; existing single-scope rows are migrated forward unchanged | P0 | EP-1 |
| FR-4 | An MCP server URL must use `https` outside the `Development` environment | P1 | EP-1 |
| FR-5 | An administrator can view a registered server's current tool surface without leaving the admin UI's data model | P1 | EP-1 |
| FR-6 | The `/problems/mcp-authorization-required` 403 body carries `scopes` (array) and `claimsChallenge` (nullable) extensions in addition to the existing `serverName`, additive only | P0 | EP-2 |
| FR-7 | A Conditional Access claims challenge from `MsalUiRequiredException.Claims`/`MicrosoftIdentityWebChallengeUserException` reaches the caller instead of being discarded | P0 | EP-2 |
| FR-8 | The user-facing `McpDto` carries `authType` and `scopes` so a client can prompt for consent before a turn starts | P1 | EP-2 |
| FR-9 | A user can query their own per-server consent state for every permitted Entra-protected server via `GET api/mcps/authorizations` | P1 | EP-2 |
| FR-10 | A missing-consent failure is detected and returned as a clean 403 before the SSE stream's headers are written, never as a corrupted in-flight stream | P0 | EP-2 |
| FR-11 | The MCP authorization problem's status (403) and type URI never change as part of this work | P0 | EP-2 |
| FR-12 | OBO tokens survive an API restart and are shared across API instances via a SQL Server-backed distributed token cache | P0 | EP-3 |
| FR-13 | The distributed token cache's encryption keys are held in a durable, shared Data Protection key ring rather than the local-disk default | P0 | EP-3 |
| FR-14 | The MCP bearer token is attached per outgoing request rather than baked into the transport at client creation, so a token expiring mid-turn is refreshed instead of failing the call | P0 | EP-3 |
| FR-15 | The distributed token cache is behind a configuration flag with a documented rollback to the in-memory cache | P1 | EP-3 |
| FR-16 | MCP connect, list-tools, token acquisition, and lease lifetime emit spans/metrics through a dedicated `ActivitySource`/`Meter` registered with the existing OpenTelemetry pipeline | P0 | EP-4 |
| FR-17 | `mcp.client.connect.duration`, `mcp.token.acquisition{outcome,source}`, and `mcp.tool.invocation{outcome}` are emitted with exactly those names and tags | P0 | EP-4 |
| FR-18 | `mcp.registration.validation{outcome}` is emitted with exactly that name and tag | P1 | EP-4 |
| FR-19 | MCP structured logs never carry a token, tool argument, or prompt content; `ToolTrackingOptions.IncludeToolArguments` stays off | P1 | EP-4 |
| FR-20 | An administrator can see each server's last-known connect health derived from the telemetry this PRD adds | P2 | EP-4 |
| FR-21 | Every new admin-only MCP route is gated by `PermissionEndpointFilter.Require(PermissionIds.Administrator)`; every new user-facing MCP route carries no permission filter and self-filters by grant, matching `GET api/mcps` today | P0 | EP-1, EP-2 |

## 6. Technical considerations

**Integration points.** All real, verified against the working tree.

| Concern | Where |
| --- | --- |
| Tool acquisition, OBO, permission check | `Enterprise.Gpt.Service/McpToolProvider.cs` |
| Client/lease cache, expiry, invalidation | `Enterprise.Gpt.Service/Caching/McpClientCache.cs` |
| Admin CRUD, permission co-creation | `Enterprise.Gpt.Service/McpServerService.cs` |
| Routes | `Enterprise.Gpt.Api/Endpoints/McpEndpoints.cs` |
| Problem mapping | `Enterprise.Gpt.Api/ExceptionHandlers/GlobalExceptionHandler.cs` |
| Entity + EF config | `Enterprise.Gpt.Entity/McpServer.cs`, `Enterprise.Gpt.Repository/Configurations/McpServerConfiguration.cs` |
| Auth registration | `Enterprise.Gpt.Api/Program.cs:42-60` |
| Stream consumption | `Enterprise.Gpt.Service/ConversationService.cs` (`CreateChatOptionsAsync` ~L1055-1150; lease held across the stream via `await using (toolLeases)` ~L640-647) |
| Existing telemetry pattern to extend | `Enterprise.Gpt.Api/Observability/TelemetryRegistration.cs` — the only place a telemetry backend is named; adds the chat client's `ActivitySource`/`Meter` by name to `WithTracing`/`WithMetrics` |
| Cache tuning knobs that become a backstop, not the mechanism | `Enterprise.Gpt.Service/Settings/McpClientCacheOptions.cs` (`TokenExpiryBuffer`, `MinRemainingLifetimeForStream`, `MaxEntryLifetime`) |

The migration path is unblocked: `Enterprise.Gpt.Repository/Migrations/` is **not** empty — `20260811024339_InitialCreate` is already committed — so every schema change in this PRD (the multi-scope column, the distributed-cache table) is an ordinary `dotnet ef migrations add`, not the first-migration decision the rest of the codebase still flags. `EnterpriseGptDbContext.OnModelCreating` (`Enterprise.Gpt.Repository/EnterpriseGptDbContext.cs:43-57`) applies 15 `IEntityTypeConfiguration`s explicitly, one line each, with no assembly scan; a genuinely new configuration type needs an explicit line there, but the multi-scope change reuses the existing `McpServerConfiguration`.

**Data storage & privacy.** `McpServer.Scope` (`nvarchar(512)`) becomes `Scopes`, still a single column, holding a space-delimited scope list — the OAuth wire format — rather than a child table. Access-token material moves from process memory (`AddInMemoryTokenCaches()`) into a SQL Server-backed distributed cache (`AddDistributedTokenCaches()` over `AddDistributedSqlServerCache()`), encrypted at the ASP.NET Core Data Protection layer before it reaches the database. Consent state exposed via `GET api/mcps/authorizations` is derived at request time from a token-acquisition probe, never stored as a standing flag, so it cannot go stale relative to a revoked consent.

**Security.**

- Tokens are never logged, never returned in a response body, never written to a problem body. `scopes` and `claimsChallenge` are not secrets and may be returned on the 403 — they describe what consent is missing, not a credential.
- The distributed token cache is encrypted with ASP.NET Core Data Protection, whose key ring persists to the same SQL Server instance. **Neither Data Protection nor any encrypted column exists in this codebase today** (verified: no `AddDataProtection`, `AddDistributedSqlServerCache`, or `IDistributedCache` reference anywhere in `enterprise-gpt-api/`) — this is net-new infrastructure, not wiring an existing piece together.
- There is no Key Vault and no `DefaultAzureCredential` anywhere in this codebase (verified). The only credential in use for anything Entra-adjacent is the `ClientSecretCredential` built from `AzureAd:ClientId`/`AzureAd:ClientSecret`/`AzureAd:TenantId` at `Program.cs:310`, for the Graph client. Data Protection key protection therefore relies on SQL Server's own protection (e.g. TDE), not an external HSM — recorded as an open question in section 9, since deciding otherwise means introducing Key Vault as new infrastructure.
- **RFC 8707 caveat.** The MCP specification makes the `resource` parameter a client MUST when requesting a token, but Entra ID v2 validates the token's audience against the registered App ID URI, and the documented bridge is the `api://{clientId}/{scope}` (or `.default`) scope form — there is no first-class `resource` parameter on the Entra v2 token endpoint. This PRD targets Entra's scope model for token acquisition, as the user's existing constraint requires; RFC 9728 protected-resource-metadata discovery (EP-1) is used only to **verify** a registration's configured scopes against what the server advertises, never to drive token acquisition itself.

**Scalability & performance.** The per-request token-refresh handler (EP-3) relies on `ITokenAcquisition`'s own MSAL-level caching to keep repeat acquisitions cheap — the existing code comment in `McpToolProvider.AcquireLeaseAsync` already notes this for the current once-per-lease acquisition, and it holds equally for a per-request handler. The distributed cache read only happens on a local MSAL cache miss (e.g. after a restart or on another instance), not on every tool call. `McpClientCache` itself stays per-instance and in-memory — only the *token* cache backing `ITokenAcquisition` becomes distributed; the client/tool lease cache's existing `TokenExpiryBuffer`/`MinRemainingLifetimeForStream` knobs remain as a backstop against handing a stream a near-expired cached entry, not as the sole mechanism keeping a token fresh once EP-3 ships.

## 7. Epics & user stories

| ID | Epic | Goal | Priority | Estimate | Depends on |
| --- | --- | --- | --- | --- | --- |
| EP-1 | Reliable server registration | An administrator learns at save time that a server will work | P0 | L | — |
| EP-2 | Completing consent for Entra-protected servers | A user who lacks consent can finish it unaided | P0 | L | EP-1 |
| EP-3 | Durable tokens and connections | Sessions survive restart and scale-out; long turns do not die mid-stream | P0 | L | — |
| EP-4 | MCP operational visibility | The section-1 metrics get a source | P0 | M | EP-1, EP-3 |

### EP-1: Reliable server registration

#### US-101: `[enabler]` Support multiple OAuth scopes per MCP server

- **Story**: `[enabler]` Replace the single `Scope` column with a space-delimited `Scopes` column — the OAuth wire format for a scope list — so a server can be registered with more than one scope. Unblocks US-103's scope comparison, EP-2's `scopes` extension and `McpDto.scopes` field, and EP-4's per-scope acquisition telemetry.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Acceptance criteria**:
  - Given the `McpServer` entity's `Scope` column (`Enterprise.Gpt.Entity/McpServer.cs`), when the migration runs, then it is replaced by `Scopes` (`nvarchar(1024)`, space-delimited) and every existing row's single scope value is rewritten unchanged into the new column.
  - Given `McpServerConfiguration` (`Enterprise.Gpt.Repository/Configurations/McpServerConfiguration.cs`), when the model is built, then it configures the new `Scopes` property; no new `IEntityTypeConfiguration` type is introduced, so `EnterpriseGptDbContext.OnModelCreating`'s explicit configuration list is unaffected.
  - Given `CreateMcpServerActionDtoValidator`/`UpdateMcpServerActionDtoValidator` (`Enterprise.Gpt.Dto/Actions/Mcp/McpServerActions.cs`), when `AuthType` is `EntraIdOnBehalfOf`, then at least one non-empty, whitespace-delimited scope is required; when `AuthType` is `None`, then `Scopes` must be empty — mirroring the existing conditional rule on `Scope`.
  - Given `McpToolProvider.AcquireAccessTokenAsync` (`Enterprise.Gpt.Service/McpToolProvider.cs:176-195`), when it calls `ITokenAcquisition.GetAuthenticationResultForUserAsync`, then it passes every configured scope, not a one-element array built from a single value.
  - Given a server registered before the migration with one scope, when read afterward, then `McpServerDto.Scopes` contains exactly that one value.

#### US-102: Enforce HTTPS on MCP server URLs outside Development

- **Story**: As an administrator, I want the API to reject an `http://` MCP server URL outside local development so that tokens and tool traffic are never sent over plaintext.
- **Priority**: P1 · **Estimate**: S · **Depends on**: —
- **Acceptance criteria**:
  - Given `CreateMcpServerActionDtoValidator`/`UpdateMcpServerActionDtoValidator`, when the hosting environment is not `Development` and `Url` uses the `http` scheme, then validation fails naming the `Url` field.
  - Given the same request in the `Development` environment, when `Url` uses `http`, then validation succeeds exactly as it does today.
  - Given a `https` URL in any environment, when validated, then it always succeeds.
  - Given a malformed URL (already rejected today by `BeAnAbsoluteHttpUri`), when validated, then the existing "must be an absolute http or https URI" message still fires — this story only narrows which scheme passes, it does not change the existing well-formedness check.

#### US-103: Dry-run validate a candidate MCP server registration

- **Story**: As an administrator, I want to validate a server's URL, auth configuration, and advertised scopes before saving it so that I learn about a broken registration immediately rather than from a user's first failed turn.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-101
- **Acceptance criteria**:
  - Given a candidate registration payload shaped like `CreateMcpServerActionDto`, when `POST api/mcps/validate` is called, then the server is connected to, `ListToolsAsync` is called, and the response reports reachability, tool count, and — for `EntraIdOnBehalfOf` — the result of an RFC 9728 protected-resource-metadata fetch compared against the candidate's configured `Scopes`.
  - Given the candidate's protected-resource metadata advertises scopes that do not overlap the configured `Scopes`, when validated, then the report flags the mismatch; the HTTP call itself still succeeds (200) since enforcement is the save path's job, not the dry run's.
  - Given the candidate server is unreachable, when validated, then the report states unreachable with no unhandled exception — no 502 leaks out of this endpoint.
  - Given the call completes, when it returns, then nothing is persisted: no `McpServer` or `Permission` row is created, regardless of outcome.
  - Given the caller lacks `PermissionIds.Administrator`, when they call `POST api/mcps/validate`, then they receive 403 `/problems/forbidden` via `PermissionEndpointFilter.Require(PermissionIds.Administrator)`, matching every other mutation on `api/mcps`.

#### US-104: View a registered server's tool surface

- **Story**: As an administrator, I want to see the tools a registered server currently exposes so that I can confirm a change to the server did not silently add or remove capabilities.
- **Priority**: P1 · **Estimate**: S · **Depends on**: —
- **Acceptance criteria**:
  - Given an active server, when `GET api/mcps/{id}/tools` is called by an administrator, then the response lists the tool names and descriptions `ListToolsAsync` currently returns, sourced live through `McpToolProvider`/`McpClientCache` rather than a stored snapshot.
  - Given a server id that does not exist or is deactivated, when called, then the response is 404 `/problems/resource-not-found`, matching `GetMcpServerAsync`'s existing behavior.
  - Given the server is unreachable, when called, then the response is 502 `/problems/mcp-server-unavailable` carrying the `serverName` extension, matching the existing `McpServerUnavailableException` mapping.
  - Given the caller lacks `PermissionIds.Administrator`, when called, then 403 `/problems/forbidden`.

#### US-105: Reject a broken registration at save time

- **Story**: As an administrator, I want `POST`/`PUT api/mcps` to run the same checks as the dry run so that I cannot save a server that will fail on first use.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-103
- **Acceptance criteria**:
  - Given a candidate registration that fails reachability or the scope comparison, when `POST api/mcps` or `PUT api/mcps/{id}` is called without an override, then the request fails with 400 rather than succeeding and failing later inside a user's turn.
  - Given a registration that passes validation, when saved, then `McpServerService.CreateMcpServerAsync`/`UpdateMcpServerAsync` proceed exactly as today — permission co-creation, name-uniqueness check, cache invalidation on update.
  - Given a scope-mismatch-only failure (server reachable, but RFC 9728 metadata disagrees with the configured `Scopes`), when saved, then the save is rejected regardless of the US-106 override — a scope mismatch is a different risk than unreachability and is never overridable.
  - Given the request carries the US-106 override and the failure is reachability-only, then the save proceeds; this criterion exists only to prove the boundary between US-105 and US-106.

#### US-106: Override registration validation for a server unreachable from the API host

- **Story**: As an administrator, I want to save a server I know is reachable only from inside a private network the validation call cannot reach, so that a legitimate registration is not blocked by an environment limitation.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-105
- **Acceptance criteria**:
  - Given a reachability-only validation failure, when the administrator resubmits with an explicit override flag on the save request, then the server saves and the response states validation was skipped.
  - Given the override is used, when the server is later leased for a chat turn and truly is unreachable, then the existing `McpServerUnavailableException` → 502 path still applies unchanged — the override only moves when the failure can surface, never whether it can.
  - Given the override flag is omitted, when the same reachability failure occurs, then the save is rejected as in US-105.
  - Given the override is used, when the save succeeds, then it is logged at a level an operator would notice, since an unvalidated production registration is a deliberate risk acceptance.

### EP-2: Completing consent for Entra-protected servers

#### US-201: `[enabler]` Carry consent scopes and the claims challenge on the MCP authorization problem

- **Story**: `[enabler]` Add `scopes` (array) and `claimsChallenge` (nullable string) extensions beside the existing `serverName` on the `/problems/mcp-authorization-required` 403 body, so a client can request exactly what is missing instead of hitting a dead end. This supersedes the half-measure tracked as **US-411 in `docs/prd/enterprise-ui-rebuild.md:597-602`** (which added a single `scope` string) and unblocks that document's US-412, plus US-202 through US-205 here.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-101
- **Acceptance criteria**:
  - Given `McpAuthorizationRequiredException` (`Enterprise.Gpt.Service/Exceptions/McpAuthorizationRequiredException.cs`), when it is constructed, then it also carries the requested scopes and an optional claims-challenge string, not just `ServerName`.
  - Given `GlobalExceptionHandler`'s mapping at `Enterprise.Gpt.Api/ExceptionHandlers/GlobalExceptionHandler.cs:109-114`, when a `McpAuthorizationRequiredException` is caught, then the 403 body carries `serverName` (unchanged), `scopes` (array, from the server's `Scopes`), and `claimsChallenge` (present only when the identity library returned one).
  - Given a server with no claims challenge (ordinary missing consent, not a Conditional Access challenge), when the 403 is produced, then `claimsChallenge` is omitted rather than sent as `null` or empty.
  - Given the resulting response, when compared to the existing contract, then the `type` URI (`/problems/mcp-authorization-required`) and status (**403, never 401** — per the existing rationale at `GlobalExceptionHandler.cs:106-108` and `docs/conversations/streaming-contract.md:149`) are unchanged; this is additive only.

#### US-202: Carry the Conditional Access claims challenge through token acquisition

- **Story**: As a user subject to a Conditional Access policy, I want the claims challenge to reach me so that my client can satisfy it instead of retrying a token acquisition that will never succeed.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-201
- **Acceptance criteria**:
  - Given `McpToolProvider.AcquireAccessTokenAsync` (`Enterprise.Gpt.Service/McpToolProvider.cs:176-195`), when it catches a `MicrosoftIdentityWebChallengeUserException`, then the claims value carried on the exception's underlying `MsalUiRequiredException.Claims` is passed into the `McpAuthorizationRequiredException` constructor added in US-201.
  - Given it catches a bare `MsalUiRequiredException` with a non-null `Claims` property, then the same claims value is carried through identically.
  - Given a `MsalUiRequiredException`/`MicrosoftIdentityWebChallengeUserException` with no claims (ordinary consent-required, not a Conditional Access challenge), then `claimsChallenge` is `null` and the 403 omits it per US-201.
  - Given a `MsalException` that is neither of the above, then the existing mapping to `McpServerUnavailableException` (502) is unchanged — this story touches only the consent-required branch.

#### US-203: Expose auth type and scopes on the user-facing MCP listing

- **Story**: As a user, I want to know before starting a turn whether a server I can select needs consent and what it will ask for, so that I am not surprised mid-turn.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-101
- **Acceptance criteria**:
  - Given `McpDto` (`Enterprise.Gpt.Dto/McpDto.cs`) and `McpServerMapper.MapToMcpDtoExpression` (`Enterprise.Gpt.Service/Mappers/McpServerMapper.cs`), when the permitted-servers listing is built, then each entry also carries `authType` and `scopes` (empty for `None`).
  - Given `GET api/mcps` is called by any authenticated user, then the response shape changes additively — the existing `id`/`name`/`description` fields are unchanged, and no existing client integration breaks.
  - Given a server whose auth type is `EntraIdOnBehalfOf`, when listed, then `scopes` reflects the same `Scopes` value the administrative `McpServerDto` already exposes — one source of truth, two projections.

#### US-204: Report per-server consent state for the caller

- **Story**: As a user, I want to see which of my permitted servers I have already consented to before I start a turn, so that I am not blindsided by a 403 mid-conversation.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-203
- **Acceptance criteria**:
  - Given the caller's permitted servers, when `GET api/mcps/authorizations` is called, then the response reports, per `EntraIdOnBehalfOf` server, whether the caller currently holds consent — derived from a live token-acquisition probe (`ITokenAcquisition`) rather than a stored flag, since consent can be revoked outside this application.
  - Given a server with `AuthType.None`, when listed, then it is reported as always-authorized without triggering a spurious token probe.
  - Given a probe for one server fails with `MicrosoftIdentityWebChallengeUserException`/`MsalUiRequiredException`, then that server reports as requiring consent, and the failure does not fail the whole request — every other server's state still returns.
  - Given the caller holds no permission for a server, then that server is absent from the response, matching `GetPermittedMcpServersAsync`'s existing self-filtering.
  - Given the route, when mapped, then it carries `.RequireAuthorization()` only, with no `PermissionEndpointFilter` — matching `GET api/mcps`'s existing self-filter-by-grant pattern, per FR-21.

#### US-205: Pre-flight consent before the SSE stream opens

- **Story**: As a user, I want an MCP consent failure to arrive as a clean error before the response starts streaming, so that my client can show it cleanly instead of receiving a corrupted stream.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-202
- **Acceptance criteria**:
  - Given `ConversationService.CreateChatOptionsAsync` (`Enterprise.Gpt.Service/ConversationService.cs:1055-1150`) calling `_mcpToolProvider.AcquireToolsAsync` at line 1121, when a selected server requires consent, then the resulting `McpAuthorizationRequiredException` propagates out of `StreamConversationCoreAsync` before any `AssistantUiEvent` is yielded.
  - Given `ConversationEndpoints.StreamConversationAsync` (`Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs:161-189`), when that exception surfaces before the first `await foreach` iteration, then `SetStreamingHeaders` never runs, and the 403 problem+json response is written normally by the exception-handler chain — exactly the mechanism the SSE headers' lazy-set-on-first-fragment design already exists for.
  - Given a turn where a tool the model invokes mid-stream belongs to an already-leased server (not the initial lease), then no regression is introduced — this story covers only the lease acquired at turn start, since token acquisition already completes before any tool is attached to `ChatOptions`.
  - Given `GlobalExceptionHandler`'s `Response.HasStarted` short-circuit (`Enterprise.Gpt.Api/ExceptionHandlers/GlobalExceptionHandler.cs:52`), when a consent failure happens after the response has already started for an unrelated reason, then the handler still returns without corrupting the stream, exactly as today.

### EP-3: Durable tokens and connections

#### US-301: `[enabler]` Move the OBO token cache to SQL Server

- **Story**: `[enabler]` Replace `AddInMemoryTokenCaches()` (`Enterprise.Gpt.Api/Program.cs:60`) with `AddDistributedTokenCaches()` over `AddDistributedSqlServerCache()` on the existing SQL Server instance, so OBO tokens survive a restart and are shared across API instances. Unblocks US-303's per-request refresh and the section-1 restart criterion via US-402.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Acceptance criteria**:
  - Given `Program.cs:60`, when the app starts, then token acquisition is registered through `AddDistributedTokenCaches()` backed by `AddDistributedSqlServerCache()` pointed at the same connection string `EnterpriseGptDbContext` uses.
  - Given the cache table does not yet exist, when the app starts in a fresh environment, then an ordinary `dotnet ef migrations add` migration creates it — `Enterprise.Gpt.Repository/Migrations/` already holds `20260811024339_InitialCreate`, so this is a normal migration, not the first-migration decision the rest of the codebase still flags.
  - Given a user who acquired an OBO token before a rolling restart, when they call a tool again within the token's lifetime after the restart, then no new consent prompt is required — the token is read from the distributed cache.
  - Given two API instances behind the load balancer, when the same user's requests land on different instances, then both read the same cached token instead of each maintaining its own.

#### US-302: `[enabler]` Encrypt the token cache with a durable Data Protection key ring

- **Story**: `[enabler]` Persist the ASP.NET Core Data Protection key ring to SQL Server so the distributed token cache's own encryption keys survive a restart too — an ephemeral key ring would make the cache unreadable after every restart, silently defeating US-301.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-301
- **Acceptance criteria**:
  - Given the app has no Data Protection configuration today (verified: no `AddDataProtection` call anywhere in `enterprise-gpt-api/`), when this story lands, then `AddDataProtection()` persists keys to a SQL Server-backed key repository instead of the default local-disk key ring, which does not survive a container restart or scale-out.
  - Given no Key Vault exists in this deployment (verified: no `DefaultAzureCredential`/Key Vault reference anywhere in `Program.cs`), then key protection at rest relies on SQL Server's own protection rather than an external HSM — recorded as an assumption in section 9, not silently decided.
  - Given a restart, when a request needing a cached token arrives, then the token cache decrypts successfully using the persisted key ring — this is the acceptance test that actually proves US-301's restart guarantee end to end, not just that the table exists.
  - Given two API instances, when both start, then they read the same key ring row rather than each generating a divergent key set that could not decrypt entries the other wrote.

#### US-303: Refresh the MCP bearer token per request instead of at client creation

- **Story**: As a user running a long, tool-heavy turn, I want my MCP server token refreshed automatically if it is about to expire, so that the turn does not fail partway through.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-301
- **Acceptance criteria**:
  - Given `McpToolProvider.CreateEntrySourceAsync` (`Enterprise.Gpt.Service/McpToolProvider.cs:197-268`), when the transport is built, then the static `AdditionalHeaders["Authorization"]` assignment at lines 207-213 is replaced by a `DelegatingHandler` registered on the `"Mcp"` named `HttpClient` (`McpToolProvider.HttpClientName`) that attaches a token to each outgoing request.
  - Given the singleton `IHttpClientFactory` pipeline, when the handler needs a token, then it acquires one using only the identity captured at lease time (the user's object id and the server's scopes) and never resolves a scoped service — preserving the invariant `McpToolProvider.AcquireLeaseAsync`'s existing comment already states for the singleton cache.
  - Given a token that is still valid, when a mid-turn request is sent, then the handler reuses `ITokenAcquisition`'s own cache (itself now backed by the distributed cache from US-301) rather than forcing a network round trip on every call.
  - Given a token that expires between the start of a long turn and a later tool call, when that later call is sent, then the handler transparently acquires a fresh token and the call succeeds instead of failing with an authentication error.
  - Given this change, when `McpClientCacheOptions.TokenExpiryBuffer`/`MinRemainingLifetimeForStream` (`Enterprise.Gpt.Service/Settings/McpClientCacheOptions.cs`) are considered, then they remain a backstop against handing a stream a near-expired cached client entry, not the sole mechanism keeping a token fresh.

#### US-304: Feature-flag the distributed token cache with a rollback to in-memory

- **Story**: As an operator, I want a configuration switch to revert to the in-memory token cache so that I can back out the distributed-cache change without a deployment if it misbehaves.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-301
- **Acceptance criteria**:
  - Given a new configuration flag defaulting to enabled, when it is set to disabled, then `Program.cs` registers `AddInMemoryTokenCaches()` instead of `AddDistributedTokenCaches()`, exactly as it does before this epic.
  - Given the flag is toggled, when the app restarts, then no code change or redeploy is required — configuration alone decides the cache backing.
  - Given the flag defaults to enabled, when a fresh environment starts with no explicit override, then it gets the distributed cache — the safer default for a multi-instance deployment.
  - Given the rollback path is exercised after tokens were cached in SQL Server, when the app falls back to in-memory, then no error occurs — it simply stops reading and writing the SQL-backed store, and every user re-authenticates on the next OBO call exactly as they would after any restart today.

### EP-4: MCP operational visibility

#### US-401: `[enabler]` Register an MCP ActivitySource and Meter

- **Story**: `[enabler]` Add a dedicated `ActivitySource`/`Meter` pair for MCP, following the naming and registration pattern `TelemetryRegistration.ChatTelemetrySourceName` already establishes for the chat client, and wire it into the existing `AddEnterpriseTelemetry` registration — scoped to what Microsoft.Extensions.AI's `UseOpenTelemetry()` does not already cover (connect, list-tools, token acquisition, lease lifetime; **not** tool invocation itself, which already emits `gen_ai.*` spans through the chat client). Unblocks US-402 through US-404.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Acceptance criteria**:
  - Given `Enterprise.Gpt.Api/Observability/TelemetryRegistration.cs`, when a new source-name constant is added alongside `ChatTelemetrySourceName`, then `AddEnterpriseTelemetry`'s `WithTracing`/`WithMetrics` calls add it, following that file's existing one-line-per-source pattern.
  - Given the new `ActivitySource`/`Meter` instances, when constructed, then they live in `Enterprise.Gpt.Service` (where `McpToolProvider` and `McpClientCache` live), never in `Api` — the layering rule is `Service` does not depend on `Api`.
  - Given no Application Insights connection string is configured (the existing skip condition in `AddEnterpriseTelemetry`), when the app starts, then the source and meter are still created — so tests can assert against them directly — but nothing is exported, matching the existing chat-telemetry behavior.
  - Given this story alone, when it ships, then no span or metric is emitted yet — that is US-402's job; this story registers only the plumbing.

#### US-402: Emit MCP connect, tool-invocation, and token-acquisition metrics

- **Story**: As an operator, I want `mcp.client.connect.duration`, `mcp.tool.invocation`, and `mcp.token.acquisition` so that four of the five section-1 success criteria have a real data source.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-401
- **Acceptance criteria**:
  - Given `McpToolProvider.CreateEntrySourceAsync` (`Enterprise.Gpt.Service/McpToolProvider.cs:197-268`), when a connect-plus-`ListToolsAsync` attempt completes, successfully or not, then `mcp.client.connect.duration` records the elapsed time as a histogram.
  - Given `McpToolProvider.AcquireAccessTokenAsync` (`Enterprise.Gpt.Service/McpToolProvider.cs:176-195`), when it returns, then `mcp.token.acquisition` increments with `outcome` = `success`, `consent_required` (on `McpAuthorizationRequiredException`), or `failed` (on `McpServerUnavailableException`), and `source` = `cache` or `network`.
  - Given a tool invocation fails with an MCP-side authorization error surfaced mid-stream — the case US-205 is meant to prevent at turn start, but which can still occur if consent is revoked mid-turn — then `mcp.tool.invocation` increments with `outcome="unauthorized"`, the direct source for the section-1 "0 mid-stream authorization failures" criterion.
  - Given `mcp.token.acquisition{source="cache"}` specifically, when US-301's distributed cache is active after a restart, then this tag distinguishes a cache hit from a fresh network acquisition — the direct source for the section-1 "≥99% from cache after restart" criterion.
  - Given no PII, token value, or tool argument is ever available to these instruments, then none is recorded as a tag or attribute — only ids, names already safe to log (server name, outcome, source), and durations.

#### US-403: Emit registration-validation metrics

- **Story**: As an operator, I want `mcp.registration.validation` so that I can measure how much first-use failure EP-1's validation actually prevents.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-401, US-103
- **Acceptance criteria**:
  - Given `POST api/mcps/validate` (US-103) or a validated save (US-105), when validation completes, then `mcp.registration.validation` increments with `outcome` = `accepted` or `rejected`.
  - Given a save that used the US-106 override, when it completes, then the metric still records `outcome="rejected"` for the failed validation, distinct from the save itself succeeding — the metric measures validation outcomes, not save outcomes.
  - Given the section-1 criterion comparing this metric against the `/problems/mcp-server-unavailable` first-use rate, when both series are read, then they share a comparable time axis (UTC, consistent resolution) so the ratio in the criterion is directly computable.

#### US-404: Structured MCP logging with no sensitive content

- **Story**: As an operator, I want MCP connect, consent, and invocation activity logged with structured, non-sensitive fields so that I can debug an incident without a compliance risk.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-401
- **Acceptance criteria**:
  - Given `McpToolProvider` and `McpClientCache` already log structured messages (e.g. `_logger.LogInformation("Connected to MCP server {McpServerId}...")`), when this epic adds new log lines, then they follow the same structured-template convention and never include a token, a tool argument, or prompt content.
  - Given `ToolTrackingOptions.IncludeToolArguments` (default `false`), when this story ships, then it is left unchanged and no MCP-specific logging bypasses it.
  - Given a consent-required event, when logged, then the log records the server name and user id (already logged elsewhere in this service) but never the claims-challenge string or any token material.
  - Given a reviewer inspects the diff, when checking for secrets, then no log statement interpolates an access token, an `Authorization` header value, or similar.

#### US-405: Admin-visible last-known health per server

- **Story**: As an administrator, I want to see each server's last-known connect health so that I can tell a failing server from a healthy one without reading logs.
- **Priority**: P2 · **Estimate**: M · **Depends on**: US-402
- **Acceptance criteria**:
  - Given the metrics from US-402, when an administrator requests server health, then each server shows its most recent connect outcome and timestamp, derived from the same telemetry rather than a new polling loop against the server.
  - Given a server with no recent connect attempt (nobody has used it since the last restart), then its health shows as unknown rather than a false "healthy".
  - Given the underlying telemetry exporter is unconfigured (no Application Insights connection string, matching the existing local/test skip condition), when this story is exercised locally, then health still reflects an in-process rolling summary, so the feature is testable without a telemetry backend.

## 8. Milestones & rollout

**Phases**, derived from the epic dependency graph.

| Phase | Contents | Relative estimate |
| --- | --- | --- |
| **P1 — Registration and token foundation** | EP-1 in full (US-101 … US-106) and EP-3 in full (US-301 … US-304), run as two independent tracks — neither epic depends on the other. EP-4's US-401 (telemetry plumbing) has no dependency either and can start alongside them | ~3 weeks |
| **P2 — Consent completion** | EP-2 in full (US-201 … US-205). Depends only on EP-1's US-101 (the multi-scope schema), not on the rest of EP-1, so it can start as soon as that one story lands | ~1.5 weeks |
| **P3 — Observability** | EP-4's remainder (US-402 … US-405). US-402 needs US-401 (P1) and benefits from US-301 (P1) being live to observe a real restart; US-403 needs US-103 (P1) | ~1.5 weeks |

**Risks & mitigations.**

| Risk | Mitigation |
| --- | --- |
| The SQL Server-backed distributed token cache adds latency or load to every OBO acquisition | `ITokenAcquisition`'s own in-process MSAL cache still serves repeat acquisitions within a process; the SQL round trip only happens on a cross-instance or post-restart miss. US-304's feature flag is the explicit back-out if load proves unacceptable |
| Not every MCP server implements RFC 9728 protected-resource-metadata discovery | US-103's validation treats a missing metadata document as "unknown" rather than "rejected" for the scope comparison — only reachability is a hard requirement; a scope mismatch is reported but does not itself block the dry run |
| Data Protection keys have no Key Vault or HSM to protect them at rest, since neither exists in this codebase | Documented as an assumption and an open question in section 9 rather than silently decided; SQL Server's own protection (e.g. TDE) is the baseline this PRD assumes |
| The per-request `DelegatingHandler` in US-303 adds latency to every MCP tool call | `McpToolProvider`'s existing code comment already establishes that MSAL's own cache makes repeat acquisitions cheap; this holds for a per-request handler exactly as it does for the current once-per-lease acquisition |
| Rewriting every existing `McpServer.Scope` value into `Scopes` touches production data | The migration preserves each row's single existing value verbatim; no server needs to be re-registered, and `McpServerDto`/`CreateMcpServerActionDto` validators are updated in the same story (US-101) so no window exists where the API and the schema disagree |

**Rollout & rollback.** EP-3's distributed token cache is the highest-risk change in this PRD — it changes token behavior for every user — and ships behind the configuration flag from US-304, defaulting to enabled but reversible without a redeploy. EP-1 and EP-2 are additive API surface (new routes, new problem-body extensions, a stricter save-time check) with no removed field or changed status code, so their rollback is a standard code revert. EP-4 is additive telemetry only and carries no behavioral risk to roll back. `Database.Migrate()` still runs at startup (skipped in the `Testing` environment) and this PRD does not change that deployment model — the multi-scope column and the distributed-cache table both ride the same migration path every other schema change already uses.

## 9. Assumptions & open questions

**Assumptions.** Each is a guess a reviewer can veto.

- `Scopes` stays a single space-delimited column rather than moving to a child table. This matches the OAuth wire format for a scope list and avoids a join for a value that is always read whole — never queried by individual scope.
- Renaming `Scope` to `Scopes` on the *administrative* `McpServerDto` and on `CreateMcpServerActionDto`/`UpdateMcpServerActionDto` (US-101) is a deliberate breaking change to that contract, not an additive one. It is taken knowingly because the admin surface has no client yet — `docs/prd/enterprise-ui-rebuild.md`'s US-1208 (admin MCP CRUD) is unstarted — and the environment is pre-production. The *user-facing* `McpDto` change in US-203 remains strictly additive, because that listing does have a live consumer.
- The API's own Entra app registration already holds (or can incrementally acquire) delegated permission to every downstream MCP server's scopes; this PRD does not itself grant consent on the API's app registration, only surfaces the *user's* consent gap.
- `AzureAd` configuration values (`Instance`, `TenantId`, `ClientId`, `ClientSecret`) live in user secrets locally and equivalent secret storage in deployed environments; there is no `AzureAd` section in `appsettings.json` today, and this PRD adds no new secret to that file.
- `McpClientCache` (the client/tool lease cache) stays per-instance and in-memory exactly as it is today — only the *token* cache backing `ITokenAcquisition` becomes distributed. Conflating the two would be a much larger change than this PRD's scope.
- Data Protection keys land in SQL Server, not Key Vault, because no Key Vault exists anywhere in this codebase; introducing Key Vault is a separate infrastructure decision this PRD does not make.
- RFC 9728 discovery in EP-1 is read-only verification against the server's advertised metadata at registration time; it never substitutes for or drives the actual Entra `api://{clientId}/{scope}` token acquisition, per the RFC 8707 caveat in section 6.
- The consent-state probe behind `GET api/mcps/authorizations` (US-204) is assumed cheap enough to run synchronously per request across a user's permitted servers, since it reuses `ITokenAcquisition`'s own cache; if a user holds many Entra-protected servers this may need to become asynchronous or cached with a short TTL — flagged as an open question below.

**Open questions.**

- **Does SQL Server TDE need to be explicitly enabled for the Data Protection key ring and the distributed token cache to meet the intent behind "encrypted at rest"?** This PRD assumes the existing SQL Server instance's own protection is the baseline and does not mandate TDE specifically — *operator, before US-302 ships to a production-like environment*.
- **What is the acceptable latency budget for `GET api/mcps/authorizations` when a user holds many Entra-protected servers?** The per-server token-acquisition probe is synchronous per this draft; if it proves slow, it may need caching with a short TTL or a bounded parallel fan-out — *backend engineer, during US-204's implementation*.
- **Should the US-106 validation override require a second administrator's approval, or is a single administrator's acknowledgment sufficient?** This PRD treats it as a single-administrator decision, logged, with no additional approval gate — *product owner*.
- **What "reasonable" scope mismatch tolerance, if any, should US-103's RFC 9728 comparison apply** (e.g. a server advertising a superset of the configured scopes) **before flagging a mismatch?** This draft treats any non-overlap as a flag; a partial-superset case may deserve different handling — *backend engineer, before US-103 is estimated in detail*.
- **Does the `mcp.tool.invocation{outcome="unauthorized"}` metric need to distinguish "consent revoked mid-turn" from other authorization failure shapes an MCP server itself might return** (e.g. an application-level 403 from the tool, unrelated to Entra)? This draft treats every unauthorized outcome the same — *backend engineer, during US-402's implementation*.

## Appendix A: Input for a future UI PRD

The frontend is mid-rebuild; this appendix names capability and API contract only, not any Angular component, store, or file path under `enterprise-gpt-ui/`.

- **Admin capability**: register or edit a server with a URL, an auth type, and a *list* of scopes (not a single scope); run the dry-run validation and read its reachability/scope-comparison/tool-count report; view the server's currently exposed tool surface; see its last-known connect health.
- **End-user capability**: read consent state per permitted server before starting a turn; when a turn returns 403 `/problems/mcp-authorization-required`, act on it by requesting exactly the `scopes` it names, passing any `claimsChallenge` through to the identity library, then retrying — this is the one failure that must never be treated as a token-refresh trigger.
- **New/changed contract surface a UI PRD would consume**:
  - `POST api/mcps/validate` — admin dry-run registration validation.
  - `GET api/mcps/{id}/tools` — admin tool-surface view.
  - `GET api/mcps/authorizations` — per-user, per-server consent state.
  - `authType` and `scopes` added to `McpDto` (the user-facing listing).
  - `scopes` and `claimsChallenge` extensions added to the existing `/problems/mcp-authorization-required` body, alongside `serverName`.
- This backend work **supersedes** `docs/prd/enterprise-ui-rebuild.md`'s US-411 and **unblocks** its US-412. No new problem type is introduced anywhere in this PRD, so a future UI PRD's error handling needs no new discriminated arm — only new optional fields on an arm that already exists.
