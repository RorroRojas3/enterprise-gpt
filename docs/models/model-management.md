# Model Management

End-to-end reference for the model catalog feature: how models are listed, created, updated, and soft-deleted, and how administrator-only access is enforced. Audience: engineers onboarding to or extending the feature.

## 1. Overview

Models are the LLM deployments users can chat with (e.g. an Azure OpenAI deployment). Each model belongs to a [`Provider`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/Provider.cs) and carries routing/limit metadata (`ContextWindowSize`, `MaxOutputTokens`, `IsToolEnabled`) plus a single-default flag (`IsDefault`).

This feature is the **one sanctioned minimal-API exception** in an otherwise controller-based API (see [`enterprise-gpt-api/CLAUDE.md`](../../enterprise-gpt-api/CLAUDE.md)). Endpoints live in [`ModelEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ModelEndpoints.cs) as a `MapGroup("api/models")` group; the former `ModelsController` was removed. The public URL surface for the frontend (`GET /api/models`) is unchanged.

## 2. API surface

All endpoints require an authenticated user (`RequireAuthorization()` on the group). Admin-gated rows additionally pass through [`AdminEndpointFilter`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Filters/AdminEndpointFilter.cs).

| Verb | Route | Access | Success | Failure modes |
|---|---|---|---|---|
| GET | `/api/models` | any authenticated | 200 `ModelDto[]` — **active only**, ordered by name | — |
| GET | `/api/models/all` | **admin** | 200 `ModelDto[]` — active **and** deactivated (`dateDeactivated` populated) | 403 not admin |
| GET | `/api/models/{id:guid}` | any authenticated | 200 `ModelDto` | 404 unknown/deactivated id |
| POST | `/api/models` | **admin** | 201 `ModelDto` + `Location: /api/models/{id}` | 400 validation, 403 not admin, 404 unknown provider |
| PUT | `/api/models/{id:guid}` | **admin** | 200 `ModelDto` | 400 validation, 403 not admin, 404 unknown model/provider |
| DELETE | `/api/models/{id:guid}` | **admin** | 204 — soft delete (sets `DateDeactivated`) | 403 not admin, 404 unknown/already deactivated |

All error responses use the standard [`ErrorDto`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/ErrorDto.cs) wire shape `{ statusCode, errors[], traceId, timestamp }`.

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
Client ──► RequireAuthorization (JWT) ──► AdminEndpointFilter (admin routes only)
       ──► ModelEndpoints handler ──► IModelService ──► FluentValidation ──► EF Core ──► SQL
```

1. [`ModelEndpoints`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ModelEndpoints.cs) handlers are thin: bind route/body/DI/`CancellationToken` (all inferred), call the service, wrap in `TypedResults` (`Ok`/`Created`/`NoContent`).
2. [`ModelService`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ModelService.cs) owns all business logic and throws precise exceptions; nothing is caught at the endpoint layer.
3. The exception-handler chain translates: FluentValidation `ValidationException` → 400 ([`ValidationExceptionHandler`](../../enterprise-gpt-api/Enterprise.Gpt.Api/ExceptionHandlers/ValidationExceptionHandler.cs)); `NotFoundException` → 404 ([`GlobalExceptionHandler`](../../enterprise-gpt-api/Enterprise.Gpt.Api/ExceptionHandlers/GlobalExceptionHandler.cs)); cancellation → 499.

## 4. Admin enforcement — `AdminEndpointFilter`

[`AdminEndpointFilter`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Filters/AdminEndpointFilter.cs) is an `IEndpointFilter` attached per-route with `.AddEndpointFilter<AdminEndpointFilter>()`. Key points:

- **Instantiation:** created per request by the framework from `HttpContext.RequestServices` — constructor-injects the singleton [`ITokenService`](../../enterprise-gpt-api/Enterprise.Gpt.Service/TokenService.cs) and the scoped [`EnterpriseGptDbContext`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/EnterpriseGptDbContext.cs). It must **not** be registered in `Program.cs`.
- **Check:** the caller's AAD oid (`ITokenService.GetOid()`, which equals `User.Id`) must have a [`UserPermission`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/UserPermission.cs) row with `Permission = Permissions.Administrator` and `DateDeactivated IS NULL`. Permissions are defined in [`Permissions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Common/Enums/Permissions.cs); the legacy `User.IsAdministrator` bool is **not** consulted.
- **Denial:** returns 403 with the `ErrorDto` shape (`errors: ["Administrator permission is required."]`).
- **Ordering:** endpoint filters run after authorization, so a principal always exists when `GetOid()` is called.

To grant admin locally, insert a row: `UserPermission { UserId = <your oid>, Permission = 1, DateDeactivated = NULL }`.

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
| Admin gate | [`Enterprise.Gpt.Api/Filters/AdminEndpointFilter.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Filters/AdminEndpointFilter.cs) |
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
- **Missing-OID tokens:** a principal that passes `RequireAuthorization()` but carries no OID claim (e.g. an app-only token) makes `TokenService.GetOid()` throw `UnauthorizedAccessException`, which the fallback handler maps to 500; a 401 arm in `GlobalExceptionHandler` would make that honest (pre-existing behavior, also reachable via `AdminEndpointFilter`).
- **Admin UI:** the frontend only consumes `GET /api/models`; the admin CRUD surface has no UI yet. Reuse `AdminEndpointFilter` for any future admin-only endpoints (provider CRUD, user permission management).
- **Frontend field drift:** `enterprise-ui`'s `ModelDto` still declares a stale `aiServiceId`; the backend now returns `providerId`. Align when building the admin UI.
