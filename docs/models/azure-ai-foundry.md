# Azure AI Foundry Provider

Reference for the provider that serves Enterprise GPT's default models and every document embedding: which API surface each of its two clients talks to and why they differ, how the endpoint is derived from one setting, how a model opts into reasoning summaries, and the two Responses-API behaviours that had to be decided before the swap was safe. Audience: engineers working on the chat path, and operators deploying this build. Companion to [Amazon Bedrock Provider](amazon-bedrock.md) and [Anthropic Provider](anthropic.md).

## 1. Why this exists, and what changed

Azure AI Foundry is the provider this application cannot run without: it serves the default model, and it is the only source of document embeddings. Unlike Bedrock and Anthropic it has **no `Enabled` flag** — a deployment missing its settings is broken, not opted out.

What changed in this release is the API surface the *chat* client talks to. It used to be **Chat Completions**, reached through `AzureOpenAIClient`. It is now the **Responses API**, reached through the plain `OpenAIClient` pointed at Foundry's OpenAI-compatible v1 endpoint:

```csharp
new OpenAIClient(
        new ApiKeyCredential(settings.ApiKey),
        new OpenAIClientOptions { Endpoint = settings.V1Endpoint })
    .GetResponsesClient()
    .AsIChatClient(settings.DefaultModel);
```

