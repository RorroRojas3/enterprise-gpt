# Backend Structure

The .NET 10 solution at `enterprise-gpt-api/Enterprise.Gpt.sln`: six projects, eight minimal-API
endpoint modules, and one `Program.cs` that wires everything.

## Project graph

```
Common <- Dto <- Entity <- Repository <- Service <- Api
```

| Project | Holds |
| --- | --- |
| `Enterprise.Gpt.Common` | Shared vocabulary with no dependencies: role/kind/status enums, `TelemetryNames`. |
| `Enterprise.Gpt.Dto` | Request DTOs under `Actions/`, response DTOs at the root, and id catalogs under `Enums/` (`PermissionIds`, `ChatClientKeys`, `Providers`). Its only package is FluentValidation; validators sit beside the actions they validate. |
| `Enterprise.Gpt.Entity` | EF entities plus the Cosmos transcript document shapes under `Transcripts/`. |
| `Enterprise.Gpt.Repository` | `EnterpriseGptDbContext`, 26 entity configurations, 20 migrations. |
| `Enterprise.Gpt.Service` | All business logic and every subsystem. |
| `Enterprise.Gpt.Api` | Hosting, endpoints, filters, middleware, problem details, provider registration. |

Two edges invert the usual expectation: **`Entity` references `Dto`** and **`Repository` references
`Dto`**, so `Dto` sits *below* `Entity`. There is no `Abstractions` project — an interface lives in
the same file as its implementation (`IModelService` and `ModelService` share
`Service/ModelService.cs`).

`Api` and `Service` both declare `InternalsVisibleTo("Enterprise.Gpt.Unit.Test")`, which is how unit
tests call the internal endpoint handlers directly.

## Endpoints

Eight modules in `Enterprise.Gpt.Api/Endpoints/`, each a static class exposing
`Map*Endpoints(this IEndpointRouteBuilder)`. There are **no controllers and no `Controllers/`
directory**; `Program.cs` never calls `MapControllers()`. `ModelEndpoints.cs` is the template for
new work.

| Module | Group | Notes |
| --- | --- | --- |
| `ConversationEndpoints` | `api/conversations` | Search, CRUD, messages, documents, export, favourite, feedback, bulk delete, and `POST {id}/stream` — the SSE turn. |
| `DocumentEndpoints` | `api/documents` | Upload into a conversation or a project, list, download, job status, supported extensions. |
| `ModelEndpoints` | `api/models` | Catalog read for everyone; create, update and deactivate for administrators. |
| `ProjectEndpoints` | `api/projects` | Project CRUD, favourite, project documents. |
| `McpEndpoints` | `api/mcps` | Permitted servers for the caller; the admin registry; per-user credential save and delete. |
| `PermissionEndpoints` | `api/permissions` and `api/users/{userId}/permissions` | Two groups in one module; all admin. |
| `UserEndpoints` | `api/users` | `POST me` is sign-in and primes the permission cache; the rest is admin. |
| `ReportEndpoints` | `api/reports` | `GET usage`, admin only. |

Every group calls `.RequireAuthorization()`. Health routes are mapped separately in
`Api/Health/HealthRegistration.cs` and are anonymous: `GET|HEAD /health` and `/health/ready`.

Conversation routes carry **no permission filter** — ownership is enforced in the service, and
another user's conversation is a 404 rather than a 403.

## Endpoint filters

`Api/Filters/` holds two **static factories**, not `IEndpointFilter` implementations; no
`IEndpointFilter` type remains in the project.

- `PermissionEndpointFilter.Require(params Guid[])` is **AND** — the caller must hold every listed
  permission. It validates its ids against `PermissionIds.Names` at *map* time, so a bad id fails
  application start rather than emitting a nameless 403 months later.
- `MaxUploadSizeEndpointFilter.Require(long)` emits the `upload-too-large` problem.

Administrators are deliberately **not** implicitly granted every permission: an admin lacking
`Upload File` is refused by the upload route like anyone else.

## Errors

Every failure is RFC 9457 Problem Details (`application/problem+json`), written through
`IProblemDetailsService` by three chained `IExceptionHandler`s, by the endpoint filters, and by
`app.UseStatusCodePages()` — so bare 401s and unrouted 404s and 405s carry a body too.

