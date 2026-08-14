# Azure AI Foundry Provider

Reference for the optional second Azure provider: what it is for, how it differs from the Azure OpenAI provider that shares its resource and its endpoint, how to switch it on, and why a reasoning-flagged model served here reasons anyway but shows nothing. Audience: engineers working on the chat path, and operators deploying this build. Companion to [Azure OpenAI Provider](azure-openai.md), [Amazon Bedrock](amazon-bedrock.md) and [Anthropic](anthropic.md).

> ## ⚠️ This page used to document a different provider
>
> Until this release, "Azure AI Foundry" was the name of the single Azure provider — the one that serves the default model and every document embedding. That provider is now called **Azure OpenAI** and is documented at **[azure-openai.md](azure-openai.md)**.
>
> **Its configuration section was renamed `AzureAIFoundry:*` → `AzureOpenAI:*`, and `AzureAIFoundry:*` now means the provider described on this page.** If you are here looking for `Url`, `ApiKey`, `DefaultModel`, `EmbeddingModel` or the reasoning settings, they moved: see [Azure OpenAI §8](azure-openai.md#8-upgrading-from-the-previous-release--the-configuration-rename) for the re-key steps.

## 1. Why this exists

An Azure AI Foundry resource hosts more than Azure OpenAI deployments. It also serves **Foundry Models** from other providers — DeepSeek, Llama, Mistral, Grok — and those do not all implement the Responses API. Microsoft's guidance is to use Responses for Azure OpenAI models, and Chat Completions on the same v1 endpoint for the rest; a deployment that does not support Responses answers **`400 Model not supported`** rather than degrading ([Endpoints for Foundry Models](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/endpoints#azure-openai-inference-endpoint)).

Before this release the application had one Azure chat client, on the Responses API. That made every non-Responses deployment on the resource unreachable: a model row pointing at one created fine and then failed on every turn. This provider is the second surface, so the catalog can carry both kinds of deployment at once.

### 1.1 Same resource, same endpoint, different surface

| | [Azure OpenAI](azure-openai.md) | **Azure AI Foundry** (this page) |
|---|---|---|
| Provider GUID | `3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0` | `b7d4e0c3-5a18-4f92-9c6e-2d31f8a70b45` |
| API surface | Responses | **Chat Completions** |
| Reasoning summaries | Yes | **No** — the request is stripped ([§4](#4-reasoning-is-stripped-and-that-is-the-design)) |
| Document embeddings | Yes — owns the generator | No |
| Configuration section | `AzureOpenAI:*` | `AzureAIFoundry:*` |
| Registered when | Always | `AzureAIFoundry:Enabled` is `true` |
| SDK | `OpenAI` 2.12.0 | `OpenAI` 2.12.0 — the same client |
| Experimental suppressions | `OPENAI001`, `MEAI001` | **None** |

Both reach the same resource over the same OpenAI-compatible v1 endpoint with the same kind of credential. The registration differs in one call:

```csharp
new OpenAIClient(
        new ApiKeyCredential(settings.ApiKey),
        new OpenAIClientOptions { Endpoint = settings.V1Endpoint })
    .GetChatClient(settings.DefaultModel)
    .AsIChatClient();
```

`GetChatClient` and its `AsIChatClient()` overload are **stable and unattributed**, where the Responses equivalents are still `[Experimental]`. That is a real benefit of the split rather than an accident: this provider and the relocated embedding generator carry no suppressions at all, so the experimental surface in the solution is now exactly the Responses path and nothing else ([Azure OpenAI §1.3](azure-openai.md#13-the-experimental-diagnostics-and-why-no-upgrade-retires-them)).

### 1.2 Why a separate section rather than a flag

`AzureAIFoundry` is its own configuration section, not a mode switch on `AzureOpenAI`. Two reasons:

- **Credentials differ in practice.** A deployment running both commonly points them at different resources, or at the same resource with different keys.
- **The choice belongs in the catalog.** A model row selects its surface by naming a provider, so an administrator changes it per model. A configuration flag would decide it for every model at once.

## 2. Quick start

**1. Switch the provider on** with user secrets — never `appsettings.json`, which is checked in and ships this section with `Enabled: false` and blank values:

```bash
cd enterprise-gpt-api/Enterprise.Gpt.Api
dotnet user-secrets set "AzureAIFoundry:Enabled" "true"
dotnet user-secrets set "AzureAIFoundry:Url" "https://my-resource.services.ai.azure.com/"
dotnet user-secrets set "AzureAIFoundry:ApiKey" "your-resource-key"
dotnet user-secrets set "AzureAIFoundry:DefaultModel" "DeepSeek-R1"
```

`Url` is the **resource root**. Do not include `/openai/v1` — the client appends it, and a URL whose path already carries it is rejected at startup ([Azure OpenAI §3.2](azure-openai.md#32-the-v1-endpoint-is-derived-not-configured) documents the shared derivation).

**2. Start the API.** The four validators run at boot, all gated on `Enabled` ([§3.2](#32-startup-validation)).

**3. Add a model** pointed at this provider:

```bash
curl -X POST https://localhost:7045/api/models \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
        "providerId": "b7d4e0c3-5a18-4f92-9c6e-2d31f8a70b45",
        "name": "DeepSeek R1",
        "deploymentName": "DeepSeek-R1",
        "description": "DeepSeek-R1, served by Azure AI Foundry over Chat Completions.",
        "contextWindowSize": 128000,
        "maxOutputTokens": 8192,
        "isToolEnabled": true,
        "isDefault": false
      }'
```

**The provider row is seeded, but only a migrated database has it** — see [§6](#6-operational-notes) before deploying.

## 3. Configuration

### 3.1 Settings

The `AzureAIFoundry` section binds to [`AzureAIFoundryOptions`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/AzureAIFoundryOptions.cs), which derives its URL and credential members from the shared [`AzureV1EndpointOptions`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/AzureV1EndpointOptions.cs).

| Setting | Default | Required | What it does |
|---|---|---|---|
| `Enabled` | `false` | no | Registers the chat client at all. Off means no keyed client, and a model row pointing here fails with a 503 ([§6](#6-operational-notes)). |
| `Url` | — | if enabled | The resource root, e.g. `https://my-resource.services.ai.azure.com/`. **Not** the v1 endpoint. |
| `ApiKey` | — | if enabled | The resource key. **Secrets store only** — user secrets locally, Key Vault when deployed. |
| `DefaultModel` | — | if enabled | The deployment the SDK is constructed with. Every turn sets the model explicitly from the catalog, so this is only the fallback the client needs to exist. |

**There are no reasoning settings, and no embedding setting.** Both absences are deliberate: reasoning parameters are a Responses-API feature ([§4](#4-reasoning-is-stripped-and-that-is-the-design)), and the embedding generator belongs to the Azure OpenAI provider, which always exists. A test pins the first — `Options_CarryNoReasoningSettings` fails if a `Reasoning*` property is ever added here.

`appsettings.json` ships `Enabled`, `Url` and `DefaultModel` as empty placeholders so the section is discoverable; `ApiKey` is deliberately absent from it.

### 3.2 Startup validation

Four predicates run under `ValidateOnStart()`, every one of them short-circuited on `Enabled` — the same shape as Bedrock and Anthropic. A deployment that does not use this provider needs no settings at all; a **half-filled** section still fails at startup rather than on the first conversation.

| Rule | Message |
|---|---|
| `Url` is an absolute http/https URI | `AzureAIFoundry:Url must be an absolute http or https URI when AzureAIFoundry:Enabled is true, for example https://my-resource.services.ai.azure.com/.` |
| `Url`'s **path** does not contain `openai` | `AzureAIFoundry:Url must be the resource root when AzureAIFoundry:Enabled is true, without /openai/v1 — the client appends it.` |
| `ApiKey` is non-blank | `AzureAIFoundry:ApiKey is required when AzureAIFoundry:Enabled is true.` |
| `DefaultModel` is non-blank | `AzureAIFoundry:DefaultModel is required when AzureAIFoundry:Enabled is true.` |

The URL predicates themselves live on the shared base type, so the two Azure providers cannot drift apart on what a valid resource root is. None of the messages echoes the key.

## 4. Reasoning is stripped, and that is the design

`ConversationService` writes the catalog's `IsReasoningEnabled` onto every turn on two channels, because they reach different providers ([Azure OpenAI §5.1](azure-openai.md#51-the-gate-is-a-catalog-column-and-it-is-opt-in)). One of them is `ChatOptions.Reasoning` — Microsoft.Extensions.AI's provider-agnostic reasoning request — and **the Chat Completions adapter translates it into `reasoning_effort` on the wire**, which a deployment reached over this surface rejects.

So [`AzureAIFoundryChatDefaults`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Chat/AzureAIFoundryChatDefaults.cs) clears it unconditionally:

```csharp
options.Reasoning = null;
```

That is the entire callback, and its unconditional-ness is the point. Without it, a model row here that sets `isReasoningEnabled` would fail **every turn** — and setting that flag on a Foundry-served model is an entirely reasonable mistake, because plenty of Foundry Models do reason. Clearing it in the one place that knows this provider's rule is what makes the mistake harmless instead. `AzureAIFoundryChatClientBridgeTests` pins the absence of `reasoning_effort` on the wire for a reasoning-flagged model, which is the load-bearing assertion in that class.

**A distinction worth being precise about.** This is a statement about the *request*, not the *response*. Some Foundry Models — DeepSeek-R1 among them — reason regardless and return that content inline over Chat Completions, typically wrapped in `<think>` tags in the message body. Nothing here suppresses that; it simply arrives as answer text rather than as a structured reasoning item. What this provider cannot do is **ask for a structured reasoning summary**, which is what fills the client's reasoning region. A deployment that needs that region filled belongs on the [Azure OpenAI provider](azure-openai.md).

`ConversationId` is deliberately left alone, and the asymmetry with the Responses callback is instructive: that one must clear it, because the Responses adapter reads any id not beginning with `conv_` as a response id and sends it as `previous_response_id` ([Azure OpenAI §6.1](azure-openai.md#61-chatoptionsconversationid-is-now-a-wire-parameter)). Chat Completions is stateless and the adapter has nowhere to put it, so there is nothing to defend against and nothing to clear. Nor is there any of the storage or encrypted-reasoning handling the Responses path needs — the whole exchange travels in each request because it always did.

## 5. Adding a Foundry-backed model to the catalog

Model CRUD is administrator-only and otherwise unchanged; see [Model Management](model-management.md). What is specific to this provider:

- **`providerId`** is `b7d4e0c3-5a18-4f92-9c6e-2d31f8a70b45`.
- **`deploymentName`** is the **Azure deployment name**, not a model family name — it goes into the request's `model` field on the v1 endpoint.
- **`isReasoningEnabled`** has no effect here. It is not rejected at create time and it is not an error; the request is stripped of reasoning at turn time ([§4](#4-reasoning-is-stripped-and-that-is-the-design)).
- **`isToolEnabled`** governs whether MCP tools and document retrieval are attached at all, exactly as elsewhere. Function calling works over Chat Completions unchanged; `AsIChatClient_AcrossAToolCall_CompletesTheExchange` pins a full tool round trip against a stub transport.

An Azure OpenAI deployment will serve happily from this provider too — Chat Completions accepts it — but it loses reasoning by doing so. Put Azure OpenAI deployments on the [Azure OpenAI provider](azure-openai.md) unless there is a reason not to.

## 6. Operational notes

- **The provider row ships as a real migration.** `20260814031023_AddAzureAIFoundryProvider` inserts `b7d4e0c3-5a18-4f92-9c6e-2d31f8a70b45` / `AzureAIFoundry` into `Core.Ref.Provider`, and `Database.Migrate()` applies it at startup outside the `Testing` environment. Unlike the Bedrock and Anthropic rows, there is **no manual `INSERT` to remember** — provided the database has an `__EFMigrationsHistory` at all. A database that predates the repository's first migration must be baselined before `Migrate()` can run against it; until then the row is absent and every attempt to create a model here returns 404 "Provider not found".
- **The row exists whether or not the provider is enabled.** Seeding it is a schema fact; registering a client is a configuration one. A model can therefore be created against a provider this deployment cannot serve — which is the same shape as every other provider, and produces a 503 rather than a 500.
- **The 503 is what an unconfigured provider looks like.** A turn on a Foundry-backed model with `Enabled` off fails with `/problems/provider-not-configured`; see [Anthropic §9](anthropic.md#9-when-the-provider-is-not-configured--the-503-contract). Two resolver tests cover this specific case, including the one that matters most: the sibling Azure provider on the same endpoint must **not** silently absorb the turn.
- **Configuration is read once**, at client construction. Changing any `AzureAIFoundry:*` value needs a restart; there is no `IOptionsMonitor` reload path. `Enabled` in particular is read during service registration, so flipping it needs a restart by construction.
- **MCP tools, streaming, telemetry and token accounting are unchanged.** This provider goes through the same builder chain as the others — `UseOpenTelemetry` → `UseToolTracking` → `UseFunctionInvocation` → the defaults callback — so everything above `IChatClient` behaves identically.

## 7. Key files

| Concern | File |
|---|---|
| Provider ids and the provider → DI-key map | [`Enterprise.Gpt.Dto/Enums/Providers.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/Providers.cs) |
| DI keys (`"azure-ai-foundry"`) | [`Enterprise.Gpt.Dto/Enums/ChatClientKeys.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/ChatClientKeys.cs) |
| Settings | [`Enterprise.Gpt.Service/Settings/AzureAIFoundryOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/AzureAIFoundryOptions.cs) |
| Shared URL handling and endpoint derivation | [`Enterprise.Gpt.Service/Settings/AzureV1EndpointOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/AzureV1EndpointOptions.cs) |
| Per-turn settings (the callback) | [`Enterprise.Gpt.Api/Chat/AzureAIFoundryChatDefaults.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Chat/AzureAIFoundryChatDefaults.cs) |
| Client registration and validation | [`Enterprise.Gpt.Api/Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) |
| Provider row | [`Repository/Configurations/ProviderConfiguration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/ProviderConfiguration.cs), [`Migrations/20260814031023_AddAzureAIFoundryProvider.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260814031023_AddAzureAIFoundryProvider.cs) |
| Settings tests | [`tests/Enterprise.Gpt.Unit.Test/Settings/AzureAIFoundryOptionsTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Settings/AzureAIFoundryOptionsTests.cs) |
| Per-turn callback tests | [`tests/Enterprise.Gpt.Unit.Test/Chat/AzureAIFoundryChatDefaultsTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Chat/AzureAIFoundryChatDefaultsTests.cs) |
| Wire-shape tests against the SDK bridge | [`tests/Enterprise.Gpt.Unit.Test/Chat/AzureAIFoundryChatClientBridgeTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Chat/AzureAIFoundryChatClientBridgeTests.cs) |
| Related reference | [Azure OpenAI](azure-openai.md), [Model Management](model-management.md), [Amazon Bedrock](amazon-bedrock.md), [Anthropic](anthropic.md) |

## 8. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| App refuses to start naming `AzureOpenAI:…` after an upgrade | The *other* provider's section was renamed out of `AzureAIFoundry:*` in this release | Re-key it — [Azure OpenAI §8](azure-openai.md#8-upgrading-from-the-previous-release--the-configuration-rename) |
| App refuses to start: `AzureAIFoundry:ApiKey is required when AzureAIFoundry:Enabled is true.` | The section is half filled | Supply all three settings, or set `Enabled` to `false` ([§3.2](#32-startup-validation)) |
| 503 `/problems/provider-not-configured` on every turn on one model | `AzureAIFoundry:Enabled` is off in this deployment, but a model row points here | Enable the provider, or move the model to another one ([§6](#6-operational-notes)) |
| 404 "Provider not found" when creating a model | The provider row is missing — a database that predates the repository's migrations | Baseline the database so `Migrate()` can apply `20260814031023_AddAzureAIFoundryProvider` ([§6](#6-operational-notes)) |
| The reasoning region is always empty for a model served here | By design — this surface cannot request a reasoning summary | Move the model to the [Azure OpenAI provider](azure-openai.md) ([§4](#4-reasoning-is-stripped-and-that-is-the-design)) |
| The answer contains `<think>…</think>` text | The model reasons inline over Chat Completions; this is response content, not a structured reasoning item | Expected ([§4](#4-reasoning-is-stripped-and-that-is-the-design)). Move the model to Azure OpenAI if the reasoning belongs in its own region |
| 404 from the provider on every turn, naming no setting | The v1 path reached the wire twice | The resource-root validator covers the configured value; check for a proxy rewriting the path |
