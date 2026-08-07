# Amazon Bedrock Provider

Reference for running Enterprise GPT against Amazon Bedrock alongside Azure AI Foundry: how a request picks its provider, how to obtain and configure a Bedrock API key, how to add a Bedrock-backed model to the catalog, and the two out-of-band database changes this release depends on. Audience: engineers wiring up a second provider, and operators deploying this build.

## 1. Why this exists

Until this change the API had exactly one chat client. It was registered under the key `"azureaifoundry"` and injected straight into [`ConversationService`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs) with `[FromKeyedServices]`. Every model in the catalog carried a `ProviderId`, and nothing ever read it — whichever provider a model claimed to belong to, its turns went to Azure.

Adding Anthropic's Claude models means adding a provider, and adding a provider means the choice has to move from *construction time* to *request time*. That is the whole shape of this change:

- A model's `ProviderId` now selects the chat client, through [`IChatClientResolver`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Chat/ChatClientResolver.cs).
- Bedrock is registered only when `AmazonBedrock:Enabled` is `true`, so a deployment that does not use it needs no Bedrock settings at all.
- A model whose provider has no client in this deployment fails with a **503**, not a 500 — it is an operator condition, not a bad request.

Both providers reach the LLM through the same [`Microsoft.Extensions.AI`](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai) `IChatClient` abstraction, so the rest of the pipeline — MCP tool invocation, OpenTelemetry, token accounting, SSE streaming — is untouched.

## 2. Quick start

Five steps to a working Bedrock-backed model locally. Steps 3 and 4 are the ones that fail silently if you skip them; §10 explains why.

**1. Generate a long-term Bedrock API key** in the AWS console (§4).

**2. Configure the API** with user secrets — never `appsettings.json`, which is checked in:

```bash
cd enterprise-gpt-api/Enterprise.Gpt.Api
dotnet user-secrets set "AmazonBedrock:Enabled" "true"
dotnet user-secrets set "AmazonBedrock:Region" "us-east-1"
dotnet user-secrets set "AmazonBedrock:ApiKey" "your-bedrock-api-key"
dotnet user-secrets set "AmazonBedrock:DefaultModelId" "us.anthropic.claude-sonnet-4-5-20250929-v1:0"
```

**3. Insert the Bedrock provider row** — it is seeded through `HasData`, which only reaches databases built by `EnsureCreated()`:

```sql
INSERT INTO [Core.Ref].[Provider] ([Id], [Name], [DateCreated])
VALUES ('8C4B1F27-6A3D-4D59-9E21-B0F4A7C6D812', 'AmazonBedrock', SYSDATETIMEOFFSET());
```

**4. Bring `Core.Ref.Model` in line** with the `DisplayName` → `DeploymentName` rename (§10.1) if your database predates this build.

**5. Add a model to the catalog** as an administrator:

```bash
curl -X POST https://localhost:7001/api/models \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
        "providerId": "8c4b1f27-6a3d-4d59-9e21-b0f4a7c6d812",
        "name": "Claude Sonnet 4.5",
        "deploymentName": "us.anthropic.claude-sonnet-4-5-20250929-v1:0",
        "description": "Anthropic Claude Sonnet 4.5 on Amazon Bedrock.",
        "contextWindowSize": 200000,
        "maxOutputTokens": 64000,
        "isToolEnabled": true,
        "isDefault": false
      }'
```

The model appears in `GET /api/models` and the frontend picker immediately; the next conversation that selects it routes to Bedrock.

## 3. Core concepts

### 3.1 A provider id selects a chat client

Three pieces do the routing, and they live together on purpose:

| Piece | Where | What it holds |
|---|---|---|
| Provider ids | [`Providers`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/Providers.cs) | `AzureOpenAI` = `3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0`, `AmazonBedrock` = `8c4b1f27-6a3d-4d59-9e21-b0f4a7c6d812` |
| DI keys | [`ChatClientKeys`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/ChatClientKeys.cs) | `"azureaifoundry"`, `"amazonbedrock"` |
| The map between them | `Providers.ServiceKeys` (a `FrozenDictionary<Guid, string>`) | provider id → DI key |

[`ChatClientResolver`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Chat/ChatClientResolver.cs) is a singleton that walks that map and pulls the keyed `IChatClient` out of the root container:

