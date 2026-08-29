# Azure Key Vault Configuration

Reference for the `KeyVault` configuration section: when the API reads secrets from an Azure Key Vault instead of `appsettings.json`, user secrets or an environment variable, how the vault's `--` naming maps onto the configuration keys those secrets already occupy, what happens when the vault is enabled but unreachable, and the operational caveat that matters most — rotation. Audience: operators configuring a deployed environment, and engineers adding the next secret-bearing setting.

## 1. Why this exists

Every secret this API needs — a SQL connection string, a Cosmos connection string, an LLM provider's API key — has, until now, come from wherever `IConfiguration` found it: `appsettings.json` locally (never committed with a real value), user secrets in development, an environment variable in a deployed environment. That works, and it stays the default: **[`KeyVaultOptions.Enabled`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Configuration/KeyVaultOptions.cs) ships `false`.**

What it does not give an operator is one place to rotate a secret, an audit trail of who read it, or RBAC scoped to secrets rather than to the app setting blade. [`KeyVaultConfiguration.AddEnterpriseKeyVault`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Configuration/KeyVaultConfiguration.cs) closes that gap by adding Azure Key Vault as one more `IConfiguration` source — the same mechanism `appsettings.json` and environment variables already are — so every existing `IOptions<T>` binding, every `builder.Configuration["..."]` read, keeps working unchanged. Nothing downstream of `IConfiguration` needed to change to gain this.

This is an **in-process, host-agnostic** provider: it needs no App Service, no platform-managed reference resolution, and no Terraform. §12 covers how it relates to the infrastructure PRD's own, different answer to the same problem.

## 2. Quick start

**By default, nothing changes.** `KeyVault:Enabled` is `false` in the shipped `appsettings.json`, so the vault is never reached and every secret resolves exactly as it does today.

**To read secrets from a vault locally:**

```bash
# write the secret to the vault, section-delimiter naming — see §4
az keyvault secret set --vault-name <vault> --name "AzureOpenAI--ApiKey" --value "<key>"

# point the API at the vault — user secrets, never appsettings.json
cd enterprise-gpt-api/Enterprise.Gpt.Api
dotnet user-secrets set "KeyVault:Enabled" "true"
dotnet user-secrets set "KeyVault:VaultUri" "https://<vault>.vault.azure.net/"
```

`DefaultAzureCredential` authenticates the request, so a local `az login` is all local development needs (§9). In a deployed environment, set the same two keys as app settings and grant the app's managed identity the **Key Vault Secrets User** role (§9) — no client secret is ever involved in reaching the vault itself.

## 3. Configuration

The `KeyVault` section binds to [`KeyVaultOptions`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Configuration/KeyVaultOptions.cs):

```json
"KeyVault": {
  "Enabled": false,
  "VaultUri": "",
  "ManagedIdentityClientId": null,
  "ReloadInterval": null
}
```

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Whether a vault is added to the configuration chain at all. Off means **nothing** about Key Vault happens — no credential is constructed, no request leaves the process |
| `VaultUri` | *(empty)* | The vault, e.g. `https://contoso.vault.azure.net/`. Required once `Enabled` is `true`; must be an absolute `https://` URI |
| `ManagedIdentityClientId` | `null` | Client id of a **user-assigned** managed identity. Leave unset for a system-assigned identity or for local development, where the credential chain falls back to the signed-in Azure CLI or Visual Studio account |
| `ReloadInterval` | `null` | How often to re-read the vault. `null` reads it once at startup; any value under one minute is rejected (§8) |

### 3.1 When the vault is (and is not) added

[`KeyVaultConfiguration.Resolve`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Configuration/KeyVaultConfiguration.cs) decides, and the rule is deliberately narrow: a vault is added **if and only if** `KeyVault:Enabled` is `true` **and** the host is not running in the `Testing` environment. The `Testing` check exists so the integration-test host — which supplies its own fake configuration and can reach no Azure endpoint — never blocks the suite on a credential exchange that can never succeed, regardless of what `KeyVault:Enabled` happens to read.

`AddEnterpriseKeyVault` runs as the very first line in `Program.cs`, ahead of every other configuration read:

```csharp
// Ahead of every other line here: builder.Configuration rebuilds on each source added, so this
// both loads the vault and fails the host if it cannot, before anything reads a secret from it.
var keyVault = builder.AddEnterpriseKeyVault();
```

That ordering is load-bearing, not stylistic: `IConfigurationBuilder` rebuilds its composed view every time a source is appended, so adding the vault here means every subsequent `Bind`/`ValidateOnStart` in `Program.cs` — for every provider, not just the ones this document names — already sees the vault's values merged in.

