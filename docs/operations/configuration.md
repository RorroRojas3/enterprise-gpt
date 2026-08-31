# Configuration

Everything a deployment sets, where secrets come from, and the conventions the whole codebase
follows.

## The `ValidateOnStart` convention

Nearly every options block is registered
`AddOptions<T>().Bind(...).ValidateDataAnnotations().Validate(...).ValidateOnStart()`. A bad value
fails the host rather than the first request that reaches it. When adding configuration, follow it —
an unvalidated setting surfaces months later as an unexplained failure in an unrelated feature.

## Sections

### Host and identity

| Section | Holds |
| --- | --- |
| `ConnectionStrings:DefaultConnection` | SQL Server |
| `AzureAd` | `TenantId`, `ClientId`, `ClientSecret` — bound three times: JWT validation, token acquisition, Graph |
| `CorsOrigins` | Allowed browser origins; the dev client's `http://localhost:4200` is the only one by default |
| `KeyVault` | See below |
| `Logging` | Standard .NET log levels |

### Providers

`AzureOpenAI` is required. `AzureAIFoundry`, `AmazonBedrock` and `Anthropic` each carry an `Enabled`
flag and are skipped entirely when false. See [../models/providers.md](../models/providers.md).

Configure `AzureOpenAI:Url` as the resource **root** — a URL already carrying `/openai/v1` fails at
startup.

### Storage and AI services

| Section | Holds |
| --- | --- |
| `AzureStorage` | `ConnectionString`, `DocumentsContainer`, `GeneratedContainer` |
| `DocumentIntelligence` | `Endpoint`, `ApiKey` |
| `CosmosDb` | `ConnectionString`, `DatabaseId`, `TranscriptContainerId` |

### Feature subsystems

| Section | Documented in |
| --- | --- |
| `Documents`, `Documents:Chunking`, `Documents:Retrieval` | [ingestion](../documents/ingestion.md), [retrieval](../documents/retrieval.md) |
| `Sheets`, `Sheets:RowWindow`, `SheetQuery` | [ingestion](../documents/ingestion.md), [sheet-query](../documents/sheet-query.md) |
| `Summarization` | [summarization](../documents/summarization.md) |
| `FileAgent` | [file-agent](../tools/file-agent.md) |
| `Export`, `Export:Pdf` | [export](../conversations/export.md) |
| `Tokenization` | [transcripts](../conversations/transcripts.md) |
| `Reports`, `BackgroundJobs`, `Tools:Weather` | — |
| `Mcp:Cache`, `Permissions:Cache` | [mcp-servers](../tools/mcp-servers.md), [auth](../architecture/auth-and-permissions.md) |
| `RequestLogging`, `RequestLogging:Bodies`, `AzureMonitor` | [telemetry](../observability/telemetry.md) |

### The frontend

The client has no build-time environments and no `fileReplacements`. `main.ts` fetches
`public/config.json` before bootstrap and validates it with a narrow assertion; a missing or invalid
file renders the fatal shell and does not bootstrap. The committed copy is the development one and
is replaced per environment.

## Key Vault

```json
"KeyVault": {
  "Enabled": false,
  "VaultUri": "",
  "ManagedIdentityClientId": null,
  "DataProtectionKeyUri": null,
  "ReloadInterval": null
}
```

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Whether a vault joins the configuration chain at all. Off means nothing about Key Vault happens — no credential is constructed, no request leaves the process |
| `VaultUri` | empty | Required once enabled; must be an absolute `https://` URI |
| `ManagedIdentityClientId` | `null` | A **user-assigned** identity's client id. Unset means system-assigned, or, locally, the signed-in Azure CLI or Visual Studio account |
| `DataProtectionKeyUri` | `null` | Versionless key identifier wrapping the Data Protection key ring. Unrelated to secret resolution |
| `ReloadInterval` | `null` | Re-read cadence; `null` reads once at startup, and anything under a minute is rejected |

A vault is added **if and only if** `Enabled` is true **and** the host is not in the `Testing`
environment. The `Testing` check exists so the integration-test host never blocks on a credential
exchange it can never complete.

