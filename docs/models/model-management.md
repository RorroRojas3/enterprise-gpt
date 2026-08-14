# Model Management

End-to-end reference for the model catalog feature: how models are listed, created, updated, and soft-deleted, and how administrator-only access is enforced. Audience: engineers onboarding to or extending the feature.

## 1. Overview

Models are the LLM deployments users can chat with (an Azure OpenAI deployment, or an Amazon Bedrock model or inference profile). Each model belongs to a [`Provider`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/Provider.cs) and carries routing/limit metadata (`ContextWindowSize`, `MaxOutputTokens`, `IsToolEnabled`, `IsReasoningEnabled`) plus a single-default flag (`IsDefault`).

`ProviderId` is not decoration: it selects the chat client that serves every turn on the model. See [Amazon Bedrock Provider](amazon-bedrock.md) for how that routing works and what happens when a model names a provider this deployment cannot serve; [Azure OpenAI Provider](azure-openai.md), [Azure AI Foundry Provider](azure-ai-foundry.md) and [Anthropic Provider](anthropic.md) cover the other three. Note that the two Azure providers reach the **same** resource and differ only in API surface — Responses versus Chat Completions — which is what decides whether a model can serve reasoning.

Endpoints live in [`ModelEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ModelEndpoints.cs) as a `MapGroup("api/models")` group; the former `ModelsController` was removed. `ModelEndpoints` is the template the other minimal-API modules follow (see [`.claude/CLAUDE.md`](../../.claude/CLAUDE.md)). The public URL surface for the frontend (`GET /api/models`) is unchanged.

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
  "name": "RR GPT 5.6 Luna",
  "deploymentName": "rr-gpt-5.6-luna",
  "description": "OpenAI's GPT-5.6 Luna model.",
  "contextWindowSize": 200000,
  "maxOutputTokens": 16384,
  "isToolEnabled": true,
  "isReasoningEnabled": false,
  "isDefault": false,
  "dateDeactivated": null
}
```

`dateDeactivated` is always `null` on user-facing endpoints (they filter to active rows); the admin listing uses it to distinguish toggled-off models.

Request DTOs — [`ModelActions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Actions/Model/ModelActions.cs). `CreateModelActionDto` and `UpdateModelActionDto` share the same shape (`providerId`, `name`, `deploymentName`, `description`, `contextWindowSize`, `maxOutputTokens`, `isToolEnabled`, `isReasoningEnabled`, `isDefault`). The update DTO carries **no id** — it comes from the route.

`isReasoningEnabled` is additive and defaults to `false`, so a client that omits it keeps the old behaviour on create — but remember that `PUT` replaces the whole model, so an update that omits it turns reasoning **off** (§2.3).

### 2.2 `name` and `deploymentName`

`displayName` was **removed** and `deploymentName` added. This is a breaking change to `ModelDto`, `CreateModelActionDto`, and `UpdateModelActionDto`; the two fields also swapped meaning, so a client that simply renames the property will send the wrong value.

| Field | Length | Meaning |
|---|---|---|
| `name` | ≤256 | The human label. What the picker shows. Nothing routes on it, so it is safe to rename. |
| `deploymentName` | ≤512 | The provider-side identifier, sent to the LLM as `ChatOptions.ModelId`. |

`deploymentName` is an Azure OpenAI deployment name on Azure, and a model id, inference profile id, or ARN on Bedrock — see [Amazon Bedrock Provider §7](amazon-bedrock.md#7-choosing-a-model-id-or-an-inference-profile). It is `nvarchar(512)` rather than 256 because a fully qualified Bedrock inference-profile ARN is far longer than an Azure deployment name.

Before this change, `name` carried the deployment identifier and `displayName` the label — so existing rows have the two values the wrong way round until they are swapped. That swap ships as **no migration and no script**; see [Amazon Bedrock Provider §10.1](amazon-bedrock.md#101-a-plain-rename-leaves-every-existing-row-inverted) for the SQL and for why the failure is silent.

Two knock-on effects worth knowing:

- **Listing order changed.** Both listings still `OrderBy(x => x.Name)`, but `Name` is now the label rather than the deployment id, so models sort the way a user would expect instead of by provider naming convention.
- **The Cosmos DB message transcript records `deploymentName`**, not the label. The transcript is append-only, so the recorded value has to stay meaningful after a catalog rename, and it is the deployment that identifies which model actually served and billed the turn.

### 2.3 `isReasoningEnabled`

A per-model switch for whether the deployment is asked for a **reasoning summary** on every turn — the text the client shows in the assistant's reasoning region while it thinks. Column: `Core.Ref.Model.IsReasoningEnabled`, `bit NOT NULL DEFAULT 0`, added by migration `20260813233326_AddModelIsReasoningEnabled`.

Three things about it are deliberate:

- **It is opt-in, and existing rows are not back-filled.** A deployment that does not support reasoning **rejects the whole request** rather than ignoring the option, so the failure mode is total: guessing "yes" for the wrong model breaks every turn on it. Reasoning also costs — reasoning tokens are billed as output tokens.
- **It carries no validation rule.** There is nothing to validate: it is a `bool`, and whether the named deployment honours it is a fact only the provider knows. The consequence is that a wrong value fails at chat time, not at create time — the same shape as `deploymentName` naming a deployment that does not exist.
- **Its effect is provider-specific today.** Only the **Azure OpenAI** client reads it, because reasoning reaches that provider as a per-request option on the Responses API. The Bedrock and Anthropic clients take their reasoning settings from their own configuration, and the Azure AI Foundry client — Chat Completions on the same resource — has no reasoning surface at all and strips the request, so the flag changes nothing on a model any of the three serves. See [Azure OpenAI §5](azure-openai.md#5-reasoning) for how the flag travels from this row to the wire, [Azure AI Foundry §4](azure-ai-foundry.md#4-reasoning-is-stripped-and-that-is-the-design) for why it is harmless there rather than fatal, and [Anthropic §5](anthropic.md#5-thinking-and-effort) for the deployment-wide alternative.

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

Rules (lengths mirror the [`Model`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/Model.cs) entity):

| Field | Rule |
|---|---|
| `providerId` | non-empty |
| `name` | `NotEmpty`, `MaximumLength(256)` |
| `deploymentName` | `NotEmpty`, `MaximumLength(512)` |
| `description` | `NotEmpty`, `MaximumLength(1024)` |
| `contextWindowSize`, `maxOutputTokens` | greater than 0 |

`deploymentName` being `NotEmpty` is load-bearing beyond the form: the streaming path reads it straight out of SQL and sends it as `ChatOptions.ModelId` without re-checking, so an empty value would reach the provider.

Provider existence is checked in the service (active providers only) and yields a 404 `"Provider not found."` — deliberately a service-level check rather than a validator rule (validators in `Enterprise.Gpt.Dto` have no database access) and rather than an FK failure (which would surface as an opaque 500). The check confirms the provider *row* exists, **not** that this deployment can serve it: creating a Bedrock model while Bedrock is switched off succeeds and fails later with a 503 ([Amazon Bedrock Provider §9](amazon-bedrock.md#9-when-the-provider-is-not-configured--the-503-contract)).

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
| Provider ids + provider seed | [`Enterprise.Gpt.Dto/Enums/Providers.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/Providers.cs), [`Configurations/ProviderConfiguration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/ProviderConfiguration.cs) |
| Provider → chat client routing | [`Enterprise.Gpt.Service/Chat/ChatClientResolver.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Chat/ChatClientResolver.cs) — see [Azure OpenAI](azure-openai.md), [Azure AI Foundry](azure-ai-foundry.md), [Amazon Bedrock](amazon-bedrock.md), [Anthropic](anthropic.md) |
| Reasoning column and migration | [`Enterprise.Gpt.Entity/Model.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/Model.cs), [`Migrations/20260813233326_AddModelIsReasoningEnabled.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260813233326_AddModelIsReasoningEnabled.cs) |
| Frontend consumer | [`enterprise-ui/src/app/services/model.service.ts`](../../enterprise-ui/src/app/services/model.service.ts) (`GET /api/models` only) |

## 9. Known gaps and extension points

- **Reactivation endpoint:** `PUT` targets active rows only, so a deactivated model currently has no "toggle back on" path; add e.g. `POST /api/models/{id}/activate` (admin) that clears `DateDeactivated`.
- **Concurrent default races (two distinct gaps):** (a) racing updates that demote the same row hit the rowversion check and surface as `DbUpdateConcurrencyException` → 500; a 409 `IExceptionHandler` arm would make that honest. (b) Two concurrent *creates* with `isDefault: true` can each miss the other's uncommitted row and both succeed, leaving **two defaults with no error** — only a filtered unique index (`WHERE IsDefault = 1`) closes this at the DB level (add when regenerating migrations; note EF must order the demotion before the insert within the transaction).
- **Seed data:** the seed in [`ModelConfiguration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/ModelConfiguration.cs) now sets both name columns — `Name = "RR GPT 5.6 Luna"`, `DeploymentName = "rr-gpt-5.6-luna"` — but still leaves `ContextWindowSize`/`MaxOutputTokens` at `0`, which the validators reject, so the seeded model cannot round-trip through `PUT` until real values are seeded.
- **Schema management is now half-migrated.** `Repository/Migrations/` holds two migrations — `20260811024339_InitialCreate` (the whole schema plus the `HasData` seeds, including all three provider rows) and `20260813233326_AddModelIsReasoningEnabled` — and `Database.Migrate()` runs at startup outside the `Testing` environment, so a database this application creates from empty is now built and seeded by migrations rather than by hand. What has **not** changed is the fate of a database that predates them: it carries no `__EFMigrationsHistory` entries, so `Migrate()` will try to create tables that already exist. Bringing an out-of-band database under migrations means baselining it (mark `InitialCreate` as applied, then let the rest run). Several docs still describe the pre-migration world — the two provider references' "insert the provider row by hand" steps ([Amazon Bedrock §10](amazon-bedrock.md#10-operational-notes--the-schema-is-not-migrated), [Anthropic §10.1](anthropic.md#101-the-anthropic-provider-row-does-not-reach-a-migrated-database)) apply only to a database built before `InitialCreate` existed.
- **Missing-OID tokens:** a principal that passes `RequireAuthorization()` but carries no OID claim (e.g. an app-only token) makes `TokenService.GetOid()` throw `UnauthorizedAccessException`, which the fallback handler maps to 500; a 401 arm in `GlobalExceptionHandler` would make that honest (pre-existing behavior, also reachable via `PermissionEndpointFilter`).
- **Admin UI:** the frontend only consumes `GET /api/models`; the admin CRUD surface has no UI yet. Reuse `PermissionEndpointFilter.Require(PermissionIds.Administrator)` for any future admin-only endpoints (provider CRUD, user permission management).
- **Frontend field drift:** `enterprise-ui`'s [`ModelDto`](../../enterprise-ui/src/app/dtos/ModelDto.ts) still declares a stale `aiServiceId`; the backend returns `providerId`. Because that field never binds, `prompt-box.component.ts`'s `getServiceIcon` falls through to its default for every model — including the new Bedrock ones, which have no icon case of their own. The frontend does not consume `deploymentName` at all, so the rename cost it nothing; the model picker now shows the human label because `name` is one. Align the field and add a Bedrock icon when building the admin UI.