## 4. Secret naming — the `--` delimiter

Key Vault secret names may not contain `:`, the character `IConfiguration` uses to nest a section. The [Azure Key Vault configuration provider](https://learn.microsoft.com/aspnet/core/security/key-vault-configuration) treats `--` as the substitute, so a secret named `Section--Key` arrives in `IConfiguration` as `Section:Key` — indistinguishable, once loaded, from the same key set any other way.

Nine settings in this application carry a secret and are candidates for this section:

| Key Vault secret name | Arrives as | Read by |
|---|---|---|
| `ConnectionStrings--DefaultConnection` | `ConnectionStrings:DefaultConnection` | `EnterpriseGptDbContext` (SQL Server) |
| `CosmosDb--ConnectionString` | `CosmosDb:ConnectionString` | the Cosmos transcript store |
| `AzureStorage--ConnectionString` | `AzureStorage:ConnectionString` | `BlobServiceClient` (uploaded and generated documents) |
| `DocumentIntelligence--ApiKey` | `DocumentIntelligence:ApiKey` | `DocumentIntelligenceClient` |
| `AzureOpenAI--ApiKey` | `AzureOpenAI:ApiKey` | the Azure OpenAI client — chat, embeddings and the File Agent all share it |
| `AzureAIFoundry--ApiKey` | `AzureAIFoundry:ApiKey` | the Azure AI Foundry chat client |
| `AzureAd--ClientSecret` | `AzureAd:ClientSecret` | the Microsoft Graph `ClientSecretCredential` (app-only, client-credentials flow) |
| `Anthropic--ApiKey` | `Anthropic:ApiKey` | the Anthropic chat client |
| `AmazonBedrock--ApiKey` | `AmazonBedrock:ApiKey` | the Amazon Bedrock chat client |

None of these nine keys appears in the committed `appsettings.json` — they are secret-classified settings, and the file ships only the non-secret defaults around them ([Document Upload and Ingestion §9](../documents/upload-workflow.md#9-configuration) shows the same split for the ones that feed ingestion). Moving one into Key Vault is a drop-in replacement for wherever it was resolving from before; nothing about the setting's own name or the code that reads it changes.

## 5. Precedence

`AddEnterpriseKeyVault` appends the vault **last** in the configuration chain, so a secret set in Key Vault outranks the same key from `appsettings.json`, an environment variable, user secrets, or the command line — the standard "last source wins" rule `IConfiguration` already applies everywhere else.

**Consequence to keep in mind: do not also resolve the same key through an App Service Key Vault reference** (`@Microsoft.KeyVault(SecretUri=…)`) set as an app setting. An App Service reference resolves into an environment variable, and this provider is appended after it, so the in-process value always wins — deterministically, which is precisely the problem: the platform-resolved secret becomes a silent no-op, and rotating it appears to do nothing. Pick **one mechanism per key** — this in-process provider, or a platform-resolved reference — never both for the same setting.

## 6. Two traps

### 6.1 `CosmosDb--ContainerId` bricks the deployment — correctly

`Program.cs` refuses to start if `CosmosDb:ContainerId` appears **anywhere** in merged configuration, from any source — that is the transcript cutover gate, and it exists because the application can no longer read documents under the pre-cutover container id (see [Transcript Cutover](../conversations/transcript-cutover.md)). A Key Vault secret named `CosmosDb--ContainerId` is merged into configuration exactly like any other source, so writing one trips the same gate the same way an environment variable would.

That is the gate working as designed, not a bug in this provider. The fix is the one the gate's own message already gives: remove the setting — from whichever source is supplying it, vault included — and use `CosmosDb:TranscriptContainerId` instead.

### 6.2 `APPLICATIONINSIGHTS_CONNECTION_STRING` stays a plain setting

The Application Insights connection string is resolved specifically as an environment variable or the bound `AzureMonitor:ConnectionString` (see [Request Logging §7.1](../observability/request-logging.md#71-registration)) — never through this provider, and never as a platform Key Vault reference either. App Service's own portal-based telemetry surfacing depends on reading that value as a literal string at deploy time; resolving it indirectly, through either mechanism, breaks that surfacing without changing whether the exporter itself works.

## 7. Failure behaviour

**Enabled but unreachable or forbidden fails startup, deliberately, before any other configuration is read.** `AddEnterpriseKeyVault` runs first (§3.1), so a vault the identity cannot reach fails the whole app before a database connection string, an LLM key, or anything else gets a chance to be missing.

Measured against a deliberately unreachable vault: a DNS failure surfaces in roughly **44 seconds**; a black-holed endpoint (one that accepts the connection and never responds) takes roughly **107 seconds** — six attempts (one initial plus five retries) at a 10-second per-attempt `NetworkTimeout`, with exponential backoff between attempts from 2s up to a 16s ceiling.

The alternative — falling back silently to whatever other sources are configured — would boot the app with secrets missing, and the failure would surface later as an options-validation error naming a setting that is, in fact, configured: just in the vault the app never reached. Failing fast here, before any of that validation runs, is what keeps the error pointing at the actual cause.

**Half-configured also fails fast.** `KeyVault:Enabled: true` with a blank or missing `VaultUri` is treated as a deployment mistake, not a request to stay local — a template that leaves a variable unset writes an empty setting, not no setting — and `Resolve` throws with a message naming the exact key (`KeyVault:VaultUri`) rather than failing somewhere downstream with no clue which setting was the problem.

## 8. Rotation — and its caveat

`ReloadInterval` is optional. Left `null` (the default), the vault is read **once**, at startup — the same lifetime every other configuration source already has. Setting it polls the vault on that interval instead, and a value under one minute is rejected outright rather than silently clamped: Key Vault throttles requests per vault, and a faster poll buys nothing except a better chance of a `429`.

Two caveats, both load-bearing, and both worth reading before turning polling on:

### 8.1 Most consumers see nothing when a secret rotates

Most settings in this application are bound through `IOptions<T>` — a singleton snapshot, read once when the container is built. A configuration reload updates the underlying `IConfiguration` values, but a component holding `IOptions<AzureOpenAIOptions>` (or `CosmosOptions`, or any of the others) keeps the value it started with. Only two shapes of consumer actually see a rotated value without a restart:

- Code that re-reads `IConfiguration` on every use rather than binding an options type once — [`DocumentStorage`](../../enterprise-gpt-api/Enterprise.Gpt.Service/DocumentStorage.cs), [`JobStatusStore`](../../enterprise-gpt-api/Enterprise.Gpt.Service/BackgroundJobs/JobStatusStore.cs), and [`DocumentIntelligenceService`](../../enterprise-gpt-api/Enterprise.Gpt.Service/DocumentIntelligenceService.cs) all do this today, incidentally rather than because of this feature.
- Code that explicitly watches a change token, the way [`SheetQueryOptionsProvider`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/SheetQueryOptionsProvider.cs) does for `SheetQuery:Enabled` (see [Sheet Query](../documents/sheet-query.md)).

Everything else — a rotated SQL connection string, a rotated Cosmos key, a rotated OpenAI API key — needs a restart to actually take effect, `ReloadInterval` or not. Polling narrows the window between "the vault has a new value" and "the app is holding it," for exactly the consumers above; it does not remove the restart most secrets still need.

### 8.2 A revoked role assignment is invisible from inside the app

The Azure SDK's own reload loop (`Azure.Extensions.AspNetCore.Configuration.Secrets` 1.5.2) swallows every exception its polling task raises, unconditionally, and logs nothing about it. So once polling is on, a revoked **Key Vault Secrets User** role assignment, a deleted vault, or a firewall change is indistinguishable — from inside this application's own logs — from an ordinary transient network blip. The app keeps serving on whatever secrets it last successfully read, silently, for as long as that condition persists.

That makes the vault's own `AuditEvent` diagnostics, sent to Log Analytics, the **only** way to observe this failure mode — not optional monitoring to add later, but a precondition for turning `ReloadInterval` on at all. An operator who enables polling without also wiring those diagnostics has no way to learn the app has drifted onto stale secrets until something downstream — an expired key, most likely — fails loudly on its own.

## 9. Azure setup

The vault must use **RBAC authorization**, not the legacy access-policy model, and the application's identity needs the **Key Vault Secrets User** role at vault scope — read access to secret values, nothing broader.

Authentication goes through `DefaultAzureCredential`, so:

- **Locally**, `az login` is sufficient — the credential chain picks up the signed-in Azure CLI (or Visual Studio) account.
- **Deployed**, a managed identity is expected. `ManagedIdentityClientId` sets **both** legs of the credential — the managed-identity credential's own client id and the workload-identity credential's client id — so a single setting selects the right user-assigned identity whether the app is hosted on App Service (managed identity) or in AKS behind workload identity federation; leaving it unset uses the system-assigned identity (or, locally, the developer's own signed-in account) on either host.

## 10. Known boundary: token acquisition is not covered by the measured figures

The 10-second per-attempt `NetworkTimeout` in §7 bounds only the requests this provider's `SecretClient` makes to the vault itself. Acquiring the token that authenticates those requests — `DefaultAzureCredential` reaching IMDS, or the Entra authority, depending on which credential in its chain succeeds — keeps the Azure SDK's own default timeout, effectively **100 seconds** per attempt. A vault that is perfectly reachable, sitting behind an IMDS endpoint or an authority endpoint that is itself black-holed, is therefore **not** covered by the ~44s/~107s figures above; startup can take substantially longer in that specific failure mode.

This was left deliberately rather than tuned blind: token acquisition is a different call, against a different endpoint, with its own retry semantics inside the credential chain, and narrowing it without measuring a real IMDS or authority outage risked trading a slow, honest failure for a fast, wrong one.

## 11. No health check, deliberately

There is no `ready`-tagged health check for Key Vault, and that is intentional, not an oversight. Secrets are read once, at startup (or on the configured `ReloadInterval`, §8) — never on the request path — so a vault outage after the app has booted has no effect on whether the app can keep serving requests. A `ready` check that depended on the vault would take a healthy, fully-functional instance out of load-balancer rotation for a dependency it is, at that point, no longer actually using.

The posture is fail-fast at startup (§7) plus the log line below — nothing more, and nothing less:

```
Configuration is reading secrets from key vault contoso.vault.azure.net (user-assigned identity: True, reload interval: 01:00:00)
```

[`KeyVaultConfiguration.LogStatus`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Configuration/KeyVaultConfiguration.cs) writes this once, right after the vault is added, and reports only the **vault host**, whether a **user-assigned identity** was configured, and the **reload interval** — never a secret value, a secret name, or the managed identity's client id. Which secrets a deployment holds, and which identity it authenticates as, are both worth keeping out of a log sink.

## 12. Relationship to the infra PRD

[`docs/prd/azure-infrastructure/azure-infrastructure.md`](../prd/azure-infrastructure/azure-infrastructure.md) EP-4 designs a different answer to the same problem: Key Vault secrets resolved as **App Service Key Vault references**, with the platform doing the resolution and no C# change required at all. That PRD is currently **0 of 45 stories done** — none of its infrastructure exists yet.

This in-process provider is not a partial implementation of that design; it is the **host-agnostic complement** to it. It needs no App Service, no Terraform, and no platform-managed identity wiring to work — `dotnet user-secrets` and an `az login` are enough locally, and any host that can run `DefaultAzureCredential` can use it in production. The two are **alternatives per secret, not layers**: §5 already states the operational reason not to configure both for the same key. Which mechanism a given deployment uses is a per-key choice, not a global one.

`docs/prd/` is owned by the PRD workflow; this document does not edit it.

## 13. Testing

[`KeyVaultConfigurationTests`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Configuration/KeyVaultConfigurationTests.cs) covers `Resolve` and `AddEnterpriseKeyVault` without reaching a real vault — the enabled-and-well-formed path is asserted against the `KeyVaultSource` `Resolve` returns, so only the refusal paths (missing section, disabled, `Testing` environment, missing/blank/malformed `VaultUri`, a `ReloadInterval` under one minute) and the `Enabled: false` non-registration path run end to end against `WebApplicationBuilder`. One test binds the API's own shipped `appsettings.json` directly (linked into the test project's output, not copied into a literal) to hold that the committed defaults add no vault — a `true` accidentally committed there would point every environment at whatever vault the file happened to name.

```bash
# from enterprise-gpt-api/
dotnet test --filter "FullyQualifiedName~KeyVaultConfigurationTests"
```

## 14. Key files

| Concern | File |
|---|---|
| Options bound from the `KeyVault` section | [`Api/Configuration/KeyVaultOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Configuration/KeyVaultOptions.cs) |
| Resolution, validation and registration | [`Api/Configuration/KeyVaultConfiguration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Configuration/KeyVaultConfiguration.cs) |
| The validated result of resolution | [`Api/Configuration/KeyVaultSource.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Configuration/KeyVaultSource.cs) |
| Wiring order and the startup log line | [`Api/Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) |
| Defaults shipped (`Enabled: false`) | [`Api/appsettings.json`](../../enterprise-gpt-api/Enterprise.Gpt.Api/appsettings.json) |
| Unit tests | [`tests/Enterprise.Gpt.Unit.Test/Configuration/KeyVaultConfigurationTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Configuration/KeyVaultConfigurationTests.cs) |
| The transcript cutover gate this section can trip (§6.1) | [`docs/conversations/transcript-cutover.md`](../conversations/transcript-cutover.md) |
| The platform-resolved alternative design (§12) | [`docs/prd/azure-infrastructure/azure-infrastructure.md`](../prd/azure-infrastructure/azure-infrastructure.md) |
