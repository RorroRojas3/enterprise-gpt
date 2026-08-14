# Anthropic Provider

Reference for running Enterprise GPT against Anthropic's own API alongside the Azure providers ([Azure OpenAI](azure-openai.md), [Azure AI Foundry](azure-ai-foundry.md)) and Amazon Bedrock: when to pick Anthropic over Bedrock for the same Claude models, how to configure and validate the provider, how thinking and effort are applied to every turn, and the one out-of-band database change this feature depends on. Audience: engineers wiring up a third provider, and operators deploying this build.

## 1. Why this exists

Amazon Bedrock ([its own reference](amazon-bedrock.md)) already brings Claude into the catalog. This adds Anthropic as a **first-party** provider — the same models, reached directly rather than through a cloud reseller.

They are separate providers, with separate ids, because a model row has to name which one serves it. The four differences that matter:

| | Anthropic (this provider) | Amazon Bedrock |
|---|---|---|
| **Billing** | Anthropic account, at Anthropic's published API rates | AWS bill, at AWS's own partner pricing |
| **Model availability** | New Claude models on the day Anthropic ships them; no per-region access request | Lags first-party, and access is granted **per region** under **Model access** in the AWS console |
| **Credential shape** | One API key. No region, no auth-scheme pinning, no ambient-credential hazard (§6.2) | A long-term Bedrock API key presented as an HTTP bearer token, plus a region, plus explicit auth-scheme wiring |
| **`DeploymentName`** | A **bare** model id: `claude-opus-5` | A prefixed inference-profile id or ARN: `us.anthropic.claude-sonnet-4-5-20250929-v1:0` |

There is a fifth, smaller difference that shows up in answers rather than in configuration: this provider stamps Anthropic's **thinking and effort** settings onto every request (§5). The Bedrock client sends neither, so a Bedrock-served Claude model reasons at whatever depth the model defaults to.

**Which to pick.** Choose Anthropic when you want first-party billing, day-one access to new models, and explicit control over reasoning depth. Choose Bedrock when spend and compliance already run through AWS, or when you need a specific AWS region or a cross-region inference profile for capacity or data residency. They are not exclusive — enable both, and each model row routes to the provider it names.

Everything below the `IChatClient` abstraction is untouched: MCP tool invocation, OpenTelemetry, token accounting, and SSE streaming work exactly as they do for the other two providers.

## 2. Quick start

Four steps to a working Anthropic-backed model locally. Step 3 is the one that fails silently if you skip it; §10.1 explains why.