```csharp
public IChatClient Resolve(Guid providerId) =>
    Providers.ServiceKeys.TryGetValue(providerId, out var serviceKey)
        ? _serviceProvider.GetKeyedService<IChatClient>(serviceKey)
            ?? throw new ProviderNotConfiguredException(providerId)
        : throw new ProviderNotConfiguredException(providerId);
```

Both misses throw the same exception. A provider absent from `ServiceKeys` (a row somebody added to `Core.Ref.Provider` by hand) and a provider whose client is switched off by configuration are one condition as far as a caller is concerned: **this deployment cannot serve that model**. Distinguishing them in the response would leak deployment detail the caller can do nothing with.

`ConversationService` calls `Resolve` at **two** sites, and it matters that both moved:

1. The streaming turn in `StreamConversationAsync`.
2. The conversation-naming turn, which runs **inline on a conversation's first turn**. Had it stayed pinned to Azure, a Bedrock-only deployment would have failed there before the stream produced a single token.

`Core.Ref.Provider` is not the source of truth for what can serve a request — `ServiceKeys` plus the registrations in `Program.cs` are. Model creation only checks that the provider *row* exists (§8), so an id that is in the table but not in the map creates fine and fails at chat time.

### 3.2 `DeploymentName` is the provider-side identifier

This release removed `Model.DisplayName` and added `Model.DeploymentName`. The split is now:

| Column | Type | Meaning |
|---|---|---|
| `Name` | `nvarchar(256)` | The human label. What the picker shows. Rename it freely. |
| `DeploymentName` | `nvarchar(512)` | The identifier the provider understands, sent as `ChatOptions.ModelId`. |

`DeploymentName` is an Azure OpenAI deployment name on Azure, and a model id, inference profile id, or ARN on Bedrock (§7). It is `nvarchar(512)` rather than 256 because a fully qualified Bedrock inference-profile ARN is far longer than an Azure deployment name.

One consequence reaches the message transcript: the Cosmos DB record now stores `DeploymentName`, not the label. The transcript is append-only, so the value written has to stay meaningful after the catalog is renamed — and it is the deployment, not the label, that identifies which model actually served and billed the turn. See [Model Management §2.1](model-management.md#21-wire-shape-contracts) for the full wire shape.

### 3.3 MCP tools work unchanged

The adapter in `AWSSDK.Extensions.Bedrock.MEAI` wraps Bedrock's **`Converse` / `ConverseStream`** APIs — not `InvokeModel` — and surfaces a `toolUse` block as a `FunctionCallContent`. That is exactly the content type `Microsoft.Extensions.AI`'s function-invocation middleware consumes, so the existing MCP tool pipeline runs on Bedrock with no provider-specific code.

Both clients share one configuration callback in [`Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) so tool behaviour cannot drift between providers:

```csharp
static void ConfigureFunctionInvocation(FunctionInvokingChatClient client)
{
    client.AllowConcurrentInvocation = false;
    client.IncludeDetailedErrors = true;
    client.MaximumIterationsPerRequest = 5;
    client.MaximumConsecutiveErrorsPerRequest = 5;
}
```

Whether a given Bedrock model supports tools at all is still yours to declare: set `isToolEnabled` on the catalog entry accordingly.

## 4. Getting a Bedrock API key

Amazon Bedrock API keys come in two forms, and **only one of them works here**.

| Key type | Lifetime | Backed by | Usable in Enterprise GPT |
|---|---|---|---|
| Short-term | Expires with your console session, at most 12 hours | The IAM principal that generated it | **No** |
| Long-term | Until the expiry you choose at generation | A dedicated IAM user | Yes |

Short-term keys are AWS's recommendation for production precisely because they rotate. They are unusable here for a mechanical reason: the token is handed to the SDK once at client construction through a `StaticTokenProvider`, which never refreshes it (§6). A short-term key would work until it expired and then fail every request until the process restarted.

To generate a long-term key:

1. Sign in to the AWS console with an identity that can use Amazon Bedrock, open the **Amazon Bedrock** console, and choose **API keys** in the left navigation.
2. On the **Long-term API keys** tab, choose **Generate long-term API keys** and pick an expiry.
3. AWS attaches the `AmazonBedrockLimitedAccess` managed policy to the new IAM user by default. Expand **Advanced permissions** to attach anything narrower or broader.
4. Copy the key once — it is not shown again.

Two things to check before the first request:

- **Model access.** Request access to the Anthropic models you intend to use, in the region you configured, under **Model access** in the same console. Access is per region.
- **Key expiry.** A long-term key still expires. Track the date; when it lapses every Bedrock turn starts failing, and the only symptom is an authorization error from the SDK.

Treat the key as a secret with a rotation plan: `dotnet user-secrets` locally, Azure Key Vault when deployed. It never belongs in `appsettings.json` — which is why the shipped file has a `Region` and a `DefaultModelId` but no `ApiKey`.

Sources: [Amazon Bedrock API keys](https://docs.aws.amazon.com/bedrock/latest/userguide/api-keys.html), [Generate an Amazon Bedrock API key](https://docs.aws.amazon.com/bedrock/latest/userguide/api-keys-generate.html).

## 5. Configuration

### 5.1 Settings

The `AmazonBedrock` section binds to [`AmazonBedrockOptions`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/AmazonBedrockOptions.cs).

| Setting | Default | Required | What it does |
|---|---|---|---|
| `Enabled` | `false` | — | Registers the Bedrock runtime client and the `"amazonbedrock"` chat client. Everything else in the section is ignored while this is `false`. |
| `Region` | `us-east-1` (in `appsettings.json`) | when enabled | The AWS region system name the Bedrock runtime endpoint resolves from. One region serves every Bedrock-backed model. |
| `ApiKey` | — | when enabled | The long-term Bedrock API key, presented as an HTTP bearer token. **Secrets store only.** |
| `DefaultModelId` | — | when enabled | The model id the SDK requires to construct a client. Every chat turn overrides it from the catalog, so this is a fallback that is never used in practice. |

There is no second region setting, and no second client. Cross-region capacity comes from putting an inference profile id in a model's `DeploymentName` (§7), not from registering another provider.

### 5.2 Startup validation

Validation runs with `ValidateOnStart()`, so a misconfigured deployment **refuses to boot** rather than failing on a user's first message. Every predicate is short-circuited on `Enabled`:

```csharp
builder.Services.AddOptions<AmazonBedrockOptions>()
    .Bind(builder.Configuration.GetSection(AmazonBedrockOptions.SectionName))
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ApiKey), /* … */)
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.DefaultModelId), /* … */)
    .Validate(options => !options.Enabled || RegionEndpoint.EnumerableAllRegions.Any(r => r.SystemName == options.Region), /* … */)
    .ValidateOnStart();
