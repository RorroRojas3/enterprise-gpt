# Backend Structure

The .NET 10 solution at `enterprise-gpt-api/Enterprise.Gpt.slnx`: six projects plus two test projects
under `test/`, eight minimal-API endpoint modules, and a `Program.cs` that is composition only.
`global.json` pins the SDK and selects the Microsoft Testing Platform runner; `Directory.Packages.props`
holds every package version; `Directory.Build.props` turns warnings into errors, so the build is also
the style gate (see [../development/testing-and-ci.md](../development/testing-and-ci.md)).

## Project graph

```
Api -> Service -> Repository -> Entity -> Common
        Service -> Dto -> Common
```

| Project | Holds |
| --- | --- |
| `Enterprise.Gpt.Common` | Shared vocabulary: enums under `Enums/`, constant catalogs under `Constants/` (`PermissionIds`, `ChatClientKeys`, `Providers`, `ProblemCodes`, `TelemetryNames`, and a `<Entity>Limits` per entity), and the FluentValidation options helper under `Validation/`. |
| `Enterprise.Gpt.Dto` | Request DTOs under `Actions/<Feature>/`, response DTOs under `<Feature>/`. Its only package is FluentValidation; validators sit beside the actions they validate. |
| `Enterprise.Gpt.Entity` | EF entities by feature, the generic bases under `Base/`, and the Cosmos transcript document shapes under `Transcripts/`. |
| `Enterprise.Gpt.Repository` | Persistence by provider: `Sql/` (`EnterpriseGptDbContext`, 26 entity configurations over ten abstract bases, the interceptors, the migrations), `Cosmos/` (the transcript store) and `Blob/` (the document store). |
| `Enterprise.Gpt.Service` | All business logic, by feature: `Conversations/`, `Documents/`, `Agents/`, `Exports/`, `McpServers/`, `ChatModels/`, `Permissions/`, `Projects/`, `Users/`, `Security/`, `Options/` and the rest. |
| `Enterprise.Gpt.Api` | Hosting, endpoints, filters, middleware, problem details, startup, and per-feature registration. |

`Entity` and `Repository` never reference `Dto`. There is no `Abstractions` project — an interface
with one implementation lives in the same file as it (`IModelService` and `ModelService` share
`Service/ChatModels/ModelService.cs`). `ChatModels/` is the model-catalog feature, named so it never
clashes with the `Models/` subfolders that hold a feature's value types.

`Api`, `Service` and `Repository` declare `InternalsVisibleTo("Enterprise.Gpt.Unit.Test")`, which is
how unit tests reach the internal endpoint handlers, interceptors and the Cosmos gateway directly.

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

`Api/Filters/` holds two **static factories** and one generic `IEndpointFilter`.

- `PermissionEndpointFilter.Require(params Guid[])` is **AND** — the caller must hold every listed
  permission. It validates its ids against `PermissionIds.Names` at *map* time, so a bad id fails
  application start rather than emitting a nameless 403 months later.
- `MaxUploadSizeEndpointFilter.Require(long)` emits the `upload-too-large` problem.
- `ValidationEndpointFilter<T>` validates the request body with the registered `IValidator<T>` before
  the handler runs and answers a failure with a 400 validation problem. Attach it with
  `.AddEndpointFilter<ValidationEndpointFilter<TDto>>()`, after any permission filter, so a caller
  without the grant learns nothing about the body rules.

Services do not take `IValidator<T>` for request bodies. A rule that needs the database stays in the
service and throws `ValidationException`. The one exception is `FileDtoValidator`
(`Service/Documents/`), which `DocumentService` runs itself because it needs `DocumentOptions` and
the extractor factory. DTO validators read their limits from
`Common/Constants/<Feature>/<Entity>Limits.cs`.

Administrators are deliberately **not** implicitly granted every permission: an admin lacking
`Upload File` is refused by the upload route like anyone else.

## Errors

Every failure is RFC 9457 Problem Details (`application/problem+json`), written through
`IProblemDetailsService` by the single `GlobalExceptionHandler` (`Api/ExceptionHandlers/`), by
`ValidationEndpointFilter<T>` and the permission and upload filters, and by `app.UseStatusCodePages()`
— so bare 401s and unrouted 404s and 405s carry a body too.

`Problems/ProblemDetailsRegistration.cs` stamps `traceId` (`Activity.Current?.Id`, else
`HttpContext.TraceIdentifier`) and `instance` on every problem, in one place.

Domain exceptions derive from `ProblemException` (`Service/Exceptions/`), which carries the status
code, the problem code and any extension members. A new failure gets a subclass, never a new arm in
the handler.

| Exception | Status |
| --- | --- |
| `ValidationException` (FluentValidation) | 400, as `HttpValidationProblemDetails` keyed by failing **property name** |
| `BadHttpRequestException` | its own status (413 for an oversized chunked upload) |
| `NotFoundException` | 404 |
| `ForbiddenException` | 403 |
| `ConflictException`, including `ConversationBusyException`, `SummarizerProtectedException` and a lost row-version race | 409 |
| `McpCredentialRequiredException`, `McpCredentialRejectedException` | 428 |
| `McpServerUnavailableException` | 502 |
| `ProviderNotConfigured`, `SummarizerNotConfigured`, `StorageNotConfigured`, `ExportRendererNotConfigured` exceptions | 503 |
| `OperationCanceledException` after the client disconnected | 499, no body |
| anything else, including a timeout inside the request and `ArgumentException`, `InvalidOperationException` and `KeyNotFoundException` | 500, message suppressed, logged |

