# Providers

Four keyed `IChatClient` registrations. A catalog row names a provider; `ChatClientResolver` turns
that provider id into the client that serves the turn.

## The four

| | Azure OpenAI | Azure AI Foundry | Amazon Bedrock | Anthropic |
| --- | --- | --- | --- | --- |
| Registered when | **always** | `AzureAIFoundry:Enabled` | `AmazonBedrock:Enabled` | `Anthropic:Enabled` |
| API surface | Responses | Chat Completions | Bedrock Runtime | Messages |
| Config section | `AzureOpenAI` | `AzureAIFoundry` | `AmazonBedrock` | `Anthropic` |
| DI key | `azure-open-ai` | `azureaifoundry` | `amazonbedrock` | `anthropic` |
| Reasoning | Yes, per model row | **Stripped** | Own configuration | Own configuration ("thinking") |
| Embeddings | **Yes — owns the generator** | no | no | no |

Azure OpenAI is required: it serves the default model *and* every document embedding. The other
three are optional and skipped entirely when their `:Enabled` flag is false.

A model whose provider has no registered client fails the turn with 503
`/problems/provider-not-configured` — an operator condition, not a caller mistake.

## How a provider id becomes a client

| Piece | Where |
| --- | --- |
| Provider id (a guid) | `Enterprise.Gpt.Dto/Enums/Providers.cs` |
| DI key | `Enterprise.Gpt.Dto/Enums/ChatClientKeys.cs` |
| The map between them | `Providers.ServiceKeys` |

Provider rows are seeded in `Core.Ref.Provider` by `HasData`, which the `Initial` migration emits,
so they are present in any database this application built. `ChatClientResolver` resolves the keyed client at
request time.

`ChatClientKeys.FileAgent` is in that key catalog but is **not** a provider key — no provider row
maps to it and `ServiceKeys` never resolves it. See [../tools/file-agent.md](../tools/file-agent.md).

## The two Azure providers

They reach the **same resource over the same v1 endpoint with the same kind of credential**, and
differ only in API surface.

An Azure AI Foundry resource hosts more than Azure OpenAI deployments — it also serves Foundry
Models such as DeepSeek, Llama, Mistral and Grok, which do not all implement the Responses API. A
deployment that does not support Responses answers `400 Model not supported` rather than degrading.
So the second registration exists to let the catalog carry both kinds of deployment at once.

`AzureAIFoundry` is its own configuration section rather than a mode switch, for two reasons:
credentials differ in practice (commonly different resources, or the same resource with different
keys), and the choice belongs in the catalog — a model row selects its surface by naming a provider,
so an administrator changes it per model rather than for every model at once.

Foundry's registration uses `GetChatClient(...).AsIChatClient()`, which is **stable and
unattributed**, where the Responses equivalents are still `[Experimental]`. That is a real benefit of
the split: the experimental surface in the solution is exactly the Responses path and nothing else.

### The v1 endpoint is derived, not configured

`AzureV1EndpointOptions.V1Endpoint` appends `openai/v1/` to the configured resource root, shared by
both Azure providers so the rule is written once.

| `AzureOpenAI:Url` | `V1Endpoint` |
| --- | --- |
| `https://my-resource.services.ai.azure.com/` | `https://my-resource.services.ai.azure.com/openai/v1/` |
| `https://my-resource.services.ai.azure.com` | `https://my-resource.services.ai.azure.com/openai/v1/` |
| `https://my-resource.openai.azure.com/` | `https://my-resource.openai.azure.com/openai/v1/` |

- Derived rather than a second setting, so an existing deployment needs no new key. The same `Url`
  also builds the embedding client.
- Azure accepts both hosts; neither is preferred.
- **The trailing slash on the path constant is load-bearing** — the SDK resolves relative request
  paths against this URI, and without it the last segment is replaced instead of extended.
  `EnsureTrailingSlash` covers the resource root copied out of the portal, which usually lacks one.

Configure `AzureOpenAI:Url` as the resource **root**. A URL that already carries `/openai/v1` fails
at startup.

The v1 endpoint is OpenAI-compatible, which is what lets a plain `OpenAIClient` talk to Azure. It
takes the **deployment name** in the request's `model` field — the value `Model.DeploymentName`
already supplies through `ChatOptions.ModelId` — and uses implicit versioning, so there is no
`api-version` to maintain.