```

That `!options.Enabled ||` prefix is what lets a deployment with no Bedrock settings whatsoever start cleanly, while a **half-filled** section — `Enabled: true` with a blank `ApiKey`, say — fails loudly instead of half-working.

The region check deserves a note. It matches against `RegionEndpoint.EnumerableAllRegions` rather than calling `RegionEndpoint.GetBySystemName`, because `GetBySystemName` fabricates an endpoint for an unknown name instead of failing — turning a typo into a confusing DNS error on the first request. The trade-off: **that list is baked into the pinned `AWSSDK.Core`**, so a region AWS launches after the pin is rejected until the package is bumped. The validation message says so, which is the only place that trade-off is visible:

```text
AmazonBedrock:Region must be a known AWS region system name, for example us-east-1.
Regions added to AWS after the pinned AWSSDK.Core version are not recognized until it is upgraded.
```

None of the messages echo the key.

### 5.3 Packages

Both live in [`Enterprise.Gpt.Api.csproj`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Enterprise.Gpt.Api.csproj):

| Package | Version | Why |
|---|---|---|
| `AWSSDK.Extensions.Bedrock.MEAI` | 4.0.101.7 | Supplies `IAmazonBedrockRuntime.AsIChatClient()` — the `Microsoft.Extensions.AI` adapter over `Converse`/`ConverseStream`. |
| `AWSSDK.BedrockRuntime` | 4.0.101 | A direct reference because `Program.cs` constructs `AmazonBedrockRuntimeClient` and its config itself, to wire bearer auth. |

The service layer takes **no** AWS package reference: `AmazonBedrockOptions` is deliberately free of AWS types, and the region name is validated where the client is registered.

## 6. Why bearer auth needs explicit wiring in .NET

This is the least obvious part of the change, and the part most likely to be "simplified" back into a bug.

Other AWS SDKs read the `AWS_BEARER_TOKEN_BEDROCK` environment variable and just work. **The AWS SDK for .NET does not.** Its default token-resolution chain resolves SSO profiles only and never looks at that variable, so setting it has no effect here. The key has to be handed to the client explicitly, and two further settings are needed to stop the SDK from authenticating some other way:

```csharp
var bedrockConfig = new AmazonBedrockRuntimeConfig
{
    RegionEndpoint = RegionEndpoint.GetBySystemName(bedrockOptions.Region),
    AWSTokenProvider = new StaticTokenProvider(bedrockOptions.ApiKey, null),
    AuthSchemePreference = ["httpBearerAuth"]
};