**The motivation was a bug users could see.** The assistant's activity timeline has a reasoning region, and on Azure-served turns it never appeared. Nothing in the pipeline was missing: the tracking middleware's `ToUiEventsAsync()` already maps a `TextReasoningContent` fragment to a `ReasoningDelta` event, the stream already carries those frames ([streaming contract §4.1](../conversations/streaming-contract.md#41-event-kinds)), and the web client already renders every delta past the server's seeded one. What was missing was a provider that emits any. **Azure returns reasoning summaries only on the Responses surface** — over Chat Completions it returns none, whatever the deployment — so the region had nothing to draw and no amount of client work would have filled it.

Everything below the `IChatClient` abstraction is untouched by the swap: MCP tool invocation, function calling, OpenTelemetry spans, per-tool token accounting, and SSE streaming all behave as before, and the other two providers are not affected at all.

### 1.1 What did **not** move

The **embedding** client is still `AzureOpenAIClient`, deliberately:

- Embeddings have no Responses equivalent, so the v1 endpoint has nothing to offer them.
- Every vector already stored in `Core.ConversationDocumentChunk` and `Core.ProjectDocumentChunk` was produced by this client. Retrieval compares new query vectors against them ([document retrieval](../documents/retrieval.md)), so a client that embedded even slightly differently would degrade every search silently rather than fail.

It is now built from a factory rather than eagerly, which is a correctness fix rather than a style change — see §4.3.

### 1.2 Both APIs are still marked experimental

| Package | Version | Diagnostic | Where it is suppressed |
|---|---|---|---|
| `OpenAI` | 2.12.0 (transitive) | `OPENAI001` — every Responses request type | The client registration in `Program.cs`, and [`AzureFoundryChatDefaults.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Chat/AzureFoundryChatDefaults.cs) file-wide |
| `Microsoft.Extensions.AI.OpenAI` | 10.8.3 | `MEAI001` — the `AsIChatClient` Responses adapter | The client registration in `Program.cs` |

Both suppressions are **at the call site**, never project-wide, and the SDK's request types are confined to that one file in `Enterprise.Gpt.Api`. That containment is what keeps `Enterprise.Gpt.Service` free of any OpenAI package reference — the same split that keeps AWS types out of it for Bedrock and Anthropic types out of it for Anthropic. It also means a second experimental API cannot slip in behind this one: reaching for `OpenAI.Responses` anywhere else is a build error until someone writes the suppression deliberately.

## 2. Quick start

**1. Configure the four required settings** with user secrets — never `appsettings.json`, which is checked in and carries no `AzureAIFoundry` section at all:

```bash
cd enterprise-gpt-api/Enterprise.Gpt.Api
dotnet user-secrets set "AzureAIFoundry:Url" "https://my-resource.services.ai.azure.com/"
dotnet user-secrets set "AzureAIFoundry:ApiKey" "your-foundry-key"
dotnet user-secrets set "AzureAIFoundry:DefaultModel" "gpt-5.6-luna"
dotnet user-secrets set "AzureAIFoundry:EmbeddingModel" "text-embedding-3-small"
```

`Url` is the **resource root**. Do not include `/openai/v1` — the client appends it (§3.2), and a URL whose path already carries it is rejected at startup.

**2. Start the API.** All seven validators run at boot (§4.2), so a missing or malformed setting refuses to start rather than failing on someone's first message.

**3. Turn reasoning on for a model that supports it** — it is off for every model until you do (§5):

```bash
curl -X PUT https://localhost:7045/api/models/{id} \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
        "providerId": "3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0",
        "name": "RR GPT 5.6 Luna",
        "deploymentName": "rr-gpt-5.6-luna",
        "description": "OpenAI GPT-5.6 Luna, served by Azure AI Foundry.",
        "contextWindowSize": 200000,
        "maxOutputTokens": 16384,
        "isToolEnabled": true,
        "isReasoningEnabled": true,
        "isDefault": true
      }'
```

`PUT` replaces the whole model, so send every field — see [Model Management §2.1](model-management.md#21-wire-shape-contracts).

The next turn on that model streams the model's own reasoning summary, and the client's reasoning region fills as it arrives.

## 3. Core concepts

### 3.1 A provider id selects a chat client

Unchanged in shape from the other two providers:

| Piece | Where | What it holds for Foundry |
|---|---|---|
| Provider id | [`Providers`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/Providers.cs) | `AzureOpenAI` = `3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0` |
| DI key | [`ChatClientKeys`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/ChatClientKeys.cs) | `"azureaifoundry"` |
| The map between them | `Providers.ServiceKeys` | provider id → DI key |

The row is named `AzureOpenAI` in `Core.Ref.Provider` and is seeded both by `HasData` and by the `InitialCreate` migration, so it is present in any database this application built. [`ChatClientResolver`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Chat/ChatClientResolver.cs) turns the id into the keyed `IChatClient` at request time; see [Amazon Bedrock §3.1](amazon-bedrock.md#31-a-provider-id-selects-a-chat-client) for the resolver itself.

### 3.2 The v1 endpoint is derived, not configured

`AzureAIFoundryOptions.V1Endpoint` appends `openai/v1/` to the configured resource root:

| `AzureAIFoundry:Url` | `V1Endpoint` |
|---|---|
| `https://my-resource.services.ai.azure.com/` | `https://my-resource.services.ai.azure.com/openai/v1/` |
| `https://my-resource.services.ai.azure.com` (no trailing slash) | `https://my-resource.services.ai.azure.com/openai/v1/` |
| `https://my-resource.openai.azure.com/` | `https://my-resource.openai.azure.com/openai/v1/` |

Three things are worth knowing about that derivation:

- **It is derived rather than added as a second setting** so an existing deployment needs no configuration change to take this release. The same `Url` also still builds the embedding client, against the resource root (§4.3).
- **Azure accepts both hosts.** `services.ai.azure.com` and `openai.azure.com` both serve the OpenAI-compatible API, so either form works and neither is preferred here.
- **The trailing slash on the path constant is load-bearing.** The SDK resolves relative request paths against this URI; without it the last segment is replaced instead of extended. `EnsureTrailingSlash` covers the resource root copied out of the portal, which usually lacks one.

The v1 endpoint is OpenAI-compatible, which is what lets the plain `OpenAIClient` talk to Foundry at all. It takes the **deployment name** in the request's `model` field — the same value `Model.DeploymentName` has always supplied through `ChatOptions.ModelId`, so nothing about the catalog changes.

### 3.3 Where the per-turn settings are applied

The keyed client is built with the same builder chain as the other providers, plus one link:

```text
UseOpenTelemetry(named source)      ← outermost
  └─ UseToolTracking
       └─ UseFunctionInvocation
            └─ ConfigureOptionsChatClient(AzureFoundryChatDefaults.Create(settings))   ← innermost
                 └─ the Responses adapter
```

`ChatClientBuilder` applies its factories in reverse, so the link registered **last** is **innermost** — closest to the SDK, and the one whose `ChatOptions` the adapter actually reads. Two consequences follow from that position and both are relied on in §6:

- `UseOpenTelemetry` has already recorded `gen_ai.conversation.id` by the time the callback clears `ConversationId`, so the telemetry keeps the conversation correlation the wire deliberately loses.
- `FunctionInvokingChatClient` sits **above** the callback, which is why the two Responses behaviours in §6 are coupled rather than independent.

[`AzureFoundryChatDefaults`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Chat/AzureFoundryChatDefaults.cs) is kept out of `Program.cs` for the same reason as its Anthropic counterpart: confining the SDK's request types to one file keeps them out of the provider-agnostic `ChatOptions` that `ConversationService` builds.

## 4. Configuration

### 4.1 Settings

The `AzureAIFoundry` section binds to [`AzureAIFoundryOptions`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/AzureAIFoundryOptions.cs). None of it ships in `appsettings.json`, so the two reasoning settings run on the type's own defaults unless a deployment overrides them.

| Setting | Default | Required | What it does |
|---|---|---|---|
| `Url` | — | yes | The Foundry resource root, e.g. `https://my-resource.services.ai.azure.com/`. **Not** the v1 endpoint (§3.2). |
| `ApiKey` | — | yes | The resource key. **Secrets store only** — user secrets locally, Key Vault when deployed. |
| `DefaultModel` | — | yes | The deployment the SDK is constructed with. Every turn sets the model explicitly from the catalog, so this is only the fallback the client needs to exist. |
| `EmbeddingModel` | — | yes | The embedding deployment. It must natively return 1536-dimension vectors, because the stored column is fixed at that width, and it selects the chunker's tokenizer. Changing it invalidates every stored embedding. |
| `ReasoningSummary` | `auto` | no | How verbose a summary to ask for: `auto`, `concise`, `detailed`, or `none` to ask for none at all (§5.2). |
| `ReasoningEffort` | `medium` | no | How much the model reasons: `minimal`, `low`, `medium`, `high` (§5.2). |

Both reasoning settings are **deployment-wide** and are read once, when the client is built. A model row carries no override, and changing either needs a restart.

### 4.2 Startup validation

This was the last provider read with raw `GetValue<string>` calls, and the cost of that was concrete: a blank `AzureAIFoundry:Url` became `new Uri("")` inside a lazily-built client factory, so the deployment started cleanly and threw `UriFormatException` on someone's first conversation. All seven predicates now run under `ValidateOnStart()`, and — unlike Bedrock and Anthropic — none of them is gated on an `Enabled` flag, because there is none.

| Rule | Message |
|---|---|
| `Url` is an absolute http/https URI | `AzureAIFoundry:Url must be an absolute http or https URI, for example https://my-resource.services.ai.azure.com/.` |
| `Url`'s **path** does not contain `openai` | `AzureAIFoundry:Url must be the resource root, without /openai/v1 — the client appends it.` |
| `ApiKey` is non-blank | `AzureAIFoundry:ApiKey is required.` |
| `DefaultModel` is non-blank | `AzureAIFoundry:DefaultModel is required.` |
| `EmbeddingModel` is non-blank | `AzureAIFoundry:EmbeddingModel is required.` |
| `ReasoningSummary` is a known value | `AzureAIFoundry:ReasoningSummary must be one of auto, concise, detailed, none.` |
| `ReasoningEffort` is a known value | `AzureAIFoundry:ReasoningEffort must be one of minimal, low, medium, high.` |

Three of them deserve a note:

- **The resource-root check tests the path, not the whole string.** `https://my-resource.openai.azure.com/` is a perfectly correct value whose *host* contains `openai`; only the path may not. It also rejects a bare `/openai`, because half the path repeated produces the same unexplained 404 as all of it — a request to `/openai/v1/openai/v1/responses`, whose error names neither the setting nor the cause.
- **The two reasoning checks null-guard before the lookup.** The accepted-value sets compare case-insensitively and that comparer throws on a null rather than reporting a miss, which would surface as a startup crash instead of the message above.
- **The vocabularies are shared with their tests.** [`AzureFoundryChatDefaultsTests`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Chat/AzureFoundryChatDefaultsTests.cs) drives every value the validators accept through the parsers, so a value added to a set without a matching parser arm fails there rather than on the first conversation — which matters because the keyed client is built lazily, and a parse failure would otherwise surface mid-turn rather than at boot.

None of the messages echoes the key.

### 4.3 The embedding client is built from a factory

`AddEmbeddingGenerator` now takes a factory that resolves `IOptions<AzureAIFoundryOptions>`, rather than constructing the client inline while the service collection is being built. That is not tidying: constructing it inline runs `new Uri(settings.Url)` *before* `Build()`, and therefore before `ValidateOnStart` — so a blank URL surfaced as a bare `UriFormatException` from that line instead of as the validator's message. The factory defers construction until the validated options exist.

## 5. Reasoning

### 5.1 The gate is a catalog column, and it is opt-in

`Model.IsReasoningEnabled` (`bit NOT NULL`, default `0`) says whether a deployment is asked for reasoning on every turn. It is exposed on `ModelDto` and both model action DTOs as `isReasoningEnabled`, admin-editable like the rest of the catalog — see [Model Management §2.1](model-management.md#21-wire-shape-contracts).

**It defaults to false and existing rows are not back-filled**, because the failure mode is total rather than graceful: a deployment that does not support reasoning **rejects the whole request** instead of ignoring the option. Guessing "yes" for a model that turns out not to reason breaks every turn on it; guessing "no" costs a UI region nobody had before. It also costs money when on — reasoning tokens are billed as output tokens.

The flag reaches the client on `ChatOptions.AdditionalProperties`, under the constant `ChatRequestProperties.IsReasoningEnabled` (`enterprisegpt.reasoning.enabled`):

```text
ConversationService                          AzureFoundryChatDefaults
(provider-agnostic, no SDK reference)        (the only place SDK types live)
        │                                              ▲
        └── AdditionalProperties[key] = model.IsReasoningEnabled ──┘
```

That channel exists because the two ends cannot see each other's types: everything that decides what a turn asks for is in `Enterprise.Gpt.Service`, which takes no provider SDK reference, and everything that knows how to say it lives beside the client registration in `Enterprise.Gpt.Api`. The key is spelled once, in [`ChatRequestProperties`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/ChatRequestProperties.cs), so neither side can misspell it.

Two details about the channel:

- **It is written on every turn, including when false**, so the reading end can tell "this model does not reason" from "nobody said".
- **Absent means no.** The reader requires the value to be `true`, so a request assembled anywhere else cannot turn a stray value into a reasoning request the deployment then rejects.

> **Known limitation — the column is Azure-only.** Only this provider's client reads the property. The Bedrock and Anthropic clients take their reasoning settings from their own configuration ([Anthropic §5](anthropic.md#5-thinking-and-effort)), so setting `isReasoningEnabled` on a model served by either changes nothing. It is a catalog-wide column with a provider-specific effect, and closing that gap means teaching those two clients to read it.

### 5.2 Summary verbosity and effort

Both are deployment-wide settings, parsed once when the client is built (§4.1) and applied to every reasoning-enabled model.

| `ReasoningSummary` | Effect |
|---|---|
| `auto` (default) | The service picks the summary detail. |
| `concise` | A short summary. |
| `detailed` | A fuller one. |
| `none` | **Application-level, not the API's.** No summary is requested at all — the property is left off the request rather than sent with a "none" value, which is how a request declines a summary while still asking the model to reason. This is how a deployment turns summaries off globally without editing every model row. |

| `ReasoningEffort` | Effect |
|---|---|
| `minimal` / `low` | Least reasoning; fastest and cheapest. |
| `medium` (default) | The balance. |
| `high` | Most reasoning — deeper answers, more latency, more billed output tokens. |

An unknown value in either setting throws `ArgumentOutOfRangeException` from the parser. The validators reject those first, so that throw is unreachable in practice; it exists to make the two vocabularies drifting apart a loud failure rather than a silent default.

### 5.3 What a reasoning turn puts on the wire

For a model whose row sets the flag, the callback supplies a raw `CreateResponseOptions` carrying:

- `ReasoningOptions` with the configured effort, and the configured summary verbosity unless it is `none`.
- `IncludedProperties += ReasoningEncryptedContent`. **Stateless mode requires this** (§6.2): with nothing stored service-side, a reasoning item replayed on a later tool iteration is one the service cannot look up, so it has to travel with the request. Without it, a reasoning model rejects its own replayed reasoning item the moment a turn calls a tool.

For every other model the request carries **no reasoning options at all** — not "reasoning switched off", which is a different thing and not what the API offers.

What the user sees is documented on the client side of the contract: reasoning arrives as `ReasoningDelta` frames, is display-only, and is never written to the transcript. See [streaming contract §4.1](../conversations/streaming-contract.md#41-event-kinds).

## 6. Two Responses-API behaviours the swap forced decisions about

These are the two things that are not visible in the diff and that anyone touching this client needs to know. They are also **coupled** — §6.3 — so changing one without the other breaks tool calling.

### 6.1 `ChatOptions.ConversationId` is now a wire parameter

Under Chat Completions this field was inert. The Responses adapter reads it: `OpenAIClientExtensions.IsConversationId` treats an id beginning with `conv_` as a service-side conversation and **anything else as a response id**, which it sends as `previous_response_id`.

`ConversationService` sets `ConversationId` to the conversation's own GUID — it is this application's identifier, and no Azure resource has ever seen it. Left alone, every turn would ask the service to continue a response it never issued.

So the callback clears it: `options.ConversationId = null`. Being innermost is what makes that safe for observability (§3.3) — the telemetry client above has already recorded the id.

### 6.2 The Responses API stores prompts and answers for 30 days by default

Chat Completions stored nothing. The Responses API retains request and response content service-side for 30 days unless told otherwise, so taking the default would have changed this application's privacy posture as a side effect of an unrelated fix.

Every turn is therefore sent with `StoredOutputEnabled = false`. That is a deliberate decision, not an omission: a turn already carries the user's prompt, the project's standing instructions and any retrieved document passages, and the conversation's own transcript in Cosmos DB is the system of record for what was said ([what a turn records](../conversations/usage-and-favorites.md)). Nothing is gained by a second copy in a service store, and the copy would be one nobody in this application can enumerate or delete.

### 6.3 Why the two are coupled

`FunctionInvokingChatClient` sits **above** the callback in the chain (§3.3), and it changes strategy based on what the service hands back:

1. With storage **on**, the service returns its own response id, which the adapter surfaces as the response's `ConversationId`.
2. The function-invoking client reads that as *"the service is tracking history"*, **drops the local messages**, and sends only the tool result on the next iteration, expecting the service to continue from the id.
3. Clearing the id (§6.1) removes exactly what that next request needs. It would send a bare `function_call_output` with nothing to attach it to.

So clearing `ConversationId` **without** disabling storage breaks every tool-calling turn from its second iteration — which is to say every turn that calls an MCP server, searches a document, or runs an agent. Disabling storage is what keeps the whole exchange in each request, and that is what makes the clear safe.

[`AzureFoundryChatClientBridgeTests`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Chat/AzureFoundryChatClientBridgeTests.cs) pins this against a stub transport by driving **two** function-invocation iterations and asserting the second request resends the prompt and the call, not just the orphaned result. The rest of that class pins the wire shape the isolated tests cannot see: `previous_response_id` absent, `store: false`, `reasoning` present only for a flagged model, and the catalog property itself never leaking into the request body.

## 7. Adding a Foundry-backed model to the catalog

Model CRUD is administrator-only and otherwise unchanged; see [Model Management](model-management.md). What is Foundry-specific:

- **`providerId`** is `3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0` — seeded, so unlike the other two providers there is no row to insert by hand.
- **`deploymentName`** is the **Azure deployment name**, not a model family name — it is what goes into the request's `model` field on the v1 endpoint.
- **`isReasoningEnabled`** should be `true` only for a deployment that actually supports reasoning (§5.1).
- **`isToolEnabled`** still governs whether MCP tools and document retrieval are attached at all.

## 8. Operational notes

- **No configuration change is needed to take this release**, unless `AzureAIFoundry:Url` was set to something other than the resource root. A URL carrying `/openai` or `/openai/v1` in its path now fails at startup with a message naming the setting.
- **Configuration is read once**, at client construction. Changing any `AzureAIFoundry:*` value needs a restart; there is no `IOptionsMonitor` reload path.
- **The keyed chat client is built lazily**, on first resolution — so anything the validators do not cover surfaces on the first Foundry conversation rather than at boot. That is why the accepted-value sets are shared between the validators and their tests (§4.2).
- **The catalog column ships as a real migration.** `20260813233326_AddModelIsReasoningEnabled` adds `Core.Ref.Model.IsReasoningEnabled` as `bit NOT NULL DEFAULT 0`, and `Database.Migrate()` at startup applies it outside the `Testing` environment. It is additive and back-compatible: an older build ignores the column.
- **Embeddings are unaffected** by everything above (§1.1). A turn's chat traffic goes to `…/openai/v1/`, its embedding traffic to the resource root, both authenticated with the same key.
- **The 503 contract is unchanged.** A model whose provider has no client still fails with `/problems/provider-not-configured`; see [Anthropic §9](anthropic.md#9-when-the-provider-is-not-configured--the-503-contract). For this provider it is close to unreachable, since the client is always registered.

> **The gate is enforced twice, and the second time is not redundant.** `ConversationService` also sets `ChatOptions.Reasoning` — Microsoft.Extensions.AI's own provider-agnostic reasoning request, and the only reason the catalog flag means anything to the Bedrock and Anthropic bridges. The Responses adapter applies it with `result.ReasoningOptions ??= …`, so it fills in exactly the raw options this path deliberately leaves empty: set unconditionally, it would put a `reasoning` block on the wire for a deployment that rejects the whole request for one. So it is written only alongside the catalog flag, **and** the callback clears it when the flag is off. Two enforcement points because they fail differently — the caller's is the correct default, the callback's is what holds when a future call site forgets. A bridge test sets `ChatOptions.Reasoning` on a non-reasoning turn and asserts nothing reaches the wire.

## 9. Key files

| Concern | File |
|---|---|
| Provider ids and the provider → DI-key map | [`Enterprise.Gpt.Dto/Enums/Providers.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/Providers.cs) |
| DI keys | [`Enterprise.Gpt.Dto/Enums/ChatClientKeys.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/ChatClientKeys.cs) |
| Settings, endpoint derivation, accepted values | [`Enterprise.Gpt.Service/Settings/AzureAIFoundryOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/AzureAIFoundryOptions.cs) |
| Per-turn Responses settings (the callback) | [`Enterprise.Gpt.Api/Chat/AzureFoundryChatDefaults.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Chat/AzureFoundryChatDefaults.cs) |
| The catalog → client channel | [`Enterprise.Gpt.Dto/Enums/ChatRequestProperties.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/ChatRequestProperties.cs) |
| Client registration, validation, builder chain, embeddings | [`Enterprise.Gpt.Api/Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) |
| Where the flag is put on the request | [`Enterprise.Gpt.Service/ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs) |
| Catalog column | [`Enterprise.Gpt.Entity/Model.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Entity/Model.cs), [`Migrations/20260813233326_AddModelIsReasoningEnabled.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260813233326_AddModelIsReasoningEnabled.cs) |
| Settings and endpoint tests | [`tests/Enterprise.Gpt.Unit.Test/Settings/AzureAIFoundryOptionsTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Settings/AzureAIFoundryOptionsTests.cs) |
| Per-turn callback tests | [`tests/Enterprise.Gpt.Unit.Test/Chat/AzureFoundryChatDefaultsTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Chat/AzureFoundryChatDefaultsTests.cs) |
| Wire-shape tests against the SDK bridge | [`tests/Enterprise.Gpt.Unit.Test/Chat/AzureFoundryChatClientBridgeTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Chat/AzureFoundryChatClientBridgeTests.cs) |
| Related reference | [Model Management](model-management.md), [Conversation Streaming Contract](../conversations/streaming-contract.md), [Amazon Bedrock](amazon-bedrock.md), [Anthropic](anthropic.md) |

## 10. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| App refuses to start: `AzureAIFoundry:Url must be the resource root…` | The configured URL already carries `/openai` or `/openai/v1` | Set it to the resource root; the client appends the path (§3.2) |
| App refuses to start: `AzureAIFoundry:Url must be an absolute http or https URI…` | Blank, relative, or a non-HTTP scheme | Supply the full `https://…` root. This used to be a `UriFormatException` on the first conversation (§4.2) |
| App refuses to start: `AzureAIFoundry:ReasoningEffort must be one of…` | A value from another provider's vocabulary — Anthropic's `xhigh`/`max` are not accepted here | Use `minimal`, `low`, `medium`, or `high` (§5.2) |
| 404 from the provider on every turn, naming no setting | The v1 path reached the wire twice | The resource-root validator covers the configured value; check for a proxy rewriting the path (§3.2) |
| The reasoning region never appears for a model | `isReasoningEnabled` is `false` on its catalog row, or `ReasoningSummary` is `none` | Set the flag (§5.1); check the setting (§5.2) |
| The provider rejects every turn on a model, with reasoning requested | The deployment does not support reasoning, but the flag is set — or the known issue in §8 is in play | Clear `isReasoningEnabled`; see §8 |
| Every tool-calling turn fails from its second iteration | `StoredOutputEnabled` was turned on, or the `ConversationId` clear was removed, without the other | Keep both, or neither (§6.3) |
| A reasoning model fails as soon as a turn calls a tool | `ReasoningEncryptedContent` is not being included | It is required by stateless mode (§5.3) |
| Document search quality drops after a configuration change | `EmbeddingModel` was changed | Re-ingest every document; stored vectors are only comparable to vectors from the same deployment (§4.1) |
| Turns work but no LLM spans reach Application Insights | The telemetry source name drifted from `TelemetryRegistration.ChatTelemetrySourceName` | Both ends must register the same name (§3.3) |