`AddEnterpriseKeyVault()` is the **first line** of `Program.cs`, and that ordering is load-bearing
rather than stylistic: `IConfigurationBuilder` rebuilds its composed view every time a source is
appended, so adding the vault first means every subsequent `Bind` and `ValidateOnStart` already sees
the vault's values merged in.

### Secret naming

Key Vault secret names may not contain `:`, so the provider treats `--` as the substitute: a secret
named `Section--Key` arrives as `Section:Key`, indistinguishable from the same key set any other
way.

| Secret name | Arrives as |
| --- | --- |
| `ConnectionStrings--DefaultConnection` | SQL Server |
| `CosmosDb--ConnectionString` | Transcript store |
| `AzureStorage--ConnectionString` | Blob storage |
| `DocumentIntelligence--ApiKey` | Document Intelligence |
| `AzureOpenAI--ApiKey` | Chat, embeddings and the File Agent |
| `AzureAIFoundry--ApiKey` | Foundry chat |
| `AzureAd--ClientSecret` | Graph, client-credentials flow |
| `Anthropic--ApiKey` | Anthropic chat |
| `AmazonBedrock--ApiKey` | Bedrock chat |

None of these nine appears in the committed `appsettings.json`; the file ships only the non-secret
defaults around them.

### Precedence, and the trap

The vault is appended **last**, so a secret in Key Vault outranks the same key from
`appsettings.json`, an environment variable, user secrets or the command line.

**Do not also resolve the same key through an App Service Key Vault reference.** A platform
reference resolves into an environment variable, and this provider is appended after it, so the
in-process value always wins — deterministically, which is precisely the problem: the
platform-resolved secret becomes a silent no-op and rotating it appears to do nothing. Pick one
mechanism per key.

**`APPLICATIONINSIGHTS_CONNECTION_STRING` stays a plain setting** — never through this provider and
never as a platform reference. App Service's portal-based telemetry surfacing reads that value as a
literal string at deploy time; resolving it indirectly breaks the surfacing without changing whether
the exporter works.

**A `CosmosDb--ContainerId` secret bricks the deployment, correctly.** Startup refuses if
`CosmosDb:ContainerId` appears anywhere in merged configuration, from any source. That is the
transcript cutover gate — see [runbooks.md](runbooks.md). The fix is the one the message gives: use
`CosmosDb:TranscriptContainerId`.

### Failure behaviour

**Enabled but unreachable or forbidden fails startup, before any other configuration is read.** A
silent fallback would boot the app with secrets missing, and the failure would surface later as an
options-validation error naming a setting that *is* configured — just in the vault the app never
reached.

Against a deliberately unreachable vault, a DNS failure surfaces in roughly 44 seconds; a
black-holed endpoint takes roughly 107 seconds (six attempts at a 10-second network timeout with
exponential backoff).

**Half-configured also fails fast.** `Enabled: true` with a blank `VaultUri` is a deployment
mistake, not a request to stay local — a template that leaves a variable unset writes an empty
setting, not no setting — and the error names the exact key.

### Rotation

With `ReloadInterval` set, a rotated secret is picked up on the next read. The caveat: a client
constructed once at startup from a secret keeps the old value until the process restarts. Settings
read on each use — the blob container names, for instance — pick up a change immediately.

## Local development

```bash
cd enterprise-gpt-api/Enterprise.Gpt.Api
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<connection string>"
dotnet user-secrets set "AzureOpenAI:Url" "https://my-resource.services.ai.azure.com"
dotnet user-secrets set "AzureOpenAI:ApiKey" "<key>"
```

Never put a secret in `appsettings.json` — it is checked in.

The dev client also needs `dotnet dev-certs https --trust`. Until that is done, every call to
`https://localhost:7045` fails while *looking* like a CORS error.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Api/appsettings.json` | Non-secret defaults for every section |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Configuration/KeyVaultConfiguration.cs` | When a vault is added, and the failure rules |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Configuration/DataProtectionConfiguration.cs` | The key ring and its wrapping key |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Settings/` | The options classes, one per section |
| `enterprise-gpt-ui/public/config.json` | The client's runtime configuration |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/` | Binds the shipped `appsettings.json` in config tests |

## Related

- [runbooks.md](runbooks.md)
- [../models/providers.md](../models/providers.md)
- [../observability/telemetry.md](../observability/telemetry.md)