return new AmazonBedrockRuntimeClient(new AnonymousAWSCredentials(), bedrockConfig);
```

Each line earns its place:

- **`AWSTokenProvider = new StaticTokenProvider(key, null)`** supplies the API key as the bearer token. `null` is the expiry — the provider never refreshes, which is the mechanical reason short-term keys (§4) do not work.
- **`AuthSchemePreference = ["httpBearerAuth"]`** pins the scheme. Bedrock's auth resolver lists SigV4 **first**, so on any host carrying ambient AWS credentials — a developer laptop with `~/.aws/credentials`, an EC2 instance with an instance profile, a container with a task role — SigV4 would win and the API key would be silently ignored. Whether the deployment worked would then depend on what credentials happened to be lying around.
- **`new AnonymousAWSCredentials()`** stops the SigV4 identity resolver from probing IMDS looking for credentials it will never use. Without it, every client construction on a non-EC2 host pays a metadata-service timeout.

One more registration detail. `IAmazonBedrockRuntime` is registered as a singleton **in its own right**, not constructed inside the chat-client factory:

```csharp
builder.Services.AddSingleton<IAmazonBedrockRuntime>(sp => /* … */);

builder.Services.AddKeyedChatClient(
        ChatClientKeys.AmazonBedrock,
        sp => sp.GetRequiredService<IAmazonBedrockRuntime>()
                .AsIChatClient(sp.GetRequiredService<IOptions<AmazonBedrockOptions>>().Value.DefaultModelId))
    .UseOpenTelemetry()
    .UseFunctionInvocation(null, ConfigureFunctionInvocation);