`Problems/ProblemDetailsRegistration.cs` stamps `traceId` (`Activity.Current?.Id`, else
`HttpContext.TraceIdentifier`) and `instance` on every problem, in one place.

| Exception | Status |
| --- | --- |
| `ValidationException` | 400, as `HttpValidationProblemDetails` keyed by failing property name |
| `ForbiddenException` | 403 |
| `NotFoundException`, `KeyNotFoundException` | 404 |
| `ConversationBusyException` | 409 |
| `OperationCanceledException` | 499, no body |
| `McpServerUnavailableException` | 502 |
| anything else | 500, message suppressed |

Domain `type` URIs live in `Api/Problems/ProblemTypes.cs` under the relative base `/problems/`:
`validation-error`, `upload-too-large`, `resource-not-found`, `forbidden`, `permission-required`,
`conversation-busy`, `mcp-server-unavailable`, `provider-not-configured`, `storage-not-configured`.
They are **opaque identifiers clients match verbatim, not links** — changing one is a breaking API
change. A problem with no domain type carries the RFC 9110 status-section link ASP.NET Core
supplies.

Handlers short-circuit on `Response.HasStarted`, so a faulting SSE stream is not corrupted
mid-flight.

New endpoints declare errors with `.ProducesProblem(status)` and `.ProducesValidationProblem()` —
never `.Produces<T>(status)` — and every route group carries a group-level `.ProducesProblem(401)`.

## Program.cs

Registration order that matters:

1. `AddEnterpriseKeyVault()` is the first line, so configuration rebuilds or fails before anything
   reads a secret.
2. JWT bearer via `AddMicrosoftIdentityWebApi` bound to `AzureAd`, with on-behalf-of token
   acquisition enabled.
3. `AddDbContext<EnterpriseGptDbContext>` on SQL Server.
4. `AddEnterpriseDataProtection(keyVault)`.
5. `IChatProgressObserver` to `ChatUsageObserver` **before** the chat clients, because
   `UseToolTracking()` collects observers when the pipeline is built.
6. The keyed chat clients, then `IChatClientResolver`.
7. Options blocks, nearly all `ValidateOnStart()` — a bad value fails the host rather than the first
   request. This is the dominant configuration convention in the codebase.
8. Background jobs, Cosmos, MCP, the permission cache, tokenization, export renderers.
9. Exception handlers, problem details, request logging, telemetry, health checks.

After `Build()`, and skipped in the `Testing` environment: `Database.Migrate()`, then the three
`Startup/` bootstrappers (`CosmosBootstrapper`, `SummarizerBootstrapper`, `FileAgentBootstrapper`).

Middleware pipeline order:

```
MapOpenApi + Scalar (Development only)
UseHttpsRedirection -> UseCors -> UseRequestLogging -> UseExceptionHandler
-> UseStatusCodePages -> UseAuthentication -> UseAuthorization -> endpoints
```

`UseRequestLogging` sits **outside** the exception handler and ahead of authentication: it must run
first so it can `EnableBuffering`, and it records the caller as anonymous on the way in.
`UseStatusCodePages` must stay *inside* the exception handler.

## Mapping

Hand-written static extension classes in `Service/Mappers/`. Each exposes both a materialized
`MapToXDto` and an `Expression<Func<X, XDto>>` so a query can project server-side. There is no
AutoMapper.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs` | Registration and pipeline, top to bottom |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ModelEndpoints.cs` | The template for a new endpoint module |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Filters/PermissionEndpointFilter.cs` | Route authorization |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Problems/ProblemTypes.cs` | The domain problem-type catalog |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Problems/ProblemDetailsRegistration.cs` | `traceId` and `instance` stamping |
| `enterprise-gpt-api/Enterprise.Gpt.Api/ExceptionHandlers/` | The three chained handlers |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Mappers/` | Entity to DTO, materialized and projectable |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Endpoints/` | Handler tests calling the internal methods directly |

## Related

- [overview.md](overview.md)
- [data-model.md](data-model.md)
- [auth-and-permissions.md](auth-and-permissions.md)
