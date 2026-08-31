# Model Catalog

`Core.Ref.Model` is the list of models this deployment offers. Administrators maintain it; every
turn resolves through it.

## API surface

| Verb | Route | Access |
| --- | --- | --- |
| GET | `api/models` | Any signed-in caller — active, user-selectable rows |
| GET | `api/models/all` | Admin — every row, deactivated included |
| GET | `api/models/{id}` | Admin |
| POST | `api/models` | Admin |
| PUT | `api/models/{id}` | Admin |
| DELETE | `api/models/{id}` | Admin — soft delete |

Admin routes carry `PermissionEndpointFilter.Require(PermissionIds.Administrator)`.

## The row

| Column | Purpose |
| --- | --- |
| `Name` | What a user picks in the model menu |
| `ProviderId` | Selects the keyed chat client — see [providers.md](providers.md) |
| `DeploymentName` | The value sent as the request's model identifier |
| `IsDefault` | The model a new conversation starts on |
| `IsUserSelectable` | Whether the row appears in the user-facing menu |
| `IsReasoningEnabled` | Whether reasoning is requested every turn (Azure OpenAI only) |
| `ContextWindowSize` | Feeds the context budget; `0` means unbounded for chat |
| `MaxOutputTokens` | Reserved from the window |
| Pricing columns | Snapshotted onto each usage row when a call runs |

### Two flags with the same store-default problem

`IsUserSelectable` and the document `Type` discriminator are both configured
`HasDefaultValue(...).ValueGeneratedNever()`. EF scaffolds a migration's `AddColumn` from the CLR
default alone, and a store-generated `bool` is silently dropped from EF's `HasData` seed differ — so
without the explicit pairing, an added bool column either fails to backfill existing rows or
disappears from the seed. `IsUserSelectable` defaults to `1`, which backfills every existing row to
*visible*.

`IsUserSelectable` is what lets a deployment keep a model in the catalog for the summarizer or the
File Agent without offering it in the chat menu.

## Invariants

- **Single default.** Saving a row with `IsDefault` true demotes whichever row held it. No response
  describes that demotion, which is why the client raises a catalog-changed event and refetches
  rather than patching locally.
- **Soft delete.** `DELETE` sets `DateDeactivated`; nothing is removed. A deactivated model still
  appears in `api/models/all` and still resolves for historical usage rows.
- **Pinned rows.** The summarizer and the File Agent each name a catalog row by id in configuration,
  validated at startup by their bootstrappers. Deactivating or misconfiguring one of those rows
  fails that subsystem with 503 `provider-not-configured` rather than silently falling back.

## Validation

Server validation returns 400 as `HttpValidationProblemDetails`, whose `errors` dictionary is keyed
by the failing **property name**. The client maps those PascalCase keys onto camelCase form fields
through `serverMessagesFor`, which reimplements .NET's own camel-casing algorithm — see
[../frontend/errors.md](../frontend/errors.md).

## The client

`ModelCatalogStore` (root-scoped) holds what the user may pick; `AdminModelsStore` (route-scoped)
holds the admin grid, which is unpaginated with client-side filtering because the catalog is small
and complete.

`ModelCatalogStore` composes a handler for `adminEvents.modelCatalogChanged`. The root store is
genuinely on the initial bundle graph, so composing that handler puts it there too even though the
admin screens it coordinates with stay lazy.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ModelEndpoints.cs` | The reference endpoint module |
| `enterprise-gpt-api/Enterprise.Gpt.Service/ModelService.cs` | CRUD, the single-default invariant, soft delete |
| `enterprise-gpt-api/Enterprise.Gpt.Entity/Model.cs` | The row |
| `enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/ModelConfiguration.cs` | The store-default pairing |
| `enterprise-gpt-ui/src/app/core/catalog/model-catalog-store.ts` | User-facing catalog |
| `enterprise-gpt-ui/src/app/features/admin/models/admin-models-store.ts` | The admin grid |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Services/ModelServiceTests.cs` | Default demotion and validation |

## Related

- [providers.md](providers.md)
- [../conversations/usage-and-reporting.md](../conversations/usage-and-reporting.md)
- [../documents/summarization.md](../documents/summarization.md)