```

The MEAI adapter's `Dispose` is a deliberate no-op because the adapter does not own the runtime client. A client created inside that factory closure would therefore be owned by nobody and never disposed.

## 7. Choosing a model id or an inference profile

`DeploymentName` accepts any identifier `Converse` accepts. For Anthropic's Claude models on Bedrock there are three practical forms:

| Form | Example | When to use it |
|---|---|---|
| Foundation model id | `anthropic.claude-3-5-haiku-20241022-v1:0` | Single-region, and only for models that still permit direct on-demand invocation. |
| Cross-region inference profile id | `us.anthropic.claude-sonnet-4-5-20250929-v1:0` | **The default choice.** Required by most current Claude models, and it spreads load across a geography's regions. |
| Inference profile ARN | `arn:aws:bedrock:us-east-1:123456789012:application-inference-profile/…` | Application inference profiles — when you need cost allocation tags or a single-region constraint for data residency. |

Prefer the **cross-region inference profile id** unless you have a reason not to. Newer Claude models reject a bare foundation model id outright, with an error saying on-demand throughput is not supported and asking you to retry with an inference profile id or ARN.

The geography prefix (`us.`, `eu.`, `apac.`, `global.`) has to match the region you configured: a `us.`-prefixed profile is callable from US regions and routes among them. Pointing `AmazonBedrock:Region` at `eu-west-1` while a model carries a `us.` profile fails at request time, not at startup — the catalog knows nothing about which profiles your region can reach.

Sources: [Supported Regions and models for inference profiles](https://docs.aws.amazon.com/bedrock/latest/userguide/inference-profiles-support.html), [Global cross-Region inference](https://docs.aws.amazon.com/bedrock/latest/userguide/global-cross-region-inference.html).

## 8. Adding a Bedrock-backed model to the catalog

Model CRUD is administrator-only and otherwise unchanged; see [Model Management](model-management.md) for the full endpoint surface. What is Bedrock-specific:

- **`providerId`** must be `8c4b1f27-6a3d-4d59-9e21-b0f4a7c6d812`, and a matching **active** row must exist in `Core.Ref.Provider`. `ModelService.EnsureProviderExistsAsync` checks that row and returns 404 `"Provider not found."` if it is missing — see §10.2, which is the usual cause.
- **`deploymentName`** carries the Bedrock identifier (§7). Required, maximum 512 characters.
- **`name`** is the label users see. It is now genuinely a label; nothing routes on it.
- **`contextWindowSize` / `maxOutputTokens`** must both be greater than zero. Set them from the model's published limits, not from the Azure model's.

Provider existence is checked, but provider *routability* is not: creating a Bedrock model while `AmazonBedrock:Enabled` is `false` succeeds. It fails later, at chat time, with the 503 in §9 below — never at create time.

## 9. When the provider is not configured — the 503 contract

[`ProviderNotConfiguredException`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Exceptions/ProviderNotConfiguredException.cs) maps to **503 Service Unavailable** with problem type `/problems/provider-not-configured`:

```json
{
  "type": "/problems/provider-not-configured",
  "title": "Provider not configured",
  "status": 503,
  "detail": "No chat client is configured for provider '8c4b1f27-6a3d-4d59-9e21-b0f4a7c6d812'.",
  "instance": "/api/conversations/9a7f.../stream",
  "traceId": "00-a1b2c3d4e5f60718293a4b5c6d7e8f90-1122334455667788-01",
  "providerId": "8c4b1f27-6a3d-4d59-9e21-b0f4a7c6d812"
}
```

As with every other domain type, `/problems/provider-not-configured` is an **opaque identifier to match verbatim**, not a URL to fetch; changing it is a breaking API change. The `providerId` extension is the machine-readable half — enough for a client to grey out the offending model without parsing `detail`.

Why 503 and not 500:

- The request is well formed and the model exists. Nothing the caller changes about the request will help; an operator has to configure a client or deactivate the model. 503 says "try later", which is the honest answer.
- The exception derives from `Exception`, **not** `InvalidOperationException`, on purpose. `GlobalExceptionHandler` maps `InvalidOperationException` to 400, which would misreport a configuration gap as the caller's mistake. A unit test pins the 503 specifically so that rebasing the exception cannot quietly downgrade the contract.

Delivery is clean on the streaming route too. `Resolve` runs before the first SSE fragment, and `POST api/conversations/{id}/stream` sets its `text/event-stream` headers lazily on that first fragment, so the 503 arrives as a normal `application/problem+json` response rather than as a corrupted stream.

The most common trigger is a Bedrock-backed model in the catalog while `AmazonBedrock:Enabled` is `false`. The second is a provider row that exists in `Core.Ref.Provider` but has no entry in `Providers.ServiceKeys`.

## 10. Operational notes — the schema is not migrated

**This change ships no EF migration and no SQL script.** `Repository/Migrations/` is still declared as an empty `<Folder Include>` in the csproj and does not exist on disk, so the `Database.Migrate()` call at startup applies nothing. The work is local-only and the database is expected to be brought in line out of band.

Two consequences follow, and **both fail silently** — the app builds, boots, and serves requests in each case.

### 10.1 A plain rename leaves every existing row inverted

`DisplayName` was not renamed to `DeploymentName` in place: the two columns **swapped roles**. Before, `Name` held the deployment identifier (`rr-gpt-5.6-luna`) and `DisplayName` held the label (`RR GPT 5.6 Luna`). Now `Name` is the label and `DeploymentName` is the identifier.

Rename the column without swapping the values and every existing model sends its *label* to the provider as `ChatOptions.ModelId`, and shows its *deployment id* in the picker. Nothing throws — the provider simply rejects an unknown model.

```sql
EXEC sp_rename N'[Core.Ref].[Model].[DisplayName]', N'DeploymentName', N'COLUMN';

ALTER TABLE [Core.Ref].[Model] ALTER COLUMN [DeploymentName] nvarchar(512) NOT NULL;

-- T-SQL evaluates every right-hand side against the pre-update row, so this is a true swap.
UPDATE [Core.Ref].[Model]
SET [Name] = [DeploymentName],
    [DeploymentName] = [Name];