**1. Create an API key** in the [Anthropic Console](https://console.anthropic.com/) (Settings → API keys). Copy it once — it is not shown again.

**2. Configure the API** with user secrets — never `appsettings.json`, which is checked in:

```bash
cd enterprise-gpt-api/Enterprise.Gpt.Api
dotnet user-secrets set "Anthropic:Enabled" "true"
dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-your-api-key"
dotnet user-secrets set "Anthropic:DefaultModelId" "claude-opus-5"
```

`DefaultMaxOutputTokens`, `Thinking`, and `Effort` already carry usable defaults in `appsettings.json` (§4.1). Read §4.1 on `DefaultMaxOutputTokens` before your first long answer — it is the ceiling on every turn, not a fallback.

**3. Insert the Anthropic provider row** — it is seeded through `HasData`, which only reaches databases built by `EnsureCreated()`:

```sql
INSERT INTO [Core.Ref].[Provider] ([Id], [Name], [DateCreated])
VALUES ('6F93EC17-E981-409F-A523-700584F1E7D6', 'Anthropic', SYSDATETIMEOFFSET());
```

**4. Add a model to the catalog** as an administrator:

```bash
curl -X POST https://localhost:7045/api/models \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
        "providerId": "6f93ec17-e981-409f-a523-700584f1e7d6",
        "name": "Claude Opus 5",
        "deploymentName": "claude-opus-5",
        "description": "Anthropic Claude Opus 5, served by Anthropic.",
        "contextWindowSize": 1000000,
        "maxOutputTokens": 128000,
        "isToolEnabled": true,
        "isDefault": false
      }'
```

The model appears in `GET /api/models` and the frontend picker immediately; the next conversation that selects it routes to Anthropic.

## 3. Core concepts

### 3.1 A provider id selects a chat client

Three pieces do the routing, unchanged in shape from the Bedrock work — this release only adds a row to each:

| Piece | Where | What it holds for Anthropic |
|---|---|---|
| Provider ids | [`Providers`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/Providers.cs) | `Anthropic` = `6f93ec17-e981-409f-a523-700584f1e7d6` |
| DI keys | [`ChatClientKeys`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/ChatClientKeys.cs) | `"anthropic"` |
| The map between them | `Providers.ServiceKeys` (a `FrozenDictionary<Guid, string>`) | provider id → DI key |

[`ChatClientResolver`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Chat/ChatClientResolver.cs) walks that map at request time and pulls the keyed `IChatClient` out of the root container; a provider that is absent from the map, or whose client is switched off by configuration, is one condition as far as the caller is concerned — see [Amazon Bedrock §3.1](amazon-bedrock.md#31-a-provider-id-selects-a-chat-client) for the resolver itself.

Two details are Anthropic-specific:

- **Both `Resolve` call sites matter.** `ConversationService` resolves once for the streaming turn and once for the **conversation-naming turn**, which runs inline on a conversation's first turn. The naming turn therefore also runs on Anthropic, with the same thinking and effort settings and the same output ceiling (§10.2).
- **Anthropic and Bedrock both serve Claude**, so a copy-paste slip that pointed the Anthropic provider id at the Bedrock DI key would still produce plausible answers. [`ChatClientResolverTests`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Services/ChatClientResolverTests.cs) asserts the resolved client is the Anthropic one **and specifically not** the Bedrock one, which is what catches that.

### 3.2 `DeploymentName` carries a bare Anthropic model id

`Model.DeploymentName` is the identifier the provider understands, sent as `ChatOptions.ModelId`. On Anthropic it is the plain model id, and the two adornments that look plausible are both wrong:

| Form | Example | Verdict |
|---|---|---|
| Bare model id | `claude-opus-5` | **Correct.** |
| `anthropic.`-prefixed | `anthropic.claude-opus-5` | Wrong — that prefix is Bedrock's. |
| Date-suffixed | `claude-opus-5-20260101` | Wrong — current Anthropic ids carry no date suffix. |

The prefixed form is the mistake the neighbouring Bedrock section invites, so a **startup validator rejects it** on `Anthropic:DefaultModelId` (§4.2) rather than letting it surface as a 404 on the first turn. That validator only covers the configured default; a prefixed id typed into a *model row* is not caught until the model is used, and fails as a provider-side error on that turn.

### 3.3 One package, no adapter

Unlike Bedrock — which needs a runtime client plus a separate `Microsoft.Extensions.AI` adapter — Anthropic needs exactly one package reference, in [`Enterprise.Gpt.Api.csproj`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Enterprise.Gpt.Api.csproj) only:

| Package | Version | Licence | Why |
|---|---|---|---|
| `Anthropic` | 12.40.0 | MIT | The official Anthropic .NET SDK. It carries the `Microsoft.Extensions.AI` bridge itself (`IAnthropicClient.AsIChatClient`), so there is no separate adapter package to reference. |

The service layer takes **no** Anthropic package reference. [`AnthropicOptions`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/AnthropicOptions.cs) is deliberately free of SDK types — `Thinking` and `Effort` are carried as strings and turned into SDK types where the client is registered — the same split that keeps AWS types out of that project for `AmazonBedrockOptions`.

### 3.4 MCP tools and the rest of the pipeline are unchanged

The Anthropic chat client is built with the same `.UseOpenTelemetry()` and `.UseFunctionInvocation(null, ConfigureFunctionInvocation)` chain as the other two providers, sharing one configuration callback in [`Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) so tool behaviour cannot drift between them. Whether a given model supports tools at all is still yours to declare: set `isToolEnabled` on the catalog entry.

One extra link sits at the end of that chain, and its position is load-bearing — see §5.4.

## 4. Configuration

### 4.1 Settings

The `Anthropic` section binds to [`AnthropicOptions`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/AnthropicOptions.cs).

| Setting | Default | Required | What it does |
|---|---|---|---|
| `Enabled` | `false` | — | Registers the Anthropic client and the `"anthropic"` chat client. Everything else in the section is ignored while this is `false`. |
| `ApiKey` | — | when enabled | The Anthropic API key. **Secrets store only** — user secrets locally, Key Vault when deployed. Never `appsettings.json`, which is why the shipped file has every other key but not this one. |
| `DefaultModelId` | `claude-opus-5` | when enabled | The model id used when a request carries none. Bare id, no `anthropic.` prefix, no date suffix (§3.2). |
| `DefaultMaxOutputTokens` | `32768` | when enabled | The output token ceiling. **See the warning below — this is the ceiling on every turn, not a fallback.** |
| `Thinking` | `adaptive` | when enabled | `adaptive` or `disabled`. Compared case-insensitively. §5. |
| `Effort` | `high` | when enabled | `low`, `medium`, `high`, `xhigh`, or `max`. Compared case-insensitively. §5. |
| `Timeout` | 10 minutes | when enabled | How long a single request may run before the SDK abandons it. The SDK's own default. |
| `MaxRetries` | `2` | when enabled | How many times the SDK retries a failed request — connection errors and 408, 409, 429, and 5xx responses, with exponential backoff. |

> **`DefaultMaxOutputTokens` is the operative limit on every Anthropic turn, not a fallback.** `ConversationService` never puts the catalog model's `MaxOutputTokens` on `ChatOptions` — for **any** provider — so nothing overrides this value today. With thinking on, the ceiling covers **reasoning and response text together**. An operator who sizes it against the expected answer alone will see turns truncate, and the higher the effort level the more of the budget reasoning consumes. Wiring the catalog value through is what would turn this into a genuine fallback; until then, size it generously.

### 4.2 Startup validation

Validation runs with `ValidateOnStart()`, so a misconfigured deployment **refuses to boot** rather than failing on a user's first message. Every predicate is short-circuited on `Enabled`, so a deployment that does not use Anthropic needs no Anthropic settings at all, while a **half-filled** section fails loudly instead of half-working.

| Rule | Message |
|---|---|
| `ApiKey` is non-blank | `Anthropic:ApiKey is required when Anthropic:Enabled is true.` |
| `DefaultModelId` is non-blank | `Anthropic:DefaultModelId is required when Anthropic:Enabled is true.` |
| `DefaultModelId` is not `anthropic.`-prefixed | `Anthropic:DefaultModelId must be a bare Anthropic model id; the 'anthropic.' prefix belongs to Bedrock.` |
| `DefaultMaxOutputTokens > 0` | `Anthropic:DefaultMaxOutputTokens must be greater than zero.` |
| `Timeout > 0` | `Anthropic:Timeout must be greater than zero.` |
| `MaxRetries >= 0` | `Anthropic:MaxRetries cannot be negative.` |
| `Thinking` is a known mode | `Anthropic:Thinking must be 'adaptive' or 'disabled'.` |
| `Effort` is a known level | `Anthropic:Effort must be one of low, medium, high, xhigh, max.` |
| `Thinking`/`Effort` pairing is legal | `Anthropic:Thinking 'disabled' is not accepted with Anthropic:Effort 'xhigh' or 'max'.` |

Two of these are worth a note:

- The **prefix check** exists because the `AmazonBedrock` section three blocks up in the same file takes `anthropic.`-prefixed ids. Copying one across is the natural mistake; catching it at boot beats a 404 on someone's first message.
- The **`Thinking`/`Effort` checks null-guard before the lookup.** Both accepted-value sets compare case-insensitively, and that comparer throws on a null rather than reporting a miss — which would surface as a startup crash instead of the message above.

None of the messages echo the key.

## 5. Thinking and effort

This is the part Bedrock has no equivalent of. Every Anthropic turn carries an explicit reasoning configuration, built once by [`AnthropicChatDefaults`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Chat/AnthropicChatDefaults.cs) and stamped onto `ChatOptions` by the innermost client in the chain.

### 5.1 `adaptive` vs `disabled`

| `Anthropic:Thinking` | Behaviour |
|---|---|
| `adaptive` (default) | The model decides per request whether to reason before answering, and how much. This is the recommended mode on every current Claude model. |
| `disabled` | No reasoning step. Lower latency and fewer tokens, at the cost of quality on anything multi-step. |

There is deliberately **no fixed-token-budget mode**. A thinking budget is rejected outright by every model this provider targets; `Effort` is what replaced it, and naming its absence in [`AnthropicOptions.ThinkingModes`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/AnthropicOptions.cs) is what stops it being reintroduced as a third mode.

### 5.2 The five effort levels

`Anthropic:Effort` controls how much work the model spends per turn — reasoning depth, tool-call consolidation, and answer thoroughness together.

| Level | When to pick it |
|---|---|
| `low` | Short, scoped, latency-sensitive turns that are not intelligence-sensitive. |
| `medium` | Cost-sensitive deployments willing to trade some depth for tokens and latency. |
| `high` | **The default.** The usual balance of quality against token spend. |
| `xhigh` | The hardest work — long, tool-heavy, multi-step turns. |
| `max` | Correctness matters more than cost. Can show diminishing returns and overthink simple prompts. |

Raising effort buys depth at the cost of latency **and** of output budget: reasoning tokens come out of `DefaultMaxOutputTokens` (§4.1), so a jump from `high` to `max` on an unchanged ceiling leaves less room for the answer itself.

### 5.3 Both settings are deployment-wide

`Thinking` and `Effort` apply to **every Anthropic-backed model in the catalog**. A model row carries no per-model override, and nothing in the request path supplies one — the values are parsed once when the chat client is built and captured in the callback, so changing either takes a configuration change and a restart.

The practical consequence: a catalog holding both a cheap model for quick lookups and an expensive one for deep work runs them at the same effort. If you need two effort profiles, run two deployments.

One pairing is illegal and is rejected at startup rather than on every request: **`disabled` thinking with `xhigh` or `max` effort**. Anthropic's models reject that combination with a 400 on every turn, so [`AnthropicOptions.IsThinkingEffortCombinationValid`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/AnthropicOptions.cs) blocks it before it can reach a conversation. `disabled` is accepted at `low`, `medium`, and `high`; `adaptive` is accepted at all five levels.

### 5.4 What lands on the wire, and why the chain order matters

`AnthropicChatDefaults.Create` returns a `ChatOptions` callback that does three things per turn:

1. **Clears the sampling parameters.** `Temperature`, `TopP`, and `TopK` are set to `null`. They were removed from the request surface on the models this provider targets — Opus 4.7 and later, Sonnet 5, Fable 5 — where sending one is a **400 on every turn regardless of the thinking mode**. Clearing them here means a call site that sets them for the other providers does not silently break only this one.
2. **Supplies a raw request object** carrying `Model`, `MaxTokens`, an empty `Messages` list, the parsed `Thinking` config, and `OutputConfig.Effort`. `Model` and `MaxTokens` are read back off `ChatOptions` (falling back to the configured defaults when blank) because the SDK's bridge merges the messages, tools, and system content it derives from `ChatOptions` onto this object but does **not** overlay those two fields — hardcoding either would pin every Anthropic conversation to one model. `Messages` is empty rather than a placeholder because the bridge concatenates that list *ahead of* the real conversation.
3. **Leaves a caller-supplied raw representation alone** (`??=`), so a call site that needs different parameters for a single turn keeps the escape hatch `Microsoft.Extensions.AI` provides.

The callback is registered with `.Use(...)` **last** in the builder chain, which makes it **innermost**: `ChatClientBuilder` applies its factories in reverse, so this client sits closest to the SDK and its `ChatOptions` are the ones the bridge reads. It is spelled out with the `(innerClient, services)` overload rather than `ConfigureOptions(...)` because only that overload reaches the container, and the settings have to come from the validated `IOptions` rather than a second bind of the same section.

Two test classes cover this from both sides. [`AnthropicChatDefaultsTests`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Chat/AnthropicChatDefaultsTests.cs) exercises the callback in isolation, including every thinking mode and effort level the validators accept — so a value added to the accepted set without a matching parser arm fails there rather than on the first conversation. [`AnthropicChatClientBridgeTests`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Chat/AnthropicChatClientBridgeTests.cs) pins the request the bridge actually puts on the wire, against a stub HTTP handler: the no-overlay behaviour it relies on is undocumented and could change in a package bump, and every isolated test would stay green while every conversation silently ran on the wrong model.

## 6. Client registration and credentials

### 6.1 One singleton client

`IAnthropicClient` is registered as a singleton **in its own right**, not constructed inside the chat-client factory, because the SDK asks for exactly that: each client carries its own connection and thread pools, so one per request would exhaust both.

Unlike the Bedrock runtime client there is no disposal to place. `AnthropicClient` is not `IDisposable`; the HTTP resources it holds are released only through the SDK's opt-in `ClientOptions.DisposeHttpResources()`, which a process-lifetime singleton never needs.

### 6.2 Assigning the key is also what disables credential discovery

```csharp
return new AnthropicClient
{
    ApiKey = anthropicOptions.ApiKey,
    Timeout = anthropicOptions.Timeout,
    MaxRetries = anthropicOptions.MaxRetries
};
```

Setting `ApiKey` explicitly suppresses the SDK's environment and profile credential resolution. That matters on any host where an `ANTHROPIC_API_KEY` happens to be set — a developer laptop, a shared build agent, a container inheriting an environment: without the explicit assignment, that ambient key could authenticate requests for a deployment whose *configured* key is wrong, and the misconfiguration would only surface when the ambient key was removed. This is the Anthropic-side analogue of Bedrock's `AuthSchemePreference` pinning, and like it, "simplifying" it back out reintroduces a bug that only appears on some hosts.

## 7. Choosing a model id

`DeploymentName` takes any model id the Anthropic Messages API accepts. The three that make sense in a chat catalogue today:

| Model id | Context window | Max output | Notes |
|---|---:|---:|---|
| `claude-opus-5` | 1M | 128K | The default in `appsettings.json`; the strongest option for complex, agentic, and tool-heavy work. |
| `claude-sonnet-5` | 1M | 128K | Near-Opus quality on coding and agentic work at Sonnet cost. |
| `claude-haiku-4-5` | 200K | 64K | Fastest and cheapest; note the smaller context window and output cap. |

Set `contextWindowSize` and `maxOutputTokens` on the catalog entry from this table, not from the Azure or Bedrock model you copied the row from. Both must be greater than zero. Bear in mind that `maxOutputTokens` on the model row is **not** what limits a turn today (§4.1) — it is catalog metadata, and `Anthropic:DefaultMaxOutputTokens` is the value that reaches the wire.

## 8. Adding an Anthropic-backed model to the catalog

Model CRUD is administrator-only and otherwise unchanged; see [Model Management](model-management.md) for the full endpoint surface. What is Anthropic-specific:

- **`providerId`** must be `6f93ec17-e981-409f-a523-700584f1e7d6`, and a matching **active** row must exist in `Core.Ref.Provider`. `ModelService.EnsureProviderExistsAsync` checks that row and returns 404 `"Provider not found."` if it is missing — see §10.1, which is the usual cause.
- **`deploymentName`** carries the bare model id (§3.2, §7). Required, maximum 512 characters — far more than an Anthropic id needs; the column was widened for Bedrock ARNs.
- **`name`** is the label users see. Nothing routes on it.
- **`contextWindowSize` / `maxOutputTokens`** must both be greater than zero (§7).

Provider existence is checked, but provider *routability* is not: creating an Anthropic model while `Anthropic:Enabled` is `false` succeeds. It fails later, at chat time, with the 503 below — never at create time.

## 9. When the provider is not configured — the 503 contract

Unchanged from Bedrock. [`ProviderNotConfiguredException`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Exceptions/ProviderNotConfiguredException.cs) maps to **503 Service Unavailable** with problem type `/problems/provider-not-configured`:

```json
{
  "type": "/problems/provider-not-configured",
  "title": "Provider not configured",
  "status": 503,
  "detail": "No chat client is configured for provider '6f93ec17-e981-409f-a523-700584f1e7d6'.",
  "instance": "/api/conversations/9a7f.../stream",
  "traceId": "00-a1b2c3d4e5f60718293a4b5c6d7e8f90-1122334455667788-01",
  "providerId": "6f93ec17-e981-409f-a523-700584f1e7d6"
}
```

As with every other domain type, `/problems/provider-not-configured` is an **opaque identifier to match verbatim**, not a URL to fetch; changing it is a breaking API change. The `providerId` extension is the machine-readable half — enough for a client to grey out the offending model without parsing `detail`.

It is a 503 rather than a 500 or a 400 because the request is well formed and the model exists: nothing the caller changes will help, and an operator has to configure a client or deactivate the model. Delivery is clean on the streaming route too — `Resolve` runs before the first SSE fragment, so the problem arrives as a normal `application/problem+json` response rather than as a corrupted stream. See [Amazon Bedrock §9](amazon-bedrock.md#9-when-the-provider-is-not-configured--the-503-contract) for the full reasoning.

The most common trigger here is an Anthropic-backed model in the catalog while `Anthropic:Enabled` is `false`. Note that it does **not** fall through to Bedrock, even though Bedrock also serves Claude — a unit test pins that specifically.

## 10. Operational notes

### 10.1 The Anthropic provider row does not reach a migrated database

**This change ships no EF migration and no SQL script.** `Repository/Migrations/` is still declared as an empty `<Folder Include>` in the csproj and holds zero migrations, so the `Database.Migrate()` call at startup applies nothing.

The new row is seeded in [`ProviderConfiguration`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/ProviderConfiguration.cs) via `HasData`, which reaches **only databases built by `EnsureCreated()`** — that is, the test databases. Until the row is inserted by hand, `ModelService.EnsureProviderExistsAsync` rejects every attempt to create an Anthropic-backed model with **404 `"Provider not found."`** — regardless of `Anthropic:Enabled`, and with nothing in the message pointing at the missing row.

```sql
INSERT INTO [Core.Ref].[Provider] ([Id], [Name], [DateCreated])
VALUES ('6F93EC17-E981-409F-A523-700584F1E7D6', 'Anthropic', SYSDATETIMEOFFSET());
```

The id must be **exactly** `6f93ec17-e981-409f-a523-700584f1e7d6`. It is the key `Providers.ServiceKeys` looks up; a freshly generated GUID produces a provider that creates models happily and 503s on every turn.

### 10.2 Session and turn behaviour

- **Every turn carries the deployment's thinking and effort settings**, including the conversation-naming turn that runs inline on a conversation's first message (§3.1). At `xhigh` or `max`, that naming turn is real latency in front of the first token of every new conversation — it is the cheapest place to notice an effort level set too high.
- **The output ceiling is shared between reasoning and answer** (§4.1). A truncated answer with thinking on usually means `DefaultMaxOutputTokens` is too low, not that the model stopped early.
- **Worst-case wall clock for one turn is `Timeout × (MaxRetries + 1)`** — 30 minutes on the defaults. Turns that combine thinking with several tool-invocation rounds run long, so trimming `Timeout` is an effective way to manufacture timeouts on exactly the requests that most needed to finish.
- **Sampling parameters never reach Anthropic** (§5.4). A call site that sets `Temperature` for Azure or Bedrock is silently and deliberately ignored on this provider.
- **Configuration is read once, at client construction.** Changing `Thinking`, `Effort`, `DefaultModelId`, or `DefaultMaxOutputTokens` requires a restart; there is no `IOptionsMonitor` reload path.
- **The keyed chat client is built lazily, on first resolution.** A misconfiguration the startup validators do not cover would surface on the first Anthropic conversation rather than at boot — which is why the accepted-value sets are shared between the validators and their tests.

## 11. Key files

| Concern | File |
|---|---|
| Provider ids and the provider → DI-key map | [`Enterprise.Gpt.Dto/Enums/Providers.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/Providers.cs) |
| DI keys | [`Enterprise.Gpt.Dto/Enums/ChatClientKeys.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/ChatClientKeys.cs) |
| Configuration binding, accepted values, pairing rule | [`Enterprise.Gpt.Service/Settings/AnthropicOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/AnthropicOptions.cs) |
| Per-turn thinking, effort, model, and token settings | [`Enterprise.Gpt.Api/Chat/AnthropicChatDefaults.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Chat/AnthropicChatDefaults.cs) |
| Client registration, startup validation, builder chain | [`Enterprise.Gpt.Api/Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) |
| Shipped defaults | [`Enterprise.Gpt.Api/appsettings.json`](../../enterprise-gpt-api/Enterprise.Gpt.Api/appsettings.json) |
| Request-time client selection | [`Enterprise.Gpt.Service/Chat/ChatClientResolver.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Chat/ChatClientResolver.cs) |
| Both `Resolve` call sites | [`Enterprise.Gpt.Service/ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs) |
| 503 exception and problem type | [`Exceptions/ProviderNotConfiguredException.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Exceptions/ProviderNotConfiguredException.cs), [`Api/Problems/ProblemTypes.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Problems/ProblemTypes.cs) |
| Provider seed row | [`Enterprise.Gpt.Repository/Configurations/ProviderConfiguration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/ProviderConfiguration.cs) |
| Settings and pairing tests | [`tests/Enterprise.Gpt.Unit.Test/Settings/AnthropicOptionsTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Settings/AnthropicOptionsTests.cs) |
| Per-turn settings tests | [`tests/Enterprise.Gpt.Unit.Test/Chat/AnthropicChatDefaultsTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Chat/AnthropicChatDefaultsTests.cs) |
| Wire-shape test against the SDK bridge | [`tests/Enterprise.Gpt.Unit.Test/Chat/AnthropicChatClientBridgeTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Chat/AnthropicChatClientBridgeTests.cs) |
| Resolver tests | [`tests/Enterprise.Gpt.Unit.Test/Services/ChatClientResolverTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Services/ChatClientResolverTests.cs) |

## 12. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| App refuses to start: `Anthropic:ApiKey is required when Anthropic:Enabled is true.` | `Enabled` set without the rest of the section | Supply `ApiKey` and `DefaultModelId`, or set `Enabled: false` |
| App refuses to start: `Anthropic:DefaultModelId must be a bare Anthropic model id…` | A Bedrock inference-profile id was copied into the Anthropic section | Strip the `anthropic.` prefix and any date suffix (§3.2) |
| App refuses to start: `Anthropic:Thinking 'disabled' is not accepted with Anthropic:Effort 'xhigh' or 'max'.` | An illegal pairing the models reject on every request | Use `adaptive` thinking, or lower effort to `high` or below (§5.3) |
| 404 `"Provider not found."` on `POST /api/models` | The Anthropic row is missing from `Core.Ref.Provider` | §10.1 |
| 503 `/problems/provider-not-configured` on the first message | `Anthropic:Enabled` is `false`, or the provider id has no entry in `Providers.ServiceKeys` | Enable and configure the provider, or deactivate the model |
| Anthropic rejects the model as unknown | A model row's `deploymentName` carries an `anthropic.` prefix or a date suffix — the startup validator only checks `DefaultModelId` | Correct the catalog row to the bare id (§3.2, §7) |
| Every answer is cut off mid-sentence | `DefaultMaxOutputTokens` too low for reasoning **plus** answer | Raise it, or lower `Effort` (§4.1, §5.2) |
| Answers are shallow, and there is no reasoning latency | `Thinking` is `disabled`, or `Effort` is `low` | Set `Thinking: adaptive` and raise `Effort` (§5) |
| Long tool-heavy turns fail after a delay | `Timeout` too low for thinking plus several tool rounds | Raise `Timeout`; worst case is `Timeout × (MaxRetries + 1)` (§10.2) |
| Requests authenticate with the wrong key, or start failing when a host variable is removed | The explicit `ApiKey` assignment was dropped, so an ambient `ANTHROPIC_API_KEY` was authenticating | Restore it (§6.2) |
| `Temperature` set by a caller has no effect | Sampling parameters are cleared on every Anthropic turn by design | Nothing to fix — the models reject them (§5.4) |
| First token of a new conversation is slow, later turns are fine | The inline conversation-naming turn runs at the configured effort | Lower `Effort`, or accept it (§10.2) |

Client construction happens once at startup, so most of these surface as either a boot failure or an error on the very first Anthropic turn — rarely as an intermittent fault.
