# Model Management

End-to-end reference for the model catalog feature: how models are listed, created, updated, and soft-deleted, and how administrator-only access is enforced. Audience: engineers onboarding to or extending the feature.

## 1. Overview

Models are the LLM deployments users can chat with (e.g. an Azure OpenAI deployment). Each model belongs to a [`Provider`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/Provider.cs) and carries routing/limit metadata (`ContextWindowSize`, `MaxOutputTokens`, `IsToolEnabled`) plus a single-default flag (`IsDefault`).

This feature is the **one sanctioned minimal-API exception** in an otherwise controller-based API (see [`enterprise-gpt-api/CLAUDE.md`](../../enterprise-gpt-api/CLAUDE.md)). Endpoints live in [`ModelEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ModelEndpoints.cs) as a `MapGroup("api/models")` group; the former `ModelsController` was removed. The public URL surface for the frontend (`GET /api/models`) is unchanged.

## 2. API surface

All endpoints require an authenticated user (`RequireAuthorization()` on the group). Admin-gated rows additionally carry [`PermissionEndpointFilter.Require(PermissionIds.Administrator)`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Filters/PermissionEndpointFilter.cs).

| Verb | Route | Access | Success | Failure modes |
|---|---|---|---|---|
| GET | `/api/models` | any authenticated | 200 `ModelDto[]` — **active only**, ordered by name | — |
| GET | `/api/models/all` | **admin** | 200 `ModelDto[]` — active **and** deactivated (`dateDeactivated` populated) | 403 not admin |
| GET | `/api/models/{id:guid}` | any authenticated | 200 `ModelDto` | 404 unknown/deactivated id |
| POST | `/api/models` | **admin** | 201 `ModelDto` + `Location: /api/models/{id}` | 400 validation, 403 not admin, 404 unknown provider |
| PUT | `/api/models/{id:guid}` | **admin** | 200 `ModelDto` | 400 validation, 403 not admin, 404 unknown model/provider |
| DELETE | `/api/models/{id:guid}` | **admin** | 204 — soft delete (sets `DateDeactivated`) | 403 not admin, 404 unknown/already deactivated |

All error responses are **RFC 9457 Problem Details**, served as `application/problem+json`:

```json
{
  "type": "/problems/resource-not-found",
  "title": "Resource not found",
  "status": 404,
  "detail": "Model not found.",
  "instance": "/api/models/3f2a91b5-...",
  "traceId": "00-a1b2c3d4e5f60718293a4b5c6d7e8f90-1122334455667788-01"
}
```

`type` identifies the failure class and comes from [`ProblemTypes`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Problems/ProblemTypes.cs) — `/problems/resource-not-found` for the 404s, `/problems/permission-required` for the 403s, `/problems/validation-error` for the 400s. Treat these as **opaque identifiers to match verbatim**, not URLs to fetch; changing one is a breaking API change. `traceId` (the W3C traceparent) and `instance` (the request path) are on every problem.

Validation failures add an `errors` dictionary keyed by the failing property, so a client can attach each message to its own form field:

```json
{
  "type": "/problems/validation-error",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "instance": "/api/models",
  "traceId": "00-a1b2c3d4e5f60718293a4b5c6d7e8f90-1122334455667788-01",
  "errors": {
    "Name": ["'Name' must not be empty."],
    "ContextWindowSize": ["'Context Window Size' must be greater than '0'."]
  }
}
```

Failures with no application-specific type — a bare `ArgumentException`, an unhandled 500, the framework's own 401/404/405 — carry the RFC 9110 status-section link instead (e.g. `https://tools.ietf.org/html/rfc9110#section-15.5.1`).

### 2.1 Wire-shape contracts

Response DTO — [`ModelDto`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/ModelDto.cs) (camelCase on the wire):

```json
{
  "id": "c36e22ed-...",
  "providerId": "3f2a91b5-...",
  "name": "rr-gpt-5.6-luna",
  "displayName": "GPT-5.6 Luna",
  "description": "OpenAI's GPT-5.6 Luna model.",
  "contextWindowSize": 200000,
  "maxOutputTokens": 16384,
  "isToolEnabled": true,
  "isDefault": false,
  "dateDeactivated": null
}
```

`dateDeactivated` is always `null` on user-facing endpoints (they filter to active rows); the admin listing uses it to distinguish toggled-off models.

Request DTOs — [`ModelActions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Actions/Model/ModelActions.cs). `CreateModelActionDto` and `UpdateModelActionDto` share the same shape (`providerId`, `name`, `displayName`, `description`, `contextWindowSize`, `maxOutputTokens`, `isToolEnabled`, `isDefault`). The update DTO carries **no id** — it comes from the route.

## 3. Request flow

```
Client ──► RequireAuthorization (JWT) ──► PermissionEndpointFilter (admin routes only)
       ──► ModelEndpoints handler ──► IModelService ──► FluentValidation ──► EF Core ──► SQL
```

1. [`ModelEndpoints`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ModelEndpoints.cs) handlers are thin: bind route/body/DI/`CancellationToken` (all inferred), call the service, wrap in `TypedResults` (`Ok`/`Created`/`NoContent`).
2. [`ModelService`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ModelService.cs) owns all business logic and throws precise exceptions; nothing is caught at the endpoint layer.
3. The exception-handler chain translates each exception into a problem: FluentValidation `ValidationException` → 400 `/problems/validation-error` ([`ValidationExceptionHandler`](../../enterprise-gpt-api/Enterprise.Gpt.Api/ExceptionHandlers/ValidationExceptionHandler.cs)); `NotFoundException` → 404 `/problems/resource-not-found` ([`GlobalExceptionHandler`](../../enterprise-gpt-api/Enterprise.Gpt.Api/ExceptionHandlers/GlobalExceptionHandler.cs)); cancellation → 499 with no body, since a disconnected client cannot read one.

Endpoints declare those responses with `.ProducesProblem(status)` and `.ProducesValidationProblem()` — never `.Produces<T>(status)` — and the group carries a single `.ProducesProblem(401)` because every route in it is authorized. Follow that convention for new routes; it is what keeps the OpenAPI document and the runtime shape from drifting apart.

## 4. Admin enforcement — `PermissionEndpointFilter`

Admin routes are gated by the same generic filter every other permission uses, mapped with the built-in Administrator id:

```csharp
group.MapGet("all", GetAllModelsAsync)
    .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.Administrator))
    .ProducesProblem(StatusCodes.Status403Forbidden);