```

Widening to `nvarchar(512)` is not optional if you intend to store an inference-profile ARN. Verify afterwards that `Name` reads like a label and `DeploymentName` like an identifier — for the seeded row, `Name = 'RR GPT 5.6 Luna'` and `DeploymentName = 'rr-gpt-5.6-luna'`.

### 10.2 The Bedrock provider row does not reach a migrated database

The new row is seeded in [`ProviderConfiguration`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/ProviderConfiguration.cs) via `HasData`, which reaches **only databases built by `EnsureCreated()`** — that is, the test databases. `Database.Migrate()` against an empty `Migrations/` folder applies nothing.

Until the row is inserted by hand, `ModelService.EnsureProviderExistsAsync` rejects every attempt to create a Bedrock-backed model with **404 `"Provider not found."`** — regardless of `AmazonBedrock:Enabled`, and with nothing in the message pointing at the missing row.

```sql
INSERT INTO [Core.Ref].[Provider] ([Id], [Name], [DateCreated])
VALUES ('8C4B1F27-6A3D-4D59-9E21-B0F4A7C6D812', 'AmazonBedrock', SYSDATETIMEOFFSET());
```

The id must be **exactly** `8c4b1f27-6a3d-4d59-9e21-b0f4a7c6d812`. It is the key `Providers.ServiceKeys` looks up; a freshly generated GUID produces a provider that creates models happily and 503s on every turn.

## 11. Key files

| Concern | File |
|---|---|
| Provider ids and the provider → DI-key map | [`Enterprise.Gpt.Dto/Enums/Providers.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/Providers.cs) |
| DI keys | [`Enterprise.Gpt.Dto/Enums/ChatClientKeys.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/ChatClientKeys.cs) |
| Request-time client selection | [`Enterprise.Gpt.Service/Chat/ChatClientResolver.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Chat/ChatClientResolver.cs) |
| Configuration binding + docs | [`Enterprise.Gpt.Service/Settings/AmazonBedrockOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/AmazonBedrockOptions.cs) |
| Client registration, bearer wiring, validation | [`Enterprise.Gpt.Api/Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) |
| 503 exception | [`Enterprise.Gpt.Service/Exceptions/ProviderNotConfiguredException.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Exceptions/ProviderNotConfiguredException.cs) |
| Problem type + status mapping | [`Enterprise.Gpt.Api/Problems/ProblemTypes.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Problems/ProblemTypes.cs), [`ExceptionHandlers/GlobalExceptionHandler.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/ExceptionHandlers/GlobalExceptionHandler.cs) |
| Both `Resolve` call sites | [`Enterprise.Gpt.Service/ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs) |
| Provider seed row | [`Enterprise.Gpt.Repository/Configurations/ProviderConfiguration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/ProviderConfiguration.cs) |
| Resolver tests | [`tests/Enterprise.Gpt.Unit.Test/Services/ChatClientResolverTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Services/ChatClientResolverTests.cs) |

## 12. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| App refuses to start: `AmazonBedrock:ApiKey is required when AmazonBedrock:Enabled is true.` | `Enabled` set without the rest of the section | Supply `ApiKey` and `DefaultModelId`, or set `Enabled: false` |
| App refuses to start: `AmazonBedrock:Region must be a known AWS region system name…` | Typo, or a region newer than the pinned `AWSSDK.Core` | Correct the name; if the region is genuinely new, bump `AWSSDK.Core` |
| 404 `"Provider not found."` on `POST /api/models` | The Bedrock row is missing from `Core.Ref.Provider` | §10.2 |
| 503 `/problems/provider-not-configured` on the first message | `AmazonBedrock:Enabled` is `false`, or the provider id has no entry in `Providers.ServiceKeys` | Enable and configure the provider, or deactivate the model |
| Every model shows its deployment id in the picker, and turns fail with an unknown-model error | The `DisplayName` → `DeploymentName` rename ran without swapping values | §10.1 |
| Bedrock rejects the model: on-demand throughput is not supported | `DeploymentName` holds a bare foundation model id for a model that requires an inference profile | Use the `us.`/`eu.`/`apac.`/`global.` inference profile id (§7) |
| Requests authorize as the wrong AWS identity, or fail with a SigV4 signature error | `AuthSchemePreference` removed, so ambient AWS credentials win over the bearer token | Restore the pinned scheme (§6) |
| Bedrock turns start failing after weeks of working | The long-term API key expired | Generate a new key and rotate the secret (§4) |
| Slow first request on a non-EC2 host | `AnonymousAWSCredentials` removed, so the SigV4 resolver probes IMDS | Restore it (§6) |

Client construction happens once at startup, so most of these surface as either a boot failure or an error on the very first Bedrock turn — rarely as an intermittent fault.
