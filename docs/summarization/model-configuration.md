# Document Summarizer Configuration

Reference for the catalog and configuration foundation document summarization is built on: how the summarizer model is named, validated, and protected. Audience: operators deploying or upgrading this application, administrators managing the model catalog, and engineers building on `ISummarizerModelResolver`. For the algorithm that runs once this foundation resolves, see [Document Summarization Engine](summarization-engine.md).

## 1. What this is — and what it is not, yet

Document summarization is a multi-wave feature (see the [PRD](../prd/document-summarization/document-summarization.md)); this document covers **wave 1** only — `US-101` through `US-105`, all shipped together. Wave 1 gives the platform:

- a way to hide a catalog model from the chat picker without retiring it (`Model.IsUserSelectable`, covered in [Model Management §2.4](../models/model-management.md#24-isuserselectable));
- a corrected, pinned catalog row for the model that will do the summarizing;
- a configuration section naming that row and a startup check that refuses to boot if it does not resolve; and
- two admin-catalog guards that stop the pinned row from being deleted or repointed at a provider that cannot serve it.

**Nothing in this wave summarizes a document.** There is no summarize action, no request route, no background job, no map-reduce engine, and no stored summary — those are later waves (`EP-2` through `EP-6` in the PRD). What ships here is the foundation: a model the rest of the feature can rely on always resolving to something real.

## 2. Why a pinned model

Summarizing a document is a compression task, not a reasoning one, so its cost should not scale with whatever model a conversation's user happens to have selected for chat — which could be an expensive reasoning deployment. The platform instead resolves summarization work to **one constant, cheap, pinned model**, independent of the conversation's own selection.

Configuration names only the model's identity — its catalog row id. Every fact that describes it — provider, deployment name, context window, output cap, both prices — is read from that row in `Core.Ref.Model` at the moment of use, never restated in configuration. The practical effect: an administrator editing the row's price or context window from the admin models screen takes effect on the next resolution, with **no restart**.

## 3. Configuring it

```json
"Summarization": {
  "ModelId": "c36e22ed-262a-47a1-b2ba-06a38355ae0f"
}
```

`Summarization:ModelId` is the section's only key, bound by [`SummarizationOptions`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/SummarizationOptions.cs). It must name an active (`DateDeactivated IS NULL`) row in `Core.Ref.Model`. The committed `appsettings.json` seeds it to the id above — the shipped summarizer row — so a fresh clone boots with no environment-specific setup.

## 4. Startup validation

Two checks run before the application accepts a request, both fail-fast rather than deferring to the first summarization call (which, in this wave, does not yet exist to fail on):

1. **`Summarization:ModelId` must be a non-empty `Guid`.** Bound with `AddOptions<SummarizationOptions>().Bind(...).Validate(...).ValidateOnStart()` in [`Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) — the same pattern every other options class in this codebase uses.
2. **The id must resolve to a usable model**, checked by [`SummarizerBootstrapper.ValidateAsync`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Startup/SummarizerBootstrapper.cs):
   - it resolves the row through [`ISummarizerModelResolver`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Summarization/SummarizerModelResolver.cs), throwing `SummarizerNotConfiguredException` if the id names no active row; and
   - it resolves a chat client for the row's `ProviderId` through the existing `IChatClientResolver`, throwing `ProviderNotConfiguredException` if this deployment has no client registered for that provider — the same failure the resolver would raise on the first real call, raised here instead so it is a deploy-time condition.

Both are logged (`app.Logger.LogError`, naming the failure) and then rethrown, which crashes startup. `SummarizerBootstrapper.ValidateAsync` is called inline from `Program.cs`, in the existing non-`Testing` block, **after** `Database.Migrate()` and after the Cosmos DB provisioning step — so it always validates against a migrated database, and in particular against the row as `US-102`'s migration (§6 below) left it, not the row that preceded it.

**This validation is unconditional.** No feature flag gates it — the flag that will gate summarization *requests* (`US-601`) has not shipped — so a misconfigured `Summarization:ModelId` fails every deployment today, whether or not anyone intends to use the feature yet. Do not treat an unused feature as a reason to leave this unset.

It is deliberately **not** an `IHostedService`. `WebApplicationFactory`, used by this repository's integration tests, starts the host — and with it any registered `IHostedService` — before the test fixture has created the database schema; a hosted service reading `Core.Ref.Model` at that point would query a database with no tables. Calling it inline, at the same point `CosmosBootstrapper` already runs, avoids that ordering problem entirely.

## 5. Errors this introduces

| Condition | Status | Type | Notes |
|---|---|---|---|
| `Summarization:ModelId` names no active model | 503 | `/problems/provider-not-configured` | `SummarizerNotConfiguredException`. Reuses the existing type rather than adding an eleventh: to a caller, "no usable summarizer" and "no usable provider" are the same condition — this deployment cannot serve the model. |
| The summarizer's provider has no registered chat client | 503 | `/problems/provider-not-configured` | Same `ProviderNotConfiguredException` any other unconfigured provider raises. |
| `DELETE /api/models/{id}` targets the configured summarizer | 409 | none | `SummarizerProtectedException`. See §7. |
| `PUT /api/models/{id}` repoints the summarizer at a provider with no chat client | 400 | `/problems/validation-error`, keyed to `providerId` | Surfaces through the ordinary validation contract — see [Model Management §5](../models/model-management.md#5-validation). |
| `POST`/`PUT /api/models` sets `isDefault: true` and `isUserSelectable: false` together | 400 | `/problems/validation-error`, keyed to `isDefault` | Not specific to the summarizer, but the summarizer is exactly such a row — see [Model Management §2.4](../models/model-management.md#24-isuserselectable). |

The 503 paths above can only be reached today by an operator misconfiguring `Summarization:ModelId` after startup already passed (for example, deactivating the row through a direct database edit) — nothing in this wave calls `ISummarizerModelResolver` from a request path, since there is no summarization route yet.

`SummarizerNotConfiguredException` and `SummarizerProtectedException` both derive from `Exception` rather than a more specific built-in type, for the reason `ProviderNotConfiguredException` already does: deriving from `InvalidOperationException` would let a broader catch clause elsewhere in the pipeline reclassify an operator's configuration gap as the caller's bad request.

## 6. The corrected seed row

Migration `20260825031604_CorrectSummarizerModelSeed` overwrites four columns on the seeded row `c36e22ed-262a-47a1-b2ba-06a38355ae0f`, unconditionally — a data correction, not a conditional seed, following `ModelConfiguration`'s own `HasData` precedent of asserting the row's shape outright, so it overwrites even a value an administrator already hand-edited:

| Column | Before | After |
|---|---|---|
| `DeploymentName` | `rr-gpt-5.6-luna` | `rr-gpt5.6-luna` |
| `ContextWindowSize` | `0` | `1000000` |
| `MaxOutputTokens` | `0` | `16384` |
| `IsUserSelectable` | *(column did not exist)* | `false` |

It runs immediately after `20260825031549_AddModelIsUserSelectable`, the migration that adds the `IsUserSelectable` column and backfills every pre-existing row — including this one — to `true` before the second migration sets it back to `false` specifically for the summarizer. These are the eleventh and twelfth migrations in `Repository/Migrations/`, applied by `Database.Migrate()` at startup like every migration before them.

The corrected `MaxOutputTokens` (16,384) is a proposed constant rather than a measured value — the PRD records it as open to revision once real usage data exists (§9 of the PRD).

## 7. Protecting the pinned row from the admin catalog

Two guards live in [`ModelService`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ModelService.cs), both comparing the target id against `Summarization:ModelId`:

- **`DeactivateModelAsync` refuses a `DELETE` on the summarizer** with `SummarizerProtectedException` — 409, and deliberately **no domain problem type**. A `DELETE` carries no request body, so there is no field a client could key an `errors` entry to; the refusal's whole value is the sentence naming the setting to change, and that reaches the caller through `detail` alone. Without this guard, an administrator could deactivate the row from a fully supported admin route and make every instance of the application fail to start on its next restart — the check in §4 treats a deactivated row as absent — with no automatic recovery, because the migration that seeded the row is already recorded in `__EFMigrationsHistory` and a redeploy will not re-run it.
- **`UpdateModelAsync` refuses to repoint the summarizer's provider** when the new `providerId` has no registered chat client — 400, `ValidationException` keyed to `providerId`, reusing the ordinary validation contract rather than a new exception type. This is the same failure `ProviderNotConfiguredException` represents, caught here at edit time instead of at the next restart.

Both guards exist because **which model is the summarizer is an operator's configuration decision, not something the admin catalog screen administers.** An administrator can still rename the pinned row, reprice it, or toggle `IsUserSelectable` on it like any other model — the guards only stop the two edits that would make `Summarization:ModelId` point at something unusable.

## 8. Operator-facing consequences

Two behaviors are worth stating plainly, because both are easy to mistake for bugs and both are deliberate:

1. **A database built fresh has an empty chat model picker.** The seeded summarizer is now the *only* seeded model, and it ships `IsUserSelectable = false`. `GET /api/models` — the picker's source — returns an empty array until an administrator adds at least one chat model through the admin screen (and, typically, marks one as default). This is an explicit product decision recorded in the PRD, not a regression: nothing in this wave seeds a chat-usable model, because the only model this application knows about by name is the one reserved for summarization.
2. **`Summarization:ModelId` is validated at startup regardless of any feature flag.** Because `US-601` — the flag that will gate summarization requests — has not shipped, there is no way to defer this check by turning the feature "off." A misconfigured summarizer fails the deploy, not the first request.

## 9. Key files

| Concern | File |
|---|---|
| Configuration section | [`Enterprise.Gpt.Service/Settings/SummarizationOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/SummarizationOptions.cs) |
| Startup validation | [`Enterprise.Gpt.Api/Startup/SummarizerBootstrapper.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Startup/SummarizerBootstrapper.cs), called from [`Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) after `Database.Migrate()` |
| Row resolution | [`Enterprise.Gpt.Service/Summarization/SummarizerModelResolver.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Summarization/SummarizerModelResolver.cs) — `ISummarizerModelResolver.ResolveAsync`, read per call rather than cached |
| Exceptions | [`SummarizerNotConfiguredException.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Exceptions/SummarizerNotConfiguredException.cs), [`SummarizerProtectedException.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Exceptions/SummarizerProtectedException.cs) |
| Exception → problem mapping | [`Enterprise.Gpt.Api/ExceptionHandlers/GlobalExceptionHandler.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/ExceptionHandlers/GlobalExceptionHandler.cs) |
| Admin-catalog guards | [`Enterprise.Gpt.Service/ModelService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ModelService.cs) — `EnsureNotTheSummarizer`, `EnsureSummarizerProviderIsServable` |
| Seed correction and the `IsUserSelectable` column | [`Migrations/20260825031549_AddModelIsUserSelectable.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260825031549_AddModelIsUserSelectable.cs), [`Migrations/20260825031604_CorrectSummarizerModelSeed.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260825031604_CorrectSummarizerModelSeed.cs) |
| The general `IsUserSelectable` catalog flag | [Model Management §2.4](../models/model-management.md#24-isuserselectable) |
| Default configuration | [`appsettings.json`](../../enterprise-gpt-api/Enterprise.Gpt.Api/appsettings.json) — `Summarization:ModelId` |

## 10. What's next

**Wave 2 has since shipped** — the summarization engine itself (fit decision, map-reduce, the collapse loop) — documented separately in [Document Summarization Engine](summarization-engine.md), since it is a large enough concern to warrant its own document. It is still service-layer only: nothing in wave 2 added a route, persistence, usage rows, or a feature flag either.

Waves 3 through 6 of the PRD — summary persistence and the request/job API, token accounting and telemetry, the frontend surface, and the feature flag and rollout bounds — are not yet built. This document, and its sibling above, will grow with them; until then, the [PRD](../prd/document-summarization/document-summarization.md) is the authority for what is planned.