## The builder chain

Every keyed client is built the same way:

```text
UseOpenTelemetry(named source)          <- outermost
  UseToolTracking
    UseFunctionInvocation
      ConfigureOptionsChatClient(<Provider>ChatDefaults.Create(settings))   <- innermost
        the provider adapter
```

`ChatClientBuilder` applies its factories in reverse, so the link registered **last** is
**innermost** — closest to the SDK, and the one whose `ChatOptions` the adapter actually reads. Two
consequences are relied on:

- `UseOpenTelemetry` has already recorded `gen_ai.conversation.id` by the time the callback clears
  `ConversationId`, so telemetry keeps a correlation the wire deliberately loses.
- `FunctionInvokingChatClient` sits **above** the callback.

Each provider's `*ChatDefaults` class is kept out of `Program.cs` on purpose: confining the SDK's
request types to one file keeps them out of the provider-agnostic `ChatOptions` that
`ConversationService` builds.

## Reasoning

`Model.IsReasoningEnabled` (`bit NOT NULL`, default `0`) says whether a deployment is asked for
reasoning on every turn. It is admin-editable like the rest of the catalog.

**It defaults to false and existing rows are not backfilled**, because the failure mode is total
rather than graceful: a deployment that does not support reasoning **rejects the whole request**
rather than ignoring the option. Guessing "yes" breaks every turn on that model; guessing "no" costs
a UI region nobody had before. Reasoning tokens are also billed as output tokens.

The flag reaches the client on `ChatOptions.AdditionalProperties` under the constant
`ChatRequestProperties.IsReasoningEnabled`. That channel exists because the two ends cannot see each
other's types: everything deciding what a turn asks for lives in `Enterprise.Gpt.Service`, which
takes no provider SDK reference, and everything knowing how to say it lives beside the client
registration in `Enterprise.Gpt.Api`. The key is spelled once so neither side can misspell it.

- **It is written on every turn, including when false**, so the reading end can distinguish "this
  model does not reason" from "nobody said".
- **Absent means no.** The reader requires `true`, so a request assembled elsewhere cannot turn a
  stray value into a reasoning request the deployment then rejects.

**Exactly one provider reads the column.** Bedrock and Anthropic take their reasoning settings from
their own configuration, and Azure AI Foundry has no reasoning surface and strips the request
instead. Setting `isReasoningEnabled` on a model served by any of the three changes nothing — it is
a catalog-wide column with a provider-specific effect.

### Azure OpenAI summary and effort

Deployment-wide, parsed once when the client is built, applied to every reasoning-enabled model.

| `ReasoningSummary` | Effect |
| --- | --- |
| `auto` (default) | The service picks the detail level |
| `concise` / `detailed` | A short or fuller summary |
| `none` | **Application-level, not the API's** — the property is left off the request entirely, which is how a request declines a summary while still asking the model to reason |

`ReasoningEffort` is `minimal`, `low`, `medium` (default) or `high`.

## Configuration

Every provider section carries `Enabled` (except `AzureOpenAI`), an endpoint or region, and a
credential. Set credentials through user secrets or Key Vault — never `appsettings.json`, which is
checked in and ships these sections blank.

```bash
cd enterprise-gpt-api/Enterprise.Gpt.Api
dotnet user-secrets set "AzureOpenAI:Url" "https://my-resource.services.ai.azure.com"
dotnet user-secrets set "AzureOpenAI:ApiKey" "<key>"
dotnet user-secrets set "AzureAIFoundry:Enabled" "true"
```

See [../operations/configuration.md](../operations/configuration.md) for the full key list.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs` | The four keyed registrations and their builder chains |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Chat/` | Per-provider `ChatOptions` defaults |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Chat/ChatClientResolver.cs` | Provider id to keyed client |
| `enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/Providers.cs` | Provider ids and the DI-key map |
| `enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/ChatRequestProperties.cs` | The reasoning channel's key |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Chat/` | Resolver and defaults tests |

## Related

- [catalog.md](catalog.md)
- [../conversations/streaming.md](../conversations/streaming.md)
- [../operations/configuration.md](../operations/configuration.md)