```

[`PermissionEndpointFilter.Require`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Filters/PermissionEndpointFilter.cs) is a **static factory returning a filter delegate**, not an `IEndpointFilter` type: the generic `AddEndpointFilter<T>` activates its type from the container and so cannot carry permission ids. Key points:

- **Check:** the caller's Entra oid (`ITokenService.GetOid()`, which equals `User.Id`) must hold an active grant of `PermissionIds.Administrator` — a [`UserPermission`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/UserPermission.cs) row with `DateDeactivated IS NULL`. Permissions are rows in the `Core.Permission` table; built-in ids are defined in [`PermissionIds.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/PermissionIds.cs); the legacy `User.IsAdministrator` bool is **not** consulted.
- **Where the grants come from:** the singleton [`IUserPermissionCache`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Caching/UserPermissionCache.cs), not the database. The entry is written at sign-in and loaded lazily on a miss, so a warm admin request runs **no permission query and constructs no `EnterpriseGptDbContext`** for authorization. See [Permission Cache](../permissions/permission-cache.md) — including the [scale-out staleness bound](../permissions/permission-cache.md#51-entrylifetime--the-scale-out-staleness-bound), which is the one behaviour to understand before deploying more than one instance.
- **Multiple permissions:** `Require(params Guid[])` is **AND** — a caller must hold every listed grant. Ids are validated against `PermissionIds.Names` at *map* time, so an unknown id fails startup rather than emitting a nameless 403.
- **Denial:** returns 403 as a problem of type `/problems/permission-required`, title `"Permission required"`, `detail` `"Administrator permission is required."`, plus a `permissions: ["Administrator"]` extension. The message names the whole requirement rather than the missing part, so the response does not vary with which grants the caller happens to hold.
- **Ordering:** endpoint filters run after authorization, so a principal always exists when `GetOid()` is called.

To grant admin locally, insert a row: `UserPermission { UserId = <your oid>, PermissionId = 'a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d', DateDeactivated = NULL }` (the seeded Administrator permission id). Because that write bypasses `PermissionService`, it also bypasses cache eviction — sign out and back in, or wait out `Permissions:Cache:EntryLifetime`, before the new grant takes effect.

## 5. Validation

[`CreateModelActionDtoValidator` / `UpdateModelActionDtoValidator`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Actions/Model/ModelActions.cs) are colocated with the DTOs and auto-registered by `AddValidatorsFromAssemblies` in [`Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs). `ModelService` injects `IValidator<T>` and calls `ValidateAndThrowAsync` at the top of create/update.

Rules (lengths mirror the [`Model`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/Model.cs) entity): `providerId` non-empty; `name`/`displayName` required, ≤256; `description` required, ≤1024; `contextWindowSize`/`maxOutputTokens` > 0.

Provider existence is checked in the service (active providers only) and yields a 404 `"Provider not found."` — deliberately a service-level check rather than a validator rule (validators in `Enterprise.Gpt.Dto` have no database access) and rather than an FK failure (which would surface as an opaque 500).

## 6. Single-default invariant

At most one model has `IsDefault = 1`. When create/update sets `isDefault: true`, `ModelService.DemoteOtherDefaultsAsync` loads every other default model (active **and** inactive, so the invariant holds table-wide) as tracked entities and clears the flag. Demotions commit in the **same `SaveChangesAsync`** as the insert/update — one implicit transaction, so the invariant can never be observed half-applied. The `rowversion Version` column turns concurrent-admin races into `DbUpdateConcurrencyException` (currently a 500; see §8).

## 7. Soft delete

`DELETE` never removes rows. `ModelService.DeactivateModelAsync` runs a single `ExecuteUpdateAsync` targeting `Id == id && DateDeactivated == null`, setting `DateDeactivated`/`DateModified`/`ModifiedById` and clearing `IsDefault` (a tombstoned row must not remain the catalog default); zero rows affected ⇒ `NotFoundException` (404) — so deleting an already-deactivated model 404s rather than silently succeeding. Deactivated models disappear from the user-facing listings but remain in `GET /api/models/all`, which is how an administrator toggles a model back on (via `PUT` — currently reactivation would require clearing `DateDeactivated`; see §9).

## 8. Key files

| Concern | File |
|---|---|
| Endpoints (minimal API group) | [`Enterprise.Gpt.Api/Endpoints/ModelEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ModelEndpoints.cs) |
| Admin gate | [`Enterprise.Gpt.Api/Filters/PermissionEndpointFilter.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Filters/PermissionEndpointFilter.cs) — see [Permission Cache](../permissions/permission-cache.md) |
| Business logic | [`Enterprise.Gpt.Service/ModelService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ModelService.cs) |
| Entity ↔ DTO mapping | [`Enterprise.Gpt.Service/Mappers/ModelMapper.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Mappers/ModelMapper.cs) |
| Response DTO | [`Enterprise.Gpt.Dto/ModelDto.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/ModelDto.cs) |
| Request DTOs + validators | [`Enterprise.Gpt.Dto/Actions/Model/ModelActions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Actions/Model/ModelActions.cs) |
| Entity | [`Enterprise.Gpt.Entity/Model.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/Model.cs) |
| EF configuration + seed | [`Enterprise.Gpt.Repository/Configurations/ModelConfiguration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/ModelConfiguration.cs) |
| Frontend consumer | [`enterprise-ui/src/app/services/model.service.ts`](../../enterprise-ui/src/app/services/model.service.ts) (`GET /api/models` only) |

## 9. Known gaps and extension points

- **Reactivation endpoint:** `PUT` targets active rows only, so a deactivated model currently has no "toggle back on" path; add e.g. `POST /api/models/{id}/activate` (admin) that clears `DateDeactivated`.
- **Concurrent default races (two distinct gaps):** (a) racing updates that demote the same row hit the rowversion check and surface as `DbUpdateConcurrencyException` → 500; a 409 `IExceptionHandler` arm would make that honest. (b) Two concurrent *creates* with `isDefault: true` can each miss the other's uncommitted row and both succeed, leaving **two defaults with no error** — only a filtered unique index (`WHERE IsDefault = 1`) closes this at the DB level (add when regenerating migrations; note EF must order the demotion before the insert within the transaction).
- **Seed data:** the seed in [`ModelConfiguration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/ModelConfiguration.cs) predates the new validation rules — `DisplayName` is unset (blocks migration generation for [`EnterpriseGptDbContext`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/EnterpriseGptDbContext.cs); the `Migrations/` folder is currently empty) and `ContextWindowSize`/`MaxOutputTokens` are `0`, which the validators reject — the seeded model can't round-trip through `PUT` until real values are seeded.
- **Missing-OID tokens:** a principal that passes `RequireAuthorization()` but carries no OID claim (e.g. an app-only token) makes `TokenService.GetOid()` throw `UnauthorizedAccessException`, which the fallback handler maps to 500; a 401 arm in `GlobalExceptionHandler` would make that honest (pre-existing behavior, also reachable via `PermissionEndpointFilter`).
- **Admin UI:** the frontend only consumes `GET /api/models`; the admin CRUD surface has no UI yet. Reuse `PermissionEndpointFilter.Require(PermissionIds.Administrator)` for any future admin-only endpoints (provider CRUD, user permission management).
- **Frontend field drift:** `enterprise-ui`'s `ModelDto` still declares a stale `aiServiceId`; the backend now returns `providerId`. Align when building the admin UI.