`McpServerUnavailableException` also covers a token acquisition that needs user interaction: these
servers are consented tenant-wide, so that state is a broken registration, not something the user
can act on.

Only 5xx responses are logged as errors; a 4xx is the caller's problem.

Domain `type` URIs are built by `Api/Problems/Constants/ProblemTypes.cs` as `/problems/{code}` from
the codes in `Common/Constants/ProblemCodes.cs`: `validation-error`, `upload-too-large`,
`resource-not-found`, `forbidden`, `permission-required`, `conversation-busy`,
`mcp-server-unavailable`, `mcp-credential-required`, `mcp-credential-rejected`,
`provider-not-configured`, `storage-not-configured`, `export-renderer-not-configured`.
They are **opaque identifiers clients match verbatim, not links** — changing one is a breaking API
change. A problem with no domain type carries the RFC 9110 status-section link ASP.NET Core
supplies.

The handler short-circuits on `Response.HasStarted`, so a faulting SSE stream is not corrupted
mid-flight.

New endpoints declare errors with `.ProducesProblem(status)` and `.ProducesValidationProblem()` —
never `.Produces<T>(status)` — and every route group carries a group-level `.ProducesProblem(401)`.

## Program.cs

`Program.cs` is composition only: a chain of `Add*` calls, then the pipeline and the endpoint maps.
Each feature registers through `Api/Configuration/<Feature>Configuration.cs`; persistence registers
through `Repository/{Sql,Cosmos,Blob}/*PersistenceConfiguration.cs`. Registration order that matters:

1. `AddEnterpriseGptKeyVault()` is the first line, so configuration rebuilds or fails before anything
   reads a secret.
2. Authentication (JWT bearer via `AddMicrosoftIdentityWebApi` bound to `AzureAd`, with on-behalf-of
   token acquisition enabled), then SQL persistence.
3. `AddEnterpriseGptDataProtection(keyVault)`, after the `DbContext` it persists its key ring in and
   the vault whose key wraps it.
4. `IChatProgressObserver` to `ChatUsageObserver` **before** the chat clients, because
   `UseToolTracking()` collects observers when a client is built.
5. The keyed chat clients, then `IChatClientResolver`.
6. The feature registrations. Options bind with
   `AddOptions<T>().Bind(...).ValidateWithFluentValidation().ValidateOnStart()` and an
   `<Area>OptionsValidator`, so a bad value fails the host rather than the first request.
7. Validators, problem details, request logging, telemetry, health checks.

After `Build()`, `RunStartupBootstrappersAsync()` (`Api/Startup/StartupBootstrapper.cs`) provisions in
a fixed order: the SQL schema (`Database.Migrate()`), Cosmos, then the summarizer and File Agent
checks (`SummarizerBootstrapper`, `FileAgentBootstrapper`). It is skipped in the `Testing`
environment. Each failure is logged before it is rethrown, so the message names the setting to fix.

Middleware pipeline order:

```
MapOpenApi + Scalar (Development only)
UseHttpsRedirection -> UseEnterpriseGptCors -> UseEnterpriseGptRequestLogging -> UseExceptionHandler
-> UseStatusCodePages -> UseAuthentication -> UseAuthorization -> endpoints
```

`UseEnterpriseGptRequestLogging` sits **outside** the exception handler and ahead of authentication: it must run
first so it can `EnableBuffering`, and it records the caller as anonymous on the way in.
`UseStatusCodePages` must stay *inside* the exception handler.

## Mapping

Hand-written static `<Entity>Mapper` classes beside their feature service (`ModelMapper` in
`Service/ChatModels/`). Each exposes both a materialized `MapToXDto` and an
`Expression<Func<X, XDto>>` so a query can project server-side. There is no AutoMapper.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs` | Registration and pipeline, top to bottom |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Configuration/` | One `<Feature>Configuration` per feature, and the provider registrations |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Startup/StartupBootstrapper.cs` | Schema, Cosmos, summarizer and File Agent provisioning |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ModelEndpoints.cs` | The template for a new endpoint module |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Filters/` | Permission, upload-size and request-validation filters |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Problems/Constants/ProblemTypes.cs` | The domain problem-type catalog |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Problems/ProblemDetailsRegistration.cs` | `traceId` and `instance` stamping |
| `enterprise-gpt-api/Enterprise.Gpt.Api/ExceptionHandlers/GlobalExceptionHandler.cs` | The one exception-to-problem mapping |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Exceptions/ProblemException.cs` | Base of every domain exception |
| `enterprise-gpt-api/Enterprise.Gpt.Service/ChatModels/ModelMapper.cs` | The mapper shape: materialized and projectable |
| `enterprise-gpt-api/test/Enterprise.Gpt.Unit.Test/Api/Endpoints/` | Handler tests calling the internal methods directly |

## Related

- [overview.md](overview.md)
- [data-model.md](data-model.md)
- [auth-and-permissions.md](auth-and-permissions.md)
