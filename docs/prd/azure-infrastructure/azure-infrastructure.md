# PRD: Terraform-Managed Azure Infrastructure and Deployment

## 1. Overview

**Problem.** Enterprise GPT has no infrastructure-as-code and no deployment path. A repo-wide search finds no `*.tf`, `*.bicep`, ARM template, `azure.yaml`, `Dockerfile`, or `azure-pipelines.yml`; `.github/workflows/` holds only `api-ci.yml` and `ui-ci.yml`, both of which build and test but deploy nothing. There is no `appsettings.Development.json` either — every secret the API needs comes from .NET user secrets on a developer machine, and nothing in the repo records what a deployed environment must provide. That gap is now blocking: the API validates most of its Azure configuration with `ValidateOnStart`, so a half-configured environment fails at boot rather than on first use — correct behaviour, but it means an environment is either completely specified or completely dead, and today nothing specifies one. Meanwhile the application has accumulated a real Azure surface with no home to run on: SQL Server 2025 with vector search, Cosmos DB for transcripts, Blob Storage for uploaded and generated documents, Document Intelligence for OCR, Azure OpenAI for chat/embeddings/Code Interpreter, Entra ID for auth, and an Azure Monitor exporter that is silently inert without a connection string.

**Solution.** A Terraform configuration that stands up a complete, working environment from nothing — dev and prod — on Windows App Service, with every secret in Key Vault, no secret in git, and app settings wired so the existing C# reads them unchanged. The work splits into a **global, one-time bootstrap tier** (PowerShell, run by a human with Owner, never by Terraform or the pipeline) that creates the Terraform state backend and the GitHub OIDC deploy identities, and a **per-environment Terraform tier** (`infra/platform/`) that provisions everything the application reads: compute (two Windows web apps plus a placeholder Function App), observability (workspace-based Application Insights wired into the exporter that already exists and does nothing today), data (SQL, Cosmos, Blob Storage, Document Intelligence, Azure OpenAI), and a Key Vault whose references resolve through a user-assigned managed identity created and role-granted before any app exists. Three GitHub Actions workflows (`infra-cd.yml`, `api-cd.yml`, `ui-cd.yml`) authenticate over OIDC with no stored credential and deploy on merge to `master`. A small set of application-side fixes — an SPA `web.config`, an anonymous `/health` endpoint, an API `web.config` that disables IIS dynamic compression for SSE — ship alongside the infrastructure because the infrastructure cannot make a broken deployment behavior correct.

**Success criteria.**

- **Time to a working environment.** Today, 0 environments exist and standing one up has no recorded procedure at all; target: `terraform apply` against an empty subscription, following the bootstrap runbook, produces a dev environment where the API passes every `ValidateOnStart` check and the SPA signs in — measured by EP-8's end-to-end smoke test completing within one CI run of `infra-cd.yml` + `api-cd.yml` + `ui-cd.yml`.
- **Secret hygiene.** Today 100% of the API's required secrets live only in a developer's local user-secrets store, and 0% are centrally managed; target 100% of the 7 identified secret-classified settings (SQL connection string, Cosmos connection string, storage connection string, Document Intelligence key, Azure OpenAI key, `AzureAd:ClientSecret`, AI Foundry key) resolve through a versionless Key Vault reference with 0 plaintext secrets committed to git, measured by a `.tftest.hcl` assertion over every secret-classified app setting plus a CI secret-scan step with 0 findings.
- **Telemetry activation.** Today `TelemetryRegistration.AddEnterpriseTelemetry` finds no `APPLICATIONINSIGHTS_CONNECTION_STRING` and skips the OpenTelemetry distro entirely, so 100% of request logs, exceptions, and GenAI spans are generated and dropped; target 100% of deployed API and Function App instances emit a `request` telemetry item correlated by `operation_Id` to the `traceId` already returned on every Problem Details response, measured by an Application Insights query run as part of EP-2's exit criterion.
- **State recoverability.** Today there are 0 remote state files and 0 recorded restore procedures; target a `dev.tfstate` blob version is listed and restored into a scratch copy in under 15 minutes, measured by the rehearsed `Restore-TerraformState.ps1` run specified in EP-9.
- **Configuration drift.** Today there are 0 automated checks over any deployment configuration; target a second consecutive `terraform plan` against dev shows zero resource diff, and `tflint` plus a Terraform security scanner report 0 criticals, measured by `infra-cd.yml`'s plan step and EP-7's scan gate.

Sam is the platform engineer who inherits this problem. Following the bootstrap README, Sam runs `New-TerraformBackend.ps1` once — it reports what it created the first time and "no changes" the second, proving the state backend is real and idempotent — then `New-DeployIdentity.ps1`, which mints three federated GitHub OIDC identities with no client secret to leak. Sam opens a pull request against `infra/platform/`; `infra-cd.yml` runs `terraform plan` as the read-only `plan` principal and posts the diff. On merge, the `deploy-dev` principal applies it: two Windows web apps, a Log Analytics workspace, SQL, Cosmos, Blob Storage, Document Intelligence, Azure OpenAI, and a Key Vault whose seven secrets the app already knows how to read. `api-cd.yml` and `ui-cd.yml` deploy the existing build artifacts unmodified. A chat user opens the SPA, signs in against the tenant, uploads a document, and starts a streaming conversation that never stalls — and back in Application Insights, that exact request shows up as a `request` telemetry item carrying the same `traceId` the user would have seen in a Problem Details body had it failed. Weeks later, when it's time to promote to prod, Sam re-runs the identical Terraform configuration with prod tfvars, behind a GitHub Environment approval gate — the same code, a different set of numbers.

## 2. Goals & non-goals

**Goals.**

- Provision every Azure resource the deployed API and SPA read today, on **Windows** App Service throughout (API, SPA, and a placeholder Function App), with no C# code change required to consume it.
- Keep every secret in Key Vault, resolved through **Key Vault references** (`@Microsoft.KeyVault(SecretUri=…)`) via a user-assigned managed identity, with no secret ever committed to git and no Terraform apply that can leave an app booting against a literal, unresolved reference string.
- Support **dev and prod** from one root Terraform configuration with per-environment tfvars and a partial backend config per environment, storing remote state in Azure Storage with **no storage key ever existing** (`use_azuread_auth = true`, `use_oidc = true`).
- Make Terraform's own state genuinely recoverable: blob versioning, blob and container soft delete, point-in-time restore, and change feed on the state storage account, with a rehearsed restore procedure, not just a documented one.
- Provision Azure OpenAI (account plus chat and embedding deployments) through Terraform, since no external AI Foundry project is assumed to exist for it.
- Wire **Application Insights** (workspace-based, per environment) into the API and the Function App with zero code change, turning on telemetry the exporter already emits and silently drops today.
- Ship **deploy pipelines** (`infra-cd.yml`, `api-cd.yml`, `ui-cd.yml`) authenticating over GitHub OIDC, with `deploy-prod` gated by GitHub Environment reviewers.
- Fix the small set of application-side defects that make a deployed environment unusable regardless of how correct the infrastructure is: the SPA's missing IIS fallback config, SSE getting buffered by IIS dynamic compression, a health check that can get a serverless SQL instance replaced, and the `CorsOrigins` array-binding footgun.
- Keep the bootstrap tier (state backend, deploy identities) outside both Terraform and the pipeline, so there is no local state file describing the account that holds every other environment's state.

**Non-goals.**

- **Private networking (VNet, private endpoints, or any network isolation beyond service firewalls).** The locked decision is public endpoints plus firewall rules; a VNet redesign is a separate PRD.
- **Automating the API or SPA Entra app registrations.** Only the three deploy-identity app registrations (`plan`, `deploy-dev`, `deploy-prod`) are Terraform/bootstrap-managed; the application's own Entra registrations stay manual, per the locked decision.
- **Static Web Apps, or any hosting for the SPA other than a second Windows App Service.** Evaluated and rejected in favor of App Service so both apps share one hosting model, one plan type, and one set of Windows-specific fixes.
- **Terraform modules.** Every candidate module boundary here wraps one or two resources deployed exactly twice (dev, prod), which `.claude/rules/terraform.md` explicitly warns against; the bootstrap/platform split is the real "separate projects" seam.
- **Terraform workspaces** in place of partial backend configs — a workspace hides which environment a command targets behind invisible CLI state; this PRD uses explicit `-backend-config` files instead.
- **The Blob SAS / managed-identity refactor.** `BlobStorageService.GenerateSasUri` mints a service SAS today, which needs the storage account's shared key; the keyless path (`GetUserDelegationKey` + `BlobSasBuilder`) is a real C# refactor and the single biggest blocker to a fully keyless architecture. This PRD provisions the storage account with a connection string, as the app expects today.
- **Moving `Database.Migrate()` into the deployment pipeline as an EF migrations bundle.** The app continues to migrate itself at startup, as it does today; a pipeline-driven migration step is future work.
- **Fixing the API's single-instance ceiling.** The in-memory job queue and the MSAL token cache together cap the API at one instance regardless of what App Service plan or scale-out setting Terraform configures; this PRD provisions one instance per environment and does not attempt horizontal scale-out.
- **Shipping fonts for PDF export.** `Export/Fonts/` remains empty; PDF export stays unavailable in every environment this PRD stands up, exactly as it is unavailable today.
- **Browser-side telemetry for the SPA.** No `@microsoft/applicationinsights-web` dependency is added; see §9.
- **Custom domains or DNS.** Both environments serve from their default `*.azurewebsites.net` hostnames; see §9.
- **Documentation stories.** `se-technical-writer` owns `docs/` updates and the `CHANGELOG.md` entry after implementation, per house convention — this PRD names no documentation-authoring story of its own, only the runbooks that are themselves deliverables (EP-9) because they are rehearsed operational procedures, not narrative docs.

## 3. Users & access

**Personas.**

- **Operator**: runs the deployment. Holds subscription Owner for the one-time bootstrap run, owns the per-environment `.tfvars` files and the `TF_VAR_*` GitHub Environment secrets, and is accountable for the region and quota decisions in §9.
- **Platform/DevOps engineer**: owns `infra/` — the Terraform root config, the bootstrap scripts, the three CD workflows, and the `.tftest.hcl` suite. Works under `.claude/rules/terraform.md`.
- **Backend engineer**: owns the EP-5 C# stories (the `/health` endpoint, the API `web.config`, the `CorsOrigins` binding fix) in `enterprise-gpt-api/` under `.claude/rules/csharp.md` and `aspnet-rest-apis.md`.
- **Frontend engineer**: owns the SPA `web.config` and the deploy-time `config.json` generation step in `ui-cd.yml`, in `enterprise-gpt-ui/`.
- **Administrator**: holds `PermissionIds.Administrator` inside the deployed application. Nothing in this PRD adds an admin-facing screen; an administrator's only involvement is that the environment they administer now actually exists and emits telemetry they can query.
- **Chat user**: a signed-in employee. Experiences no functional change from this PRD beyond the application working at all in a deployed environment, and streaming no longer stalling once EP-5's SSE fix ships.

**Role-based access.**

- **`plan` principal** (GitHub OIDC, `pull_request` subject): read-only. Can run `terraform plan` against remote state; holds Reader-equivalent roles only, never Contributor or a Key Vault data-plane role.
- **`deploy-dev` / `deploy-prod` principals** (GitHub OIDC, `environment:` subject, gated by GitHub Environment reviewers on `deploy-prod`): Contributor **and** Role Based Access Control Administrator (Contributor alone cannot create the role assignments this config needs) on their environment's resource group, **Key Vault Secrets Officer** on that resource group, and **Storage Blob Data Contributor** on the global state container. No client secret exists for either — federated credentials only.
- **Operator (human, subscription Owner)**: the only identity that runs bootstrap; holds no standing elevated role afterward beyond whatever the platform engineer's own Azure RBAC already grants for day-to-day operations.
- **Chat user / Administrator**: unaffected by this PRD's access model — both continue to authenticate against the application's own, unchanged Entra ID app registration and permission cache (`IUserPermissionCache`); this PRD changes where the application runs, not who can do what inside it.

## 4. Functional requirements

| ID | Requirement | Priority | Epic(s) |
| --- | --- | --- | --- |
| FR-1 | `New-TerraformBackend.ps1` idempotently creates the global `rg-egpt-tfstate-<loc>` resource group and a storage account with blob versioning, blob and container soft delete (30 days), point-in-time restore, and change feed all enabled, plus a `CanNotDelete` resource lock on the resource group; a second run reports no changes | P0 | EP-1 |
| FR-2 | The state storage account has `shared_access_key_enabled = false` (RBAC-only, no shared keys), enforces TLS 1.2, and disables public blob access | P0 | EP-1 |
| FR-3 | `New-TerraformBackend.ps1` also creates the environment resource groups `rg-egpt-{dev,prod}-<loc>`, consumed by the Terraform platform config through a `data` block and never created by Terraform itself | P0 | EP-1 |
| FR-4 | `New-DeployIdentity.ps1` creates three Entra app registrations (`plan`, `deploy-dev`, `deploy-prod`) with federated credentials on GitHub OIDC subjects and the RBAC role assignments in §3's role table; no client secret is created for any of the three | P0 | EP-1 |
| FR-5 | The Terraform platform root config pins `azurerm ~> 5.3`, sets `resource_providers_to_register` explicitly, and reaches remote state through a partial backend config per environment (`-backend-config=env/{env}.backend.hcl`) with `use_azuread_auth = true` and `use_oidc = true` | P0 | EP-1 |
| FR-6 | `terraform init` and `terraform plan` succeed from GitHub Actions authenticating over OIDC against the remote backend, with no storage account key ever present in a workflow, a secret, or a log | P0 | EP-1 |
| FR-7 | `Restore-TerraformState.ps1` lists the blob-version history of an environment's state file and restores a selected version into a scratch copy without mutating the live state blob | P0 | EP-1 |
| FR-8 | Two Windows `azurerm_service_plan`s are provisioned per environment: the web plan (`B1` dev, `P0v4` prod with `P0v3` as the named regional fallback) and an isolated Windows `Y1` Consumption plan for the Function App placeholder — never Flex Consumption, which is Linux-only | P0 | EP-2 |
| FR-9 | `azurerm_windows_web_app` provisions the API and the SPA as two independent Windows web apps on the shared web plan, each with `use_32_bit_worker = false`, `always_on = true`, `https_only = true`, `client_affinity_enabled = false`, and `WEBSITE_RUN_FROM_PACKAGE = 1` | P0 | EP-2 |
| FR-10 | `site_config.cors` is left unset on both web apps so no platform-level CORS header is emitted alongside the application's own CORS policy | P0 | EP-2 |
| FR-11 | `azurerm_windows_function_app` provisions a placeholder Function App on its own Windows `Y1` plan with its own `azurerm_storage_account`, isolated from the API's plan and blob storage | P0 | EP-2 |
| FR-12 | `azurerm_log_analytics_workspace` and a workspace-based `azurerm_application_insights` (`application_type = "web"`) are provisioned per environment, retention 30 days dev / 90 days prod | P0 | EP-2 |
| FR-13 | `APPLICATIONINSIGHTS_CONNECTION_STRING` is wired as a **plain** app setting (never a Key Vault reference) on both the API app and the Function App, and the App Service codeless auto-instrumentation agent (`ApplicationInsightsAgent_EXTENSION_VERSION`) is never set | P0 | EP-2 |
| FR-14 | `azurerm_monitor_diagnostic_setting` sends both web apps' and the Function App's logs to the environment's Log Analytics workspace | P0 | EP-2 |
| FR-15 | A request against the deployed API appears in Application Insights as a `request` telemetry item whose `operation_Id` correlates to the `traceId` on that request's Problem Details response, with no code change | P0 | EP-2 |
| FR-16 | `azurerm_mssql_server` and `azurerm_mssql_database` provision Azure SQL (`GP_S_Gen5_1`, auto-pause 60 min, dev; `GP_S_Gen5_2`, auto-pause off, prod); Terraform sets nothing toward compatibility level, and the database is never created from a bacpac | P0 | EP-3 |
| FR-17 | `azurerm_mssql_firewall_rule` reaches `Succeeded` with **no dependency on the API web app resource**, and specifically no `for_each` over the app's `possible_outbound_ip_address_list` | P0 | EP-3 |
| FR-18 | `azurerm_cosmosdb_account` provisions transcript storage (`EnableServerless` dev; autoscale 1000 RU/s at the database level, prod) with `local_authentication_enabled = true`, since the app connects with an account key | P0 | EP-3 |
| FR-19 | `CosmosDb:ContainerId` never appears as an app setting under any name; `CosmosDb:TranscriptContainerId` is the only container identifier configured, and `DeleteAllItemsByPartitionKey` is left `false` | P0 | EP-3 |
| FR-20 | `azurerm_storage_account` plus two `azurerm_storage_container`s (`documents`, `generated-documents`) provision Blob Storage (LRS dev, ZRS prod), using `storage_account_id` per the azurerm 5.x container resource shape | P0 | EP-3 |
| FR-21 | `azurerm_cognitive_account` (kind `FormRecognizer`, SKU `S0`) provisions Document Intelligence — never SKU `F0`, which is one per subscription per kind | P0 | EP-3 |
| FR-22 | `azurerm_cognitive_account` (kind `OpenAI`) plus two `azurerm_cognitive_deployment`s provision Azure OpenAI's chat and embedding models; the embedding deployment is confirmed to return exactly 1536 dimensions before it is handed to the app | P0 | EP-3 |
| FR-23 | `azurerm_monitor_diagnostic_setting` sends SQL, Cosmos, and storage diagnostics to the environment's Log Analytics workspace | P1 | EP-3 |
| FR-24 | A user-assigned managed identity is created and granted **Key Vault Secrets User** at vault scope, followed by a `time_sleep` (60s, for Entra RBAC propagation), entirely before any web app resource that references it is created | P0 | EP-4 |
| FR-25 | `azurerm_key_vault` is provisioned with `rbac_authorization_enabled = true`, `network_acls.default_action = "Allow"` (reference resolution comes from platform IPs, not the app's outbound set), and soft-delete retention of 7 days dev / 90 days plus purge protection prod | P0 | EP-4 |
| FR-26 | Seven `azurerm_key_vault_secret` resources hold the SQL connection string, Cosmos connection string, storage connection string, Document Intelligence key, Azure OpenAI key, `AzureAd:ClientSecret`, and the AI Foundry key; every corresponding app setting references its secret **versionlessly** | P0 | EP-4 |
| FR-27 | The API web app's `identity` block carries both `SystemAssigned` and the user-assigned identity, with `key_vault_reference_identity_id` set to the user-assigned identity, so references resolve before `ValidateOnStart` runs rather than after the app has already failed to boot | P0 | EP-4 |
| FR-28 | `azurerm_monitor_diagnostic_setting` sends the Key Vault's `AuditEvent` category to the environment's Log Analytics workspace, so a failed reference resolution is diagnosable rather than an unexplained `500.30` | P0 | EP-4 |
| FR-29 | The deployed API passes every `ValidateOnStart` check and `Database.Migrate()` completes on first boot, with no literal `@Microsoft.KeyVault(...)` string ever observed in a resolved app setting | P0 | EP-4 |
| FR-30 | `enterprise-gpt-ui/public/web.config` rewrites unmatched requests to `index.html` except where `IsFile`/`IsDirectory` match, and serves `config.json` and `index.html` with `Cache-Control: no-cache` | P0 | EP-5 |
| FR-31 | An anonymous, dependency-free `/health` endpoint exists on the API, touching no SQL, Cosmos, or Blob Storage dependency, so a serverless SQL resume in progress cannot fail a health probe and get the instance replaced | P0 | EP-5 |
| FR-32 | The API's `web.config` sets `<urlCompression doDynamicCompression="false" />` so a `text/event-stream` response is never matched by IIS's default dynamic-compression MIME list | P0 | EP-5 |
| FR-33 | A streamed conversation against the deployed environment delivers at least one keep-alive signal within Azure Load Balancer's ~230-second idle-timeout window | P1 | EP-5 |
| FR-34 | `CorsOrigins` app settings are written index-bound (`CorsOrigins__0`, `CorsOrigins__1`, …), and a regression test confirms no origin beyond index 0 in the committed `appsettings.json` silently survives into a generated environment's setting set | P0 | EP-5 |
| FR-35 | `infra-cd.yml` runs `terraform plan` on every pull request touching `infra/platform/` using the `plan` OIDC principal, and `terraform apply` on merge to `master` using the environment-scoped deploy principal, persisting the plan file between the two steps | P0 | EP-6 |
| FR-36 | `api-cd.yml` builds, publishes, and deploys the API to the dev Windows web app on merge to `master`, gated on `api-ci.yml`'s checks passing | P0 | EP-6 |
| FR-37 | `ui-cd.yml` builds the SPA once, generates a per-environment `config.json` at deploy time from GitHub Environment variables, and deploys the identical build artifact to dev, and later to prod on promotion | P0 | EP-6 |
| FR-38 | A merge to `master` deploys the API and the SPA to dev with no manual step | P0 | EP-6 |
| FR-39 | `deploy-prod` runs only after its GitHub Environment's required reviewers approve | P0 | EP-6 |
| FR-40 | `health_check_path` is set on both web apps once `/health` (FR-31) exists | P1 | EP-7 |
| FR-41 | SQL and storage firewall rules are tightened from a bootstrap-time allowlist to the pipeline's and operators' actual outbound IPs, with no wildcard rule left except the documented Key Vault `Allow` (FR-25) | P1 | EP-7 |
| FR-42 | Prod Key Vault purge protection is verified enabled and irreversible, and a daily ingestion cap is set on the dev Log Analytics workspace | P2 | EP-7 |
| FR-43 | `tflint` and a Terraform security scanner run in CI over every `plan` and report 0 criticals | P0 | EP-7 |
| FR-44 | Prod tfvars provision the full per-environment tier at prod-tier SKUs, and prod redirect URIs are added to the existing, manually managed API and SPA Entra app registrations | P0 | EP-8 |
| FR-45 | A first prod `apply` succeeds, and an end-to-end smoke test — API boot, SPA sign-in, a deep link, a document upload and download, a streamed conversation — passes against the prod URLs | P0 | EP-8 |
| FR-46 | A `.tftest.hcl` suite asserts the full required app-setting key list is present (a complete set, not a delta, since there is no `appsettings.Development.json`), `CosmosDb__ContainerId` is absent, and `ASPNETCORE_ENVIRONMENT` is never `Testing` | P0 | EP-9 |
| FR-47 | `terraform-docs` generates and keeps current a README for the bootstrap scripts and for the platform root config | P2 | EP-9 |
| FR-48 | Runbooks exist and are rehearsed — not only written — for secret rotation and for adding a new app setting | P1 | EP-9 |
| FR-49 | Runbooks exist and are rehearsed for Key Vault recovery, Terraform state restore, and a deploy rollback | P0 | EP-9 |

## 5. User experience

**There is no new screen for the chat user or the administrator.** This PRD's "UI" is operational: `terraform plan` output on a pull request, a GitHub Environment approval gate before `deploy-prod`, and Application Insights dashboards an operator queries after the fact. The one user-visible outcome is that the application **works at all** in a deployed environment, and that a streamed conversation no longer stalls once EP-5 ships.

**Entry points & first-time flow.** The Operator runs the two bootstrap scripts once, by hand, with Owner. The Platform engineer then opens a pull request against `infra/platform/`; CI comments the plan. Merging to `master` applies it to dev and, separately, deploys the API and SPA build artifacts. Promoting to prod is a second, explicit path — a workflow run against prod tfvars — gated by required reviewers on the `prod` GitHub Environment.

**Core experience.**

1. Operator runs `New-TerraformBackend.ps1`; a second run reports no changes, proving idempotence before anyone trusts it with state.
2. Operator runs `New-DeployIdentity.ps1`; three OIDC identities exist, none with a client secret.
3. Platform engineer opens a PR touching `infra/platform/*.tf`; `infra-cd.yml`'s `plan` job runs as the read-only principal and posts the diff.
4. Merge to `master` triggers `apply` against dev as `deploy-dev`; the plan file saved from CI is the one applied, not a freshly recomputed one.
5. `api-cd.yml` and `ui-cd.yml` deploy the existing build artifacts to the now-existing dev web apps; `ui-cd.yml` writes a dev-specific `config.json` into the same artifact `api-cd.yml`'s companion built.
6. Weeks later, the same Terraform configuration is applied to prod with prod tfvars, behind reviewer approval, and the same two CD workflows promote the same build artifacts.

**Edge cases & UI states.**

- **Apply fails on Azure OpenAI quota**, not configuration: the error names the quota dimension, and the fix is a support ticket, not a code change (see §9).
- **A Key Vault reference fails to resolve**: the app boots with the literal `@Microsoft.KeyVault(...)` string and `ValidateOnStart` fails; the Key Vault's `AuditEvent` diagnostics (FR-28) are the diagnosis path, not guesswork against a `500.30`.
- **The SQL firewall race**: FR-17 forbids the dependency inversion that would otherwise guarantee a `500.30` on first boot.
- **`deploy-prod` blocked pending review**: the workflow run sits `Waiting` until a required reviewer on the `prod` GitHub Environment approves it; no Terraform apply happens silently.
- **A second consecutive `terraform plan` against dev shows a diff**: treated as a defect in the configuration, not an expected steady state — EP-9's exit criterion is zero diff.

**UI/UX highlights.** None apply to an end user. The operational surfaces — plan diffs, environment approval gates, Application Insights — are the entirety of this feature's "UX," and their legibility (a clear plan diff, a diagnosable Key Vault failure, a `traceId` that round-trips from a user-reported error to a query) is treated with the same rigor a customer-facing screen would get.

## 6. Technical considerations

**Four findings shape the design below, verified against the working tree, the Terraform registry, and Microsoft Learn rather than assumed.**

**1. `azurerm` 5.x changed the exact shape of every resource this PRD touches.** `resource_provider_registrations` now defaults to `none` and `skip_provider_registration` is removed entirely — a fresh subscription's first apply fails with `MissingSubscriptionRegistration` unless `resource_providers_to_register` is set explicitly in the provider block. `azurerm_storage_container` takes `storage_account_id`, not `storage_account_name`. `azurerm_key_vault.rbac_authorization_enabled` is **Required** (renamed from `enable_rbac_authorization`, which no longer exists). `azurerm_cosmosdb_sql_container` takes `partition_key_paths` (a list), not a single `partition_key_path`. Cosmos's `local_authentication_disabled` is renamed `local_authentication_enabled` and inverted — it must be set to `true` here, since the app authenticates with an account key. `azurerm_app_service`, `azurerm_app_service_plan`, and `azurerm_function_app` are **removed**, not deprecated — every reference in this PRD and its Terraform is to `azurerm_service_plan`, `azurerm_windows_web_app`, and `azurerm_windows_function_app`. `azurerm_monitor_diagnostic_setting`'s `metric` block is renamed `enabled_metric` and `retention_policy` is removed (retention is set on the Log Analytics workspace instead).

**2. Windows hosting has real, specific gotchas beyond "pick Windows in the provider block."** Flex Consumption is Linux-only, so the Function App placeholder needs its own classic Windows `Y1` Consumption plan — free at idle, and the Windows Consumption retirement notice covers Linux only. The SPA needs `enterprise-gpt-ui/public/web.config`: the asset glob already copies `public/**` verbatim, so shipping the file needs no build change, but without its `IsFile`/`IsDirectory` negations every asset — including the hashed JS/CSS bundles and `config.json` — gets rewritten to `index.html`, and MSAL's redirect to `/auth` 404s, breaking login completely. SSE has two independent failure modes: ANCM's `responseBufferLimit` does not apply to in-process hosting (a non-issue), but IIS **dynamic compression**'s default MIME list matches `text/*` and therefore `text/event-stream`, buffering the stream until it closes — fixed by `<urlCompression doDynamicCompression="false" />` in the API's own `web.config`. Separately, Azure Load Balancer's ~230-second **idle** timeout drops a stream carrying no traffic, independent of compression. App settings on a Windows web app nest with `__` and index arrays (`CorsOrigins__0`), never a comma-joined string. `use_32_bit_worker` defaults to `true` in the provider and must be forced `false`, or a 2 GB cap OOMs on an embedding batch. `site_config.cors` must be left **unset** — platform CORS layered on top of the app's own policy emits duplicate headers, and browsers reject a response carrying two.

**3. The Key Vault chicken-and-egg is real and has one fix.** A system-assigned identity does not exist until the app is created, but `app_settings` carrying Key Vault references are written at create time — so without a pre-existing identity to grant, the app boots with the literal reference string and `ValidateOnStart` fails on first apply, every time. The fix is a **user-assigned managed identity created and role-granted before any app exists**:

  ```
  azurerm_user_assigned_identity.app
    └─> azurerm_role_assignment.uai_kv_secrets_user  (Key Vault Secrets User, scope = vault)
          └─> time_sleep.wait_for_kv_rbac  (60s, for Entra RBAC propagation)
                └─> azurerm_windows_web_app.api
                      identity { type = "SystemAssigned, UserAssigned", identity_ids = [...] }
                      key_vault_reference_identity_id = <uai>
  ```

  Both identity types matter: the user-assigned identity resolves the references at boot, and the system-assigned one is what a later keyless-storage story would grant Storage/Cosmos data-plane roles to. The vault's `network_acls.default_action` must stay `Allow` — reference resolution comes from platform IPs, not the app's own outbound set, so there is no correct allowlist without a VNet (a non-goal); the control is RBAC plus `AuditEvent` diagnostics instead. The deploy principal needs **Key Vault Secrets Officer**: with RBAC authorization on, plain Contributor does not grant data-plane secret writes.

**4. The application encodes constraints Terraform must not violate, several of them non-obvious.** `UseCompatibilityLevel(170)` is EF-side only — it never issues `ALTER DATABASE`, and Azure SQL already defaults new databases to compatibility level 170, so Terraform sets nothing toward it and the database must never be created from a bacpac. The `vector` column type works at every compatibility level, but `CREATE VECTOR INDEX`/`VECTOR_SEARCH` are preview and roll out per region — a reason the target region is an open question (§9), not a settled input. `CosmosBootstrapper` calls `CreateDatabaseIfNotExistsAsync`/`CreateContainerIfNotExistsAsync` with a specific indexing policy at startup, and that call leaves an existing container alone — if Terraform creates the container first, its indexing policy must reproduce `CosmosBootstrapper`'s exactly, or the app silently runs on a costlier default policy (flagged as a vetoable assumption in §9). `CosmosDb:ContainerId` must be **absent** entirely — its mere presence fails startup by design, distinct from `CosmosDb:TranscriptContainerId`, which is required. `DeleteAllItemsByPartitionKey` cannot be set through ARM, and Terraform goes through ARM, so it stays `false` unless an operator accepts a manual CLI post-step. `ASPNETCORE_ENVIRONMENT` must never be `Testing`, or `Database.Migrate()` is skipped and the app runs against an empty schema — pinned by a `.tftest.hcl` assertion (FR-46). `FileAgentBootstrapper` reads the File Agent's skills/prompt/conversion-matrix content files at startup **regardless of `FileAgent:Enabled`**, so a packaging miss fails the deploy outright, not just the feature. `AzureAIFoundry:Enabled` is `true` in the committed `appsettings.json`, which makes `AzureAIFoundry:Url`/`:ApiKey`/`:DefaultModel` mandatory for `ValidateOnStart` today — this PRD does **not** provision an Azure AI Foundry account (no such resource appears in §"Per-environment tier"); its API key is supplied the same way `AzureAd:ClientSecret` is, as a `sensitive` Terraform variable rather than a resource attribute, on the assumption that an AI Foundry resource already exists or is provisioned out of band (§9). The Entra Application ID URI must stay the default `api://{clientId}`, since audience validation is hard-computed from `ClientId`. The app is framework-dependent portable with no `RuntimeIdentifier`, so Windows is a free hosting choice and the existing ubuntu-built CI artifact deploys to it unchanged.

**Integration points.**

| Concern | Where |
| --- | --- |
| The config surface Terraform must fully populate | `enterprise-gpt-api/Enterprise.Gpt.Api/appsettings.json` — no `AzureAd` or `AzureStorage` section exists there today; two committed keys (`OllamaUrl`, `AzureOpenAI:Enabled`) are dead and are not carried into any generated app setting |
| Startup validation that turns a missing setting into a boot failure | `Program.cs`'s `AddOptions<T>().ValidateDataAnnotations().ValidateOnStart()` chain across every `*Options` class — `SummarizerBootstrapper`/`FileAgentBootstrapper`-style validators run regardless of the corresponding feature flag |
| The telemetry registration this PRD turns on with one setting | `TelemetryRegistration.AddEnterpriseTelemetry` — reads `APPLICATIONINSIGHTS_CONNECTION_STRING`, falling back to `AzureMonitor:ConnectionString`; finding neither, it skips the entire OpenTelemetry distro silently |
| The Cosmos bootstrap behavior Terraform must not disagree with | `CosmosBootstrapper` — `CreateDatabaseIfNotExistsAsync`/`CreateContainerIfNotExistsAsync`, idempotent, leaves an existing container's indexing policy alone |
| The SPA's own config contract | `enterprise-gpt-ui/public/config.json`, validated by `assertAppConfig` — `apiBaseUrl`, `auth.clientId` (GUID-checked), `auth.authority` (https-only, tenant embedded), `auth.redirectUri` (`/auth`), `auth.postLogoutRedirectUri` (`/signed-out`), `auth.apiScopes`, `features.{diagrams,math,rawStreamCodec}` as **strict booleans** — the string `"false"` is rejected. One build artifact is promoted across environments; `config.json` is overwritten per environment by `ui-cd.yml` |
| The download route that must keep working once storage is Terraform-provisioned | `Enterprise.Gpt.Api/Endpoints/DocumentEndpoints.cs`, `Enterprise.Gpt.Service/DocumentService.cs` — ownership read off the parent conversation, a miss is a 404 |
| The permission cache that is unaffected by this PRD | `Service/Caching/UserPermissionCache.cs` — this PRD changes where the app runs, not its authorization model |
| CI that must keep passing unchanged | `.github/workflows/api-ci.yml`, `ui-ci.yml` — this PRD adds three new CD workflows beside them, never modifies them |

**Repository layout.**

```
infra/
  bootstrap/                        # PowerShell, human-run once, no Terraform state
    New-TerraformBackend.ps1        # global state RG + storage (versioning, soft delete, PITR,
                                     #   change feed), env RGs, resource lock — idempotent
    New-DeployIdentity.ps1          # 3 app registrations + federated credentials + RBAC
    Restore-TerraformState.ps1      # list state versions, restore one
    README.md                       # run order, required roles, what to do if state is lost
  platform/                         # the Terraform root config
    providers.tf variables.tf locals.tf data.tf outputs.tf README.md
    observability.tf identity.tf keyvault.tf
    sql.tf cosmos.tf storage.tf ai.tf appservice.tf
    env/{dev,prod}.tfvars + {dev,prod}.backend.hcl
    tests/*.tftest.hcl
enterprise-gpt-ui/public/web.config       # new — SPA fallback
enterprise-gpt-api/.../web.config         # new — disable dynamic compression for SSE
.github/workflows/{infra,api,ui}-cd.yml   # new
.gitignore                                # new Terraform entries
```

No modules; the bootstrap/platform split is the "separate projects per major component" seam `.claude/rules/terraform.md` calls for, since it carries different blast radii and different runners, and bootstrap has no Terraform state at all to protect. Its Security section is AWS-worded (Secrets Manager, IAM, security groups); this PRD translates that to Key Vault, managed identity plus RBAC, and service firewall rules rather than following it literally. Its style rules — 2-space indent, alphabetization, `depends_on` first, `lifecycle` last, `description` and `type` on every variable, `sensitive = true` where it applies, a README per project, and a `.tftest.hcl` suite — apply as written.

**Data storage & privacy.**

- Every data resource (SQL, Cosmos, Blob Storage) is provisioned with public network access plus firewall rules, per the locked networking decision — no VNet, no private endpoint.
- The SQL admin password is Terraform-generated (`random_password`), with `;` and `'` excluded from the character set so the connection string built from it survives unescaped.
- The honest consequence of provisioning secrets from resource attributes is that those values land in Terraform **state** in plaintext — which makes the state storage account itself a Tier-0 asset, hence RBAC-only access, no shared keys, versioning, and 30-day soft delete on it specifically (bootstrap tier, not the per-environment tier).
- `Cells`-equivalent structured data is out of scope here; this PRD's only schema-adjacent concern is that `Database.Migrate()` continues to run at API startup against whatever SQL Server Terraform provisions, unchanged.
- Cosmos, storage, and Document Intelligence secrets are read straight off their own resource attributes at apply time; `AzureAd:ClientSecret` and the AI Foundry API key are supplied as `sensitive` Terraform variables via `TF_VAR_*` from GitHub Environment secrets — never a committed `.tfvars` file.

**Security.**

- No secret is ever committed to git; the seven Key Vault secrets are either Terraform-computed (SQL password) or supplied as `sensitive` variables (Entra client secret, AI Foundry key) and written to Key Vault, never to a `.tfvars` file or a workflow log.
- Deploy identities are federated-credential-only — no client secret exists for `plan`, `deploy-dev`, or `deploy-prod` at any point.
- The Key Vault's RBAC-only authorization model means Contributor alone cannot read or write a secret; every principal that needs to (the deploy principals, the API's user-assigned identity) is granted a specific, named data-plane role.
- `deploy-prod`'s federated credential is scoped to the `environment:` subject specifically, so GitHub Environment protection rules (required reviewers) are a real gate on a prod apply, not a convention.
- The state storage account disables shared keys entirely (`shared_access_key_enabled = false`); every read and write to state goes through Azure AD auth.

**Scalability & performance.**

- Dev and prod diverge structurally, not just by SKU: Cosmos's `EnableServerless` cannot be added to or removed from an existing account, so dev (serverless) and prod (autoscale, 1000 RU/s at the database level) are two different account shapes by design, not a tfvars toggle on one shape.
- SQL uses the General Purpose Serverless tier in both environments, with auto-pause enabled in dev (60 minutes) and disabled in prod — dev cost-optimizes for idle time, prod optimizes for latency.
- The API's own single-instance ceiling (in-memory job queue, MSAL token cache) is a known, out-of-scope constraint (§2); this PRD provisions one instance per environment and does not attempt to work around it.
- Retention is the primary cost lever for observability: 30 days in dev, 90 in prod, set on the Log Analytics workspace, with a daily ingestion cap considered for dev (FR-42).

**AI system requirements.**

- Azure OpenAI is provisioned by Terraform specifically because no other tooling or model call is needed to stand it up — an account plus two `azurerm_cognitive_deployment`s (chat, embedding).
- The embedding deployment's dimensionality is a hard functional requirement, not a nice-to-have: the app's `vector(1536)` columns fail every insert if the deployment returns anything other than 1536 dimensions, so FR-22 requires this confirmed before the deployment is handed to the app.
- Azure OpenAI model quota is allocated **per region and per subscription** — an apply can fail on quota exhaustion with no configuration error at all, which is why the target region and its available quota are open questions (§9) rather than assumed.
- No evaluation benchmark applies here — this PRD provisions infrastructure, not model behavior; the existing application-level AI evaluation (document retrieval, File Agent verification, etc.) is unaffected.

## 7. Epics & user stories

| ID | Epic | Goal | Priority | Estimate | Depends on |
| --- | --- | --- | --- | --- | --- |
| EP-1 | Bootstrap & platform skeleton | An idempotent PowerShell bootstrap creates the state backend, env resource groups, and OIDC deploy identities; the Terraform platform skeleton reaches remote state over OIDC | P0 | L | — |
| EP-2 | Compute & observability | Both web apps and the placeholder Function App exist, wired to workspace-based Application Insights with plain app settings only, no Key Vault yet | P0 | L | EP-1 |
| EP-3 | Data plane | SQL, Cosmos, Blob Storage, Document Intelligence, and Azure OpenAI all exist and are reachable | P0 | L | EP-2 |
| EP-4 | Key Vault & identity | A pre-granted user-assigned identity resolves seven Key Vault references; the API boots, passes `ValidateOnStart`, and migrates | P0 | L | EP-3 |
| EP-5 | App-side enablers | SPA `web.config`, `/health`, API compression `web.config`, SSE keep-alive verification, the `CorsOrigins` binding fix — parallel throughout | P0 | M | — |
| EP-6 | Pipelines | `infra-cd.yml`, `api-cd.yml`, `ui-cd.yml` deploy unattended to dev on merge, with deploy-time `config.json` generation | P0 | L | EP-4 |
| EP-7 | Hardening | Health-check wiring, tightened firewalls, prod purge protection, a dev ingestion cap, `tflint` plus a scanner in CI | P0 | M | EP-6 |
| EP-8 | Production cutover | Prod tfvars, prod Entra redirect URIs, the first prod apply, and an end-to-end smoke test | P0 | M | EP-7 |
| EP-9 | Tests & runbooks | `.tftest.hcl` suite, `terraform-docs`, and rehearsed runbooks for rotation, adding a setting, vault recovery, state restore, and rollback | P0 | M | — |

EP-1 through EP-4 form a strict chain because each later epic reads a resource the one before it created: compute needs the platform skeleton and remote state to exist at all, data-plane resources are sequenced after compute specifically to bank a "first working apply" milestone before the larger data-plane surface is built, and Key Vault needs both compute (an app to attach identity to) and data-plane (resource attributes to read into secrets) already in place. EP-5 has no technical dependency on any infrastructure epic — it is pure application code — and is scheduled to run the entire time the others are in flight; its own verification story (SSE keep-alive against a real deployment) is the one place it reaches forward into what EP-2/EP-6 produce. EP-6 needs a fully working environment (through EP-4) before "deploy on merge" means anything more than deploying into a half-configured app. EP-7 and EP-8 close the loop in the order the plan fixes: harden dev before cutting prod over, never the reverse. EP-9's stories are written to run throughout — a test suite and a runbook are only useful validated against something real — but the epic itself is treated as closed last, once every runbook in it has actually been rehearsed rather than only written.

### EP-1: Bootstrap & platform skeleton

#### US-101: `[enabler]` Idempotent Terraform state backend

- **Story**: `[enabler]` Write `infra/bootstrap/New-TerraformBackend.ps1`, driving the `az` CLI, to create `rg-egpt-tfstate-<loc>` and a storage account + `tfstate` container with blob versioning, blob soft delete (30d), container soft delete (30d), point-in-time restore, and change feed all enabled, plus a `CanNotDelete` lock on the resource group. Unblocks US-102, US-103, US-105.
- **Priority**: P0 · **Estimate**: M · **Depends on**: —
- **Acceptance criteria**:
  - Given a subscription with none of these resources, when the script runs, then the resource group, storage account, and container exist with every setting above enabled.
  - Given the script has already run once, when it runs a second time, then it reports no changes and makes none — the whole contract of a script with no state file.
  - Given the storage account, when its access configuration is inspected, then `shared_access_key_enabled` is `false`, TLS minimum is 1.2, and public blob access is disabled.
  - Given the resource group, when a delete is attempted against it, then the `CanNotDelete` lock blocks it.

#### US-102: `[enabler]` Environment resource groups

- **Story**: `[enabler]` Extend `New-TerraformBackend.ps1` to also create `rg-egpt-dev-<loc>` and `rg-egpt-prod-<loc>`, consumed later by the Terraform platform config through a `data "azurerm_resource_group"` block rather than a Terraform-managed resource. Unblocks US-105.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-101
- **Acceptance criteria**:
  - Given the script runs, when it completes, then both environment resource groups exist, tagged with their environment name.
  - Given the platform Terraform config's `data.tf`, when it is read, then it references these resource groups by `data` block, never by `resource` block — so the deploy principal's RBAC never hangs off a resource the platform config itself could destroy.
  - Given the script runs a second time, then neither resource group is recreated or modified.

#### US-103: `[enabler]` GitHub OIDC deploy identities

- **Story**: `[enabler]` Write `infra/bootstrap/New-DeployIdentity.ps1` to create three Entra app registrations — `plan`, `deploy-dev`, `deploy-prod` — each with a federated credential (`pull_request` subject for `plan`, `environment:dev`/`environment:prod` subjects for the other two) and the RBAC role assignments from §3's role table: `plan` gets Reader-equivalent roles only; `deploy-dev`/`deploy-prod` get Contributor, Role Based Access Control Administrator, Key Vault Secrets Officer (on their environment resource group), and Storage Blob Data Contributor (on the global state container). No client secret is created for any of the three. Unblocks US-105, US-106.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-101
- **Acceptance criteria**:
  - Given the script runs, when the three app registrations are inspected, then each carries a federated credential and zero client secrets.
  - Given `deploy-dev`, when its role assignments are listed, then all four named roles are present at the scopes specified above.
  - Given `plan`, when its role assignments are listed, then it holds only read-equivalent roles — no Contributor, no Key Vault data-plane role.
  - Given the script runs a second time, then no duplicate app registration or role assignment is created.

#### US-104: `[enabler]` Terraform state restore script and runbook

- **Story**: `[enabler]` Write `infra/bootstrap/Restore-TerraformState.ps1`, listing an environment's `dev.tfstate`/`prod.tfstate` blob version history and restoring a chosen version into a named scratch blob, plus `infra/bootstrap/README.md` documenting run order, required roles, and what to do if state is lost. Unblocks US-904.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-101
- **Acceptance criteria**:
  - Given at least two `terraform apply` runs have written `dev.tfstate`, when the script lists versions, then it prints at least two, each with a timestamp.
  - Given a chosen version id, when the script restores it, then a new scratch blob contains that version's content, and the live `dev.tfstate` blob is untouched.
  - Given the README, when it is read, then it states the bootstrap run order (`New-TerraformBackend.ps1` before `New-DeployIdentity.ps1`) and the exact Azure role the operator running it must hold.

#### US-105: `[enabler]` Terraform platform skeleton

- **Story**: `[enabler]` Create `infra/platform/{providers,variables,locals,data,outputs}.tf`, pinning `azurerm ~> 5.3` with `resource_providers_to_register` set explicitly, `env/{dev,prod}.backend.hcl` partial backend configs (`use_azuread_auth = true`, `use_oidc = true`), `env/{dev,prod}.tfvars`, and repo-root `.gitignore` entries for `.terraform/`, `*.tfstate*`, and `.terraform.lock.hcl` overrides. Unblocks US-106, EP-2 through EP-4.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-102, US-103
- **Acceptance criteria**:
  - Given a fresh subscription with no resource providers registered, when `terraform init` and `plan` run for the first time, then no `MissingSubscriptionRegistration` error occurs.
  - Given `env/dev.backend.hcl`, when `terraform init -backend-config=env/dev.backend.hcl` runs, then no storage account key is required or present anywhere in the command or its output.
  - Given the repository root, when `.gitignore` is inspected, then no local Terraform state or plan file can be accidentally committed.

#### US-106: CI plan and apply reach remote state over OIDC

- **Story**: Prove `terraform init` and `terraform plan` succeed from a GitHub Actions job authenticating as the `plan` OIDC principal against the remote backend created in US-101/US-105, with no secret stored in the repository. This is the exit criterion for EP-1 and unblocks EP-6's `infra-cd.yml`.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-105
- **Acceptance criteria**:
  - Given a pull request touching `infra/platform/`, when a CI job runs as the `plan` principal, then `terraform init` and `terraform plan` both succeed against `dev.tfstate`.
  - Given that same job, when its logs are inspected, then no storage account key, client secret, or other long-lived credential appears anywhere in them.
  - Given a `git log` of the repository, when searched, then no `.tfvars` file containing a real secret value has ever been committed.

### EP-2: Compute & observability

#### US-201: `[enabler]` Windows service plans

- **Story**: `[enabler]` Add `azurerm_service_plan` for the shared web plan (`B1` dev, `P0v4` prod, falling back to `P0v3` if `P0v4` is unavailable in the target region) and a second, isolated `azurerm_service_plan` (`Y1`, Windows) for the Function App placeholder, in `appservice.tf`. Unblocks US-202, US-203.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-105
- **Acceptance criteria**:
  - Given the web plan, when it is inspected, then its `os_type` is `Windows` and its SKU matches the environment's tfvars.
  - Given the function plan, when it is inspected, then it is a separate `Y1` Windows Consumption plan, not shared with the web plan.
  - Given `P0v4` is unavailable in the applied region, when `plan` runs against `env/prod.tfvars`, then the fallback SKU (`P0v3`) is selectable via a single tfvars change, not a code change.

#### US-202: API and SPA web apps

- **Story**: Add two `azurerm_windows_web_app` resources — API and SPA — on the shared web plan, each with `use_32_bit_worker = false`, `always_on = true`, `https_only = true`, `client_affinity_enabled = false`, `WEBSITE_RUN_FROM_PACKAGE = 1`, and `site_config.cors` left unset. Unblocks US-205, US-401, US-602, US-603.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-201
- **Acceptance criteria**:
  - Given the API app, when its site config is read, then `use32BitWorkerProcess` is `false` and `alwaysOn` is `true`.
  - Given either app, when an HTTP (non-HTTPS) request is made to it, then it receives a 307 redirect, confirming `https_only` is enforced.
  - Given either app's `site_config`, when it is read, then no `cors` block is present at all.
  - Given the SPA app, when its app settings are read, then no application-level app settings collide with what `ui-cd.yml`'s deploy-time `config.json` generation later writes.

#### US-203: `[enabler]` Function App placeholder

- **Story**: `[enabler]` Add `azurerm_windows_function_app` on the `Y1` plan from US-201, with its own dedicated `azurerm_storage_account`, isolated from the API's data plane. Unblocks US-205.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-201
- **Acceptance criteria**:
  - Given the Function App, when its plan is inspected, then it is the `Y1` plan, not the API's `B1`/`P0v4` web plan.
  - Given the Function App's storage account, when inspected, then it is a distinct resource from the Blob Storage account EP-3 provisions for documents.
  - Given the Function App with no functions deployed, when it is browsed, then it returns the default placeholder response rather than an error.

#### US-204: Log Analytics and Application Insights per environment

- **Story**: Add `azurerm_log_analytics_workspace` (30d retention dev, 90d prod) and a workspace-based `azurerm_application_insights` (`application_type = "web"`) per environment, in `observability.tf`. Unblocks US-205, US-703.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-105
- **Acceptance criteria**:
  - Given the App Insights resource, when its `workspace_id` is read, then it points at the environment's own Log Analytics workspace — never classic, workspace-less App Insights, which is retired.
  - Given dev's workspace, when its retention is read, then it is 30 days; given prod's, then it is 90.
  - Given dev and prod, when their App Insights resources are compared, then each environment has its own instance — never shared.

#### US-205: `[enabler]` Wire Application Insights into the API and Function App

- **Story**: `[enabler]` Set `APPLICATIONINSIGHTS_CONNECTION_STRING` as a plain app setting (never a Key Vault reference) on both `azurerm_windows_web_app.api` and `azurerm_windows_function_app.placeholder`, reading the connection string off `azurerm_application_insights.main.connection_string`. Explicitly do not set `ApplicationInsightsAgent_EXTENSION_VERSION` on either app. Unblocks US-206.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-202, US-203, US-204
- **Acceptance criteria**:
  - Given the API app's settings, when read, then `APPLICATIONINSIGHTS_CONNECTION_STRING` is a literal connection string, not a `@Microsoft.KeyVault(...)` reference.
  - Given both apps' settings, when read, then neither sets `ApplicationInsightsAgent_EXTENSION_VERSION` — the codeless auto-instrumentation agent stays off, since the app ships its own OpenTelemetry distro.
  - Given the Function App's settings, when read, then it carries the same connection string as the API app, from the same environment's App Insights resource.

#### US-206: Diagnostics on compute, and the first working apply

- **Story**: Add `azurerm_monitor_diagnostic_setting` on both web apps and the Function App, sending logs to the environment's Log Analytics workspace. This story is EP-2's exit criterion: both apps serve their default page, and a request to the API is observable end to end in Application Insights.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-202, US-203, US-205
- **Acceptance criteria**:
  - Given a fresh `terraform apply` of everything through this story, when the API and SPA URLs are opened, then both serve an HTTP 200 (even absent a full config, in the API's case a Problem Details response rather than a connection failure).
  - Given a request made to the API's default route, when Application Insights is queried a few minutes later, then a `request` telemetry item exists carrying a `traceId` correlatable to any Problem Details response from that same request.
  - Given the diagnostic settings, when the Log Analytics workspace is queried, then log entries from both web apps and the Function App are present.

### EP-3: Data plane

#### US-301: Azure SQL, sequenced ahead of the API

- **Story**: Add `azurerm_mssql_server`, `azurerm_mssql_database` (`GP_S_Gen5_1`, auto-pause 60 min, dev; `GP_S_Gen5_2`, auto-pause off, prod), and `azurerm_mssql_firewall_rule` in `sql.tf`. The admin password is a Terraform-generated `random_password` with `;` and `'` excluded from its character set. The firewall rule carries **no dependency on the API web app** — specifically no `for_each` over `possible_outbound_ip_address_list`. Unblocks US-306, US-403.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-105
- **Acceptance criteria**:
  - Given the SQL server and firewall rule, when their resource graph is inspected, then the firewall rule has no dependency edge to the API web app resource.
  - Given the database, when its compatibility level is read after creation, then it is 170 (Azure SQL's own default) with no `ALTER DATABASE` ever issued by Terraform.
  - Given the generated admin password, when it is inspected, then it contains neither `;` nor `'`.
  - Given dev's database, when idle for the configured auto-pause window, then it pauses; given prod's, then it never does.

#### US-302: Cosmos DB for transcripts

- **Story**: Add `azurerm_cosmosdb_account` (`EnableServerless` dev; autoscale 1000 RU/s at the database level, prod), `azurerm_cosmosdb_sql_database` (`enterprise-gpt`), and `azurerm_cosmosdb_sql_container` (`enterprise-gpt-transcript`, `partition_key_paths = ["/pk"]`), in `cosmos.tf`, with `local_authentication_enabled = true`. The container's indexing policy is authored to reproduce `CosmosBootstrapper`'s own policy exactly (§9 flags this as a vetoable default). Unblocks US-306, US-403.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-105
- **Acceptance criteria**:
  - Given the Cosmos account, when `local_authentication_enabled` is read, then it is `true`.
  - Given the container, when its `partition_key_paths` is read, then it is `["/pk"]`, matching the app's synthetic `{userId}:{conversationId}` key.
  - Given dev's account, when its capacity mode is read, then it is serverless; given prod's, then it is autoscale at 1000 RU/s and neither mode is later toggled on the same account.
  - Given the container's indexing policy, when compared to `CosmosBootstrapper`'s own policy, then they match — or the story is not accepted until they do.

#### US-303: Blob Storage for documents

- **Story**: Add `azurerm_storage_account` (LRS dev, ZRS prod) and two `azurerm_storage_container`s (`documents`, `generated-documents`) in `storage.tf`, using `storage_account_id` on the container resource per the azurerm 5.x shape. Unblocks US-306, US-403, US-702.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-105
- **Acceptance criteria**:
  - Given the storage account, when its replication type is read, then it is `LRS` in dev and `ZRS` in prod.
  - Given both containers, when listed, then `documents` and `generated-documents` both exist, matching `AzureStorage:DocumentsContainer`/`:GeneratedContainer`.
  - Given the container resources, when their HCL is read, then each references `azurerm_storage_account.main.id`, never `.name`.

#### US-304: Document Intelligence

- **Story**: Add `azurerm_cognitive_account` (kind `FormRecognizer`, SKU `S0`) in `ai.tf`. Unblocks US-403.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-105
- **Acceptance criteria**:
  - Given the cognitive account, when its SKU is read, then it is `S0`, never `F0`.
  - Given a third environment were added later, when `F0` is considered, then the story's own note (F0 is one per subscription per kind) documents why it is not used here.

#### US-305: Azure OpenAI account and deployments

- **Story**: Add `azurerm_cognitive_account` (kind `OpenAI`) and two `azurerm_cognitive_deployment`s (chat, embedding) in `ai.tf`. The embedding deployment's dimensionality is verified to be exactly 1536 before this story is accepted. Unblocks US-403.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-105
- **Acceptance criteria**:
  - Given the embedding deployment, when a test embedding call is made against it, then the returned vector has exactly 1536 dimensions.
  - Given both deployments, when inspected, then their model names and versions match `AzureOpenAI:DefaultModel`/`:EmbeddingModel`'s expected values.
  - Given the OpenAI account is applied in a region with insufficient model quota, when `apply` runs, then the failure names the quota dimension exceeded, not a generic provisioning error.

#### US-306: `[enabler]` Data-plane diagnostics

- **Story**: `[enabler]` Add `azurerm_monitor_diagnostic_setting` on SQL, Cosmos, and the storage account, sending diagnostics to the environment's Log Analytics workspace. This story is EP-3's exit criterion alongside SQL being reachable from an allowlisted IP.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-301, US-302, US-303
- **Acceptance criteria**:
  - Given the three diagnostic settings, when the Log Analytics workspace is queried, then log entries from SQL, Cosmos, and storage are all present.
  - Given an allowlisted IP address, when a SQL client connects to the deployed server, then the connection succeeds.
  - Given every resource in EP-3, when `terraform plan` runs a second time with no manual changes, then it reports zero diff.

### EP-4: Key Vault & identity

#### US-401: `[enabler]` User-assigned identity, granted before any app depends on it

- **Story**: `[enabler]` Add `azurerm_user_assigned_identity.app`, `azurerm_role_assignment.uai_kv_secrets_user` (Key Vault Secrets User, scope = vault), and `time_sleep.wait_for_kv_rbac` (60s) in `identity.tf`, sequenced strictly before the API web app's `identity` block is populated with it. Unblocks US-403.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-105
- **Acceptance criteria**:
  - Given the resource graph, when inspected, then `azurerm_windows_web_app.api`'s `identity_ids` depends transitively on `time_sleep.wait_for_kv_rbac`, which depends on the role assignment, which depends on the identity.
  - Given a fresh `apply` from empty, when it completes, then the API app never observes a literal `@Microsoft.KeyVault(...)` string in a resolved setting — the sleep is long enough for RBAC propagation in practice.
  - Given the identity, when its role assignments are listed, then it holds Key Vault Secrets User at vault scope and nothing broader.

#### US-402: Key Vault provisioning

- **Story**: Add `azurerm_key_vault` in `keyvault.tf` with `rbac_authorization_enabled = true`, `network_acls.default_action = "Allow"`, soft-delete retention of 7 days in dev and 90 days plus purge protection in prod. Unblocks US-403, US-703, US-904.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-105
- **Acceptance criteria**:
  - Given the vault, when `rbac_authorization_enabled` is read, then it is `true` — the argument is Required in azurerm 5.x and has no legacy `enable_rbac_authorization` alias.
  - Given prod's vault, when purge protection is read, then it is enabled and cannot be disabled by a later apply.
  - Given the vault's network ACLs, when read, then `default_action` is `Allow`, with the reason (platform-IP reference resolution, no VNet) recorded in the vault's own resource comment.

#### US-403: Seven Key Vault secrets, wired as versionless references

- **Story**: Add seven `azurerm_key_vault_secret` resources (SQL connection string, Cosmos connection string, storage connection string, Document Intelligence key, Azure OpenAI key, `AzureAd:ClientSecret`, AI Foundry key) in `keyvault.tf`, five computed from resource attributes and two (`azure_ad_client_secret`, `azure_ai_foundry_api_key`) supplied as `sensitive` Terraform variables via `TF_VAR_*`. Wire the API app's corresponding app settings as versionless `@Microsoft.KeyVault(SecretUri=...)` references, and set its `identity` block to `"SystemAssigned, UserAssigned"` with `key_vault_reference_identity_id` pointing at US-401's identity. Unblocks US-404.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-401, US-402, US-202, US-301, US-302, US-303, US-304, US-305
- **Acceptance criteria**:
  - Given the seven secrets, when their app settings are read, then every reference is versionless — no `SecretUri` includes a version segment.
  - Given the two `sensitive` variables, when `terraform plan` runs, then neither value ever appears in plan output or CI logs.
  - Given a rotated secret value written directly to Key Vault outside of Terraform, when 24 hours pass with no `terraform apply`, then the app resolves the new value on its next reference refresh.
  - Given `APPLICATIONINSIGHTS_CONNECTION_STRING` and `WEBSITE_CONTENTAZUREFILECONNECTIONSTRING`, when their app settings are read, then neither is a Key Vault reference — the first breaks portal telemetry surfacing as a reference, the second fails content-share validation as one.

#### US-404: Key Vault audit diagnostics and the first full boot

- **Story**: Add `azurerm_monitor_diagnostic_setting` on the Key Vault for its `AuditEvent` category, sending it to the environment's Log Analytics workspace. This story is EP-4's exit criterion: the API starts, passes `ValidateOnStart`, and `Database.Migrate()` completes.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-403
- **Acceptance criteria**:
  - Given a fresh `apply` of everything through EP-4, when the API app is browsed, then it boots successfully with no `ValidateOnStart` failure.
  - Given the API's startup logs (via US-206's diagnostics), when inspected, then `Database.Migrate()` reports completion against the newly provisioned SQL database.
  - Given a deliberately mis-scoped role assignment (a negative test run once, then reverted), when the app fails to resolve a reference, then the vault's `AuditEvent` log names the failed access attempt, making the failure diagnosable rather than an unexplained `500.30`.

### EP-5: App-side enablers

#### US-501: `[enabler]` SPA IIS fallback `web.config`

- **Story**: `[enabler]` Add `enterprise-gpt-ui/public/web.config` with a URL Rewrite rule that serves `index.html` for any request that is not an existing file or directory, and sets `Cache-Control: no-cache` on `config.json` and `index.html`. No build change is needed — the existing asset glob already copies `public/**` verbatim. Unblocks US-603.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Acceptance criteria**:
  - Given a deep link like `/conversations/123`, when requested directly against the deployed SPA, then `index.html` is served rather than a 404.
  - Given a hashed bundle file (for example `main.abc123.js`) or `config.json`, when requested, then it is served as itself, not rewritten to `index.html`.
  - Given `config.json` and `index.html`, when their response headers are inspected, then both carry `Cache-Control: no-cache`.
  - Given MSAL's redirect to `/auth`, when it fires against the deployed SPA, then it resolves to the SPA shell rather than a 404 — login completes.

#### US-502: `[enabler]` Anonymous, dependency-free health endpoint

- **Story**: `[enabler]` Add an anonymous `GET /health` endpoint to the API that touches no SQL, Cosmos, or Blob Storage dependency, returning a simple liveness response. Unblocks US-701.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Acceptance criteria**:
  - Given the endpoint, when called with no authentication, then it returns 200 with no `.RequireAuthorization()` in its route group.
  - Given SQL is mid-resume from a serverless auto-pause, when `/health` is called during that window, then it still returns 200 — it never queries SQL.
  - Given the endpoint's response, when compared to a Problem Details response, then it carries no dependency-status body that could itself fail to serialize.

#### US-503: `[enabler]` API `web.config` disables dynamic compression

- **Story**: `[enabler]` Add a `web.config` (or equivalent publish-profile transform) for the API setting `<urlCompression doDynamicCompression="false" />`, so IIS's default MIME list — which matches `text/*` and therefore `text/event-stream` — never buffers a streaming response. Unblocks US-504, US-602.
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Acceptance criteria**:
  - Given the deployed API, when its response headers for an SSE endpoint are inspected, then no `Content-Encoding` header indicating compression is present.
  - Given the API's publish output, when packaged by `api-cd.yml`, then this `web.config` is included verbatim in the deployed artifact.
  - Given a non-streaming JSON response, when requested, then compression behavior for it is unaffected — only dynamic compression is disabled, not static.

#### US-504: SSE keep-alive survives the platform's idle timeout

- **Story**: Verify, against a deployed dev environment, that a streamed conversation delivers at least one keep-alive signal within Azure Load Balancer's ~230-second idle-timeout window, so a slow model response never has its connection silently dropped.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-503, US-604
- **Acceptance criteria**:
  - Given a conversation whose model response takes longer than 230 seconds to produce its first token, when streamed against the deployed dev environment, then the connection remains open throughout via at least one keep-alive frame.
  - Given the same scenario against an environment missing US-503's `web.config`, when reproduced once as a regression check, then the stream stalls — confirming the fix is what closes the gap, not an unrelated factor.

#### US-505: `[enabler]` `CorsOrigins` index-bound configuration

- **Story**: `[enabler]` Confirm and, if needed, fix `CorsOrigins` binding so every generated environment's app settings are written index-bound (`CorsOrigins__0`, `CorsOrigins__1`, …), and add a regression test proving that any origin present in the committed `appsettings.json` beyond index 0 does not silently survive into a generated environment's setting set (array binding replaces by index, so an entry beyond index 0 in the base file would otherwise leak through).
- **Priority**: P0 · **Estimate**: S · **Depends on**: —
- **Acceptance criteria**:
  - Given a generated environment's app settings, when inspected, then `CorsOrigins` is present as `CorsOrigins__0`, `CorsOrigins__1`, etc. — never a comma-joined string.
  - Given the committed `appsettings.json` carries a second `CorsOrigins` entry today, when the deployed setting set is generated, then that second entry does not appear unless the environment's tfvars explicitly re-declares it.
  - Given the deployed SPA's exact origin, when it is the only entry configured, then a CORS preflight from that origin succeeds and one from any other origin is rejected — `AllowCredentials()` forbids a wildcard.

### EP-6: Pipelines

#### US-601: `[enabler]` `infra-cd.yml` — plan on PR, apply on merge

- **Story**: `[enabler]` Write `.github/workflows/infra-cd.yml`: a `plan` job on every pull request touching `infra/platform/`, authenticating as the `plan` OIDC principal and posting the diff; an `apply` job on merge to `master`, authenticating as `deploy-dev`, applying the exact plan file saved from the `plan` job. Unblocks US-602, US-603, US-605, US-704.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-106
- **Acceptance criteria**:
  - Given a pull request touching `infra/platform/*.tf`, when CI runs, then a comment or check summarizes the plan diff.
  - Given that PR is merged, when the `apply` job runs, then it applies the identical saved plan file, not a freshly recomputed one.
  - Given the workflow's permissions block, when inspected, then it grants only what OIDC token exchange and the checkout require — no broader `GITHUB_TOKEN` scope.

#### US-602: `[enabler]` `api-cd.yml` deploys the API to dev

- **Story**: `[enabler]` Write `.github/workflows/api-cd.yml`: build and publish the API on merge to `master` (gated on `api-ci.yml`'s checks passing), including US-503's `web.config` in the publish output, and deploy to the dev API web app. Unblocks US-604.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-202, US-503, US-601
- **Acceptance criteria**:
  - Given a merge to `master`, when `api-ci.yml` passes, then `api-cd.yml` deploys the resulting artifact to the dev API web app.
  - Given `api-ci.yml` fails, when that happens, then `api-cd.yml` does not deploy.
  - Given the deployed artifact, when its contents are inspected, then `web.config` and the File Agent's skills/prompt/conversion-matrix content files (read by `FileAgentBootstrapper` regardless of the feature flag) are all present.

#### US-603: `[enabler]` `ui-cd.yml` builds once, generates `config.json` per environment

- **Story**: `[enabler]` Write `.github/workflows/ui-cd.yml`: build the SPA once on merge to `master`, generate a dev-specific `config.json` from GitHub Environment variables, deploy that combined artifact to the dev SPA web app, including US-501's `web.config`. Unblocks US-604.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-202, US-501, US-601
- **Acceptance criteria**:
  - Given a merge to `master`, when the workflow runs, then the SPA is built exactly once and `config.json` is generated afterward, overwriting the committed dev copy.
  - Given the generated `config.json`, when validated against `assertAppConfig`, then every field passes, including the strict-boolean check on `features.*`.
  - Given the deployed artifact, when inspected, then `web.config` is present at the SPA's root.

#### US-604: Merge to master deploys dev unattended

- **Story**: Confirm end to end that a single merge to `master` triggers `infra-cd.yml`, `api-cd.yml`, and `ui-cd.yml` such that dev's infrastructure, API, and SPA are all current, with no manual step. This is EP-6's exit criterion.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-602, US-603
- **Acceptance criteria**:
  - Given a merge introducing an infra change, an API change, and a UI change together, when the three workflows complete, then dev reflects all three with no operator intervention.
  - Given any one of the three workflows fails, when that happens, then the others' outcomes are independently visible — a failed UI deploy does not mask a successful infra apply or vice versa.

#### US-605: `deploy-prod` gated by required reviewers

- **Story**: Configure the `prod` GitHub Environment with required reviewers, and confirm `deploy-prod`'s federated credential's `environment:` subject means no prod apply or deploy proceeds without that approval.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-601
- **Acceptance criteria**:
  - Given a workflow run targeting the `prod` environment, when it reaches the `deploy-prod` step, then it pauses in a `Waiting` state until a required reviewer approves.
  - Given no reviewer has approved, when 24 hours pass, then no prod resource has been touched.
  - Given a reviewer approves, when the run resumes, then it authenticates as `deploy-prod` and proceeds.

### EP-7: Hardening

#### US-701: `[enabler]` Wire `health_check_path`

- **Story**: `[enabler]` Set `health_check_path = "/health"` on both the API and SPA web apps, now that US-502's endpoint exists.
- **Priority**: P1 · **Estimate**: S · **Depends on**: US-502, US-202
- **Acceptance criteria**:
  - Given both web apps, when their `site_config` is read, then `health_check_path` is set to `/health`.
  - Given a deliberately unhealthy instance (simulated by stopping the app), when App Service's health check evaluates it, then the instance is marked unhealthy without ever having queried SQL.

#### US-702: Tighten firewalls to explicit outbound IPs

- **Story**: Replace the bootstrap-time allowlist on SQL and storage firewalls with explicit rules covering the pipeline's and operators' actual outbound IP ranges, removing any temporary wildcard rule, while leaving the Key Vault's documented `Allow` network ACL in place.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-301, US-303
- **Acceptance criteria**:
  - Given the SQL and storage firewalls, when inspected after this story, then no rule allows an unbounded IP range.
  - Given the Key Vault's network ACL, when inspected, then it remains `Allow`, with the same documented rationale as EP-4.
  - Given a request from an IP outside the new allowlist, when it attempts to reach SQL or storage directly, then it is rejected.

#### US-703: Prod purge protection and a dev ingestion cap

- **Story**: Verify prod Key Vault purge protection is enabled and cannot be disabled by any later apply, and set a daily ingestion cap on the dev Log Analytics workspace.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-402, US-204
- **Acceptance criteria**:
  - Given prod's Key Vault, when a `terraform apply` attempts to set `purge_protection_enabled = false`, then Terraform (or Azure) rejects the change.
  - Given dev's Log Analytics workspace, when its daily cap is reached in a given day, then further ingestion for that day is dropped rather than billed without bound.

#### US-704: `[enabler]` `tflint` and a security scanner in CI

- **Story**: `[enabler]` Add `tflint` and a Terraform security scanner (for example Checkov or tfsec) to `infra-cd.yml`'s `plan` job, failing the check on any critical finding. This story is EP-7's exit criterion alongside US-702's tightened firewalls.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-601
- **Acceptance criteria**:
  - Given the current `infra/platform/` configuration, when the scanner runs, then it reports 0 critical findings.
  - Given a deliberately introduced critical finding (a wildcard firewall rule, reverted after the test), when the scanner runs, then the CI check fails.
  - Given `tflint`, when run against the configuration, then it reports 0 errors.

### EP-8: Production cutover

#### US-801: Prod tfvars and the prod-tier resources

- **Story**: Author `env/prod.tfvars` at the SKUs specified in §"Per-environment tier" (`P0v4`/`P0v3`, `GP_S_Gen5_2` auto-pause off, Cosmos autoscale 1000 RU/s, ZRS storage, 90-day retention, Key Vault purge protection), and apply the platform configuration to prod for the first time via `infra-cd.yml` under `deploy-prod`.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-404, US-704
- **Acceptance criteria**:
  - Given `env/prod.tfvars`, when `terraform plan` runs against it, then every prod-tier SKU and setting from §"Per-environment tier" is reflected in the plan.
  - Given the prod apply, when it runs under `deploy-prod`, then it is gated by US-605's required reviewers.
  - Given a second consecutive `terraform plan` against prod, when run, then it reports zero diff.

#### US-802: `[enabler]` Prod Entra redirect URIs

- **Story**: `[enabler]` Add the prod SPA's `/auth` and `/signed-out` redirect URIs to the existing, manually managed API and SPA Entra app registrations. No new app registration is created — this PRD's Entra scope is deploy principals only.
- **Priority**: P0 · **Estimate**: S · **Depends on**: US-801
- **Acceptance criteria**:
  - Given the SPA's Entra app registration, when its redirect URIs are listed, then the prod SPA's `/auth` and `/signed-out` URLs are both present alongside the dev ones.
  - Given a sign-in attempt from the prod SPA, when MSAL redirects, then Entra ID accepts the redirect URI with no `AADSTS50011` mismatch.

#### US-803: First prod apply and end-to-end smoke test

- **Story**: Complete the first prod apply and run the full smoke test: API boot (`ValidateOnStart`, `Database.Migrate()`, `CosmosBootstrapper`, `SummarizerBootstrapper`, `FileAgentBootstrapper` all pass), SPA loads `config.json` and signs in against the tenant, a deep link resolves, a document uploads and its download link works, and a conversation streams without stalling. This is EP-8's exit criterion.
- **Priority**: P0 · **Estimate**: L · **Depends on**: US-801, US-802, US-605
- **Acceptance criteria**:
  - Given the prod API, when it boots, then every named bootstrapper completes without error.
  - Given the prod SPA, when opened cold, then it loads `config.json`, signs in against the tenant, and resolves a deep link (for example `/conversations`) directly.
  - Given a document uploaded to a prod conversation, when its download link is used, then the original file downloads successfully.
  - Given a conversation streamed against prod, when the response takes over 230 seconds to complete, then it never stalls, confirming US-504's fix holds in prod too.
  - Given the same request in App Insights, when queried, then it appears as a `request` telemetry item with a correlated `traceId`.

#### US-804: `[enabler]` Record the custom-domain and DNS decision

- **Story**: `[enabler]` Document, as a decision record rather than an implementation, whether prod serves from a custom domain and whose DNS zone it would live in — deferring implementation entirely if no decision is made, consistent with §9's open question.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-801
- **Acceptance criteria**:
  - Given the decision record, when read, then it states either "no custom domain for this launch" or names the domain and the DNS zone owner, with no ambiguous middle state.
  - Given "no custom domain" is the recorded decision, when prod goes live, then both apps continue to serve from their default `*.azurewebsites.net` hostnames with no partially configured custom-domain binding left behind.

### EP-9: Tests & runbooks

#### US-901: `[enabler]` `.tftest.hcl` suite for the three named invariants

- **Story**: `[enabler]` Write `infra/platform/tests/*.tftest.hcl` asserting: the required app-setting key list is complete (a full set, not a delta, since there is no `appsettings.Development.json`); `CosmosDb__ContainerId` is absent from every generated setting set; and `ASPNETCORE_ENVIRONMENT` is never `Testing`.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-403, US-202
- **Acceptance criteria**:
  - Given the full set of expected app-setting keys, when `terraform test` runs, then every key is present in the plan for both dev and prod.
  - Given a deliberately introduced `CosmosDb__ContainerId` setting (reverted after the test), when `terraform test` runs, then it fails.
  - Given `ASPNETCORE_ENVIRONMENT` set to `Testing` in a scratch tfvars file (never committed), when `terraform test` runs against it, then it fails.

#### US-902: `[enabler]` `terraform-docs` READMEs

- **Story**: `[enabler]` Wire `terraform-docs` to generate and keep current a README for `infra/platform/` and `infra/bootstrap/`, covering every variable, output, and resource.
- **Priority**: P2 · **Estimate**: S · **Depends on**: US-105
- **Acceptance criteria**:
  - Given the platform config's variables, when `terraform-docs` runs, then the generated README lists every one with its description and type.
  - Given a variable added later with no `description`, when CI runs the doc-generation check, then it fails, since `.claude/rules/terraform.md` requires `description` and `type` on every variable.

#### US-903: Secret rotation and add-a-setting runbooks, rehearsed

- **Story**: Write and rehearse a runbook for rotating one of the seven Key Vault secrets without a Terraform apply, and a second for adding a new app setting through a Terraform change.
- **Priority**: P1 · **Estimate**: M · **Depends on**: US-403
- **Acceptance criteria**:
  - Given the rotation runbook, when followed against dev, then a secret's value changes in Key Vault directly and the app resolves the new value within 24 hours with no `terraform apply`.
  - Given the add-a-setting runbook, when followed against dev, then a new app setting appears on the target app after a normal `plan`/`apply` cycle, with no manual portal edit.

#### US-904: Key Vault recovery and state restore runbooks, rehearsed

- **Story**: Write and rehearse a runbook for recovering a soft-deleted Key Vault secret or the vault itself, and rehearse US-104's state restore script against a real `dev.tfstate` with at least two versions.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-104, US-402
- **Acceptance criteria**:
  - Given a deliberately deleted (then recovered) dev secret, when the recovery runbook is followed, then the secret and its value are restored within the vault's soft-delete window.
  - Given at least two `terraform apply` runs have written `dev.tfstate`, when `Restore-TerraformState.ps1` is run for real (not just described), then it lists both versions and restores the earlier one into a scratch copy.

#### US-905: Deploy rollback runbook, rehearsed against dev

- **Story**: Write and rehearse a runbook for rolling back a bad API or SPA deploy to the previous build artifact via `api-cd.yml`/`ui-cd.yml`, and for rolling back a bad Terraform apply via a previously saved plan file or a full state restore.
- **Priority**: P0 · **Estimate**: M · **Depends on**: US-604
- **Acceptance criteria**:
  - Given a deliberately bad API deploy to dev, when the rollback runbook is followed, then the previous artifact is redeployed and the API boots successfully again.
  - Given a deliberately bad Terraform apply to dev (an additive, reversible change), when the rollback runbook is followed, then the environment returns to its prior state via either the saved-plan or state-restore path, and the choice between the two is documented in the runbook itself.

## 8. Milestones & rollout

**Phases**, derived from the epic dependency graph — EP-1 → EP-2 → EP-3 → EP-4 → EP-6 → EP-7 → EP-8, with EP-5 running in parallel throughout and EP-9 closed out last.

| Phase | Contents | Relative estimate | Status |
| --- | --- | --- | --- |
| **Phase 1 — bootstrap, skeleton, and app-side fixes** | EP-1 in full (US-101–US-106); EP-5 in full (US-501–US-505), which needs nothing from the infra chain and runs alongside it | ~1.5 weeks | Not started |
| **Phase 2 — first working apply** | EP-2 in full (US-201–US-206) | ~1 week | Not started |
| **Phase 3 — data plane** | EP-3 in full (US-301–US-306) | ~1 week | Not started |
| **Phase 4 — secrets, identity, and the first real boot** | EP-4 in full (US-401–US-404) | ~1 week | Not started |
| **Phase 5 — pipelines automate dev** | EP-6 in full (US-601–US-605) | ~1 week | Not started |
| **Phase 6 — hardening** | EP-7 in full (US-701–US-704) | ~0.5 week | Not started |
| **Phase 7 — production cutover** | EP-8 in full (US-801–US-804) | ~0.5 week | Not started |
| **Phase 8 — tests and runbooks close-out** | EP-9 in full (US-901–US-905); individual stories are written and exercised alongside every earlier phase, but the epic is not considered closed until every runbook has been rehearsed, not merely written | ~0.5 week, interleaved | Not started |

**Risks & mitigations.**

| Risk | Mitigation |
| --- | --- |
| Azure OpenAI model quota is per region and per subscription; an apply can fail on quota exhaustion rather than on a configuration error | US-305's acceptance criteria require the target region's quota confirmed before EP-3's apply; §9 names the region/quota decision as open, gating EP-3 explicitly |
| The embedding deployment returns something other than 1536 dimensions | US-305 requires a live test embedding call confirming 1536 dimensions before the deployment is handed to the app — every vector insert otherwise fails |
| Entra auth has never been exercised against a live tenant before this PRD | US-803's smoke test exercises sign-in end to end in dev well before EP-8's prod cutover, so the first real test happens on the lower-stakes environment |
| The SQL firewall and web app creation order inverts, guaranteeing a `500.30` on first boot | US-301 forbids `for_each` over the app's outbound IP list; the firewall rule carries no dependency on the web app resource at all |
| A Key Vault reference fails to resolve because the identity/RBAC sequencing isn't actually respected | US-401's `time_sleep` plus US-404's `AuditEvent` diagnostics turn an otherwise-unexplained `500.30` into a diagnosable failed-access-attempt log entry |
| The Cosmos container's indexing policy, if Terraform-authored, diverges from `CosmosBootstrapper`'s own policy | US-302's acceptance criteria require the two to match before the story is accepted; §9 carries the underlying decision (Terraform vs. `CosmosBootstrapper`) as vetoable |
| `json`/vector-search feature availability is preview and rolls out per region | §9 gates the target region decision on this explicitly, before EP-3's first apply, rather than discovering a regional gap after resources exist |
| IIS dynamic compression silently buffers SSE if the API's `web.config` is missed from the deployed package | US-503 ships the fix; US-602 pins it into the publish output; US-504 verifies streaming from the actual deployed environment, not just a local dev server |
| Terraform state is lost or corrupted | EP-1's versioning, soft delete, point-in-time restore, and change feed on the state storage account, plus the rehearsed restore in US-904 — not merely documented |
| A bad deploy or a bad apply reaches dev or prod | US-905's rehearsed rollback runbook, covering both a code rollback (redeploy the previous artifact) and an infra rollback (saved plan or full state restore) |

**Rollout & rollback.** Infrastructure rolls out environment-scoped: dev first, through EP-6, then EP-7's hardening, then EP-8's prod cutover behind GitHub Environment reviewer approval — prod is never the first environment anything lands on. There is no feature flag at the infrastructure layer; a rollback is either a redeploy of the previous API/SPA build artifact (`api-cd.yml`/`ui-cd.yml` re-run against the prior artifact) or a Terraform rollback via the last saved plan file or a full state restore, both rehearsed in US-905 and US-904 respectively rather than left as untested procedure. No resource this PRD provisions is destroyed or replaced destructively during normal rollout — every addition is additive (new resource groups, new resources); the one genuinely irreversible structural choice, Cosmos's serverless-vs-provisioned capacity mode, is made once per environment and is why dev and prod diverge by design rather than needing a migration path between them.

## 9. Assumptions & open questions

**Assumptions.**

- The target Azure region is assumed **TBD**, gated on Azure SQL vector-search feature availability if `CREATE VECTOR INDEX`/`VECTOR_SEARCH` is ever adopted (today's exact-kNN scan works in any region, so this gates a future optimization, not day-one launch) — *operator/product owner, before EP-3's first apply. If unanswered, this PRD assumes a region is chosen and confirmed for both SQL feature availability and Azure OpenAI model/quota availability before EP-3 begins, and EP-3 is blocked until it is.*
- Azure OpenAI model quota (TPM/RPM) for the chat and embedding deployments in the target region/subscription is assumed **sufficient**, without having been confirmed against an actual quota check — *operator, before EP-3. If unanswered, this PRD assumes the operator requests and confirms quota ahead of the first apply, treating a quota-exhaustion failure during EP-3 as a blocking incident, not an acceptable retry.*
- The Cosmos transcript container is assumed **created by Terraform** (`azurerm_cosmosdb_sql_container`), reproducing `CosmosBootstrapper`'s indexing policy by hand in HCL, rather than left for the app's own `CreateContainerIfNotExistsAsync` to create on first boot. Vetoable because a policy drift between the two would silently leave the app running on a costlier default policy — *platform/backend engineer, before US-302. If unanswered, this PRD proceeds with Terraform-owned creation and US-302's acceptance criteria enforcing policy parity.*
- Browser-side Application Insights for the SPA is assumed **out of scope** for this PRD; no `@microsoft/applicationinsights-web` dependency is added, and the bundle-budget headroom (~3.5 kB against the 675 kB warning line) is assumed insufficient to absorb it casually — *product owner, as a follow-up frontend PRD. If unanswered, this PRD ships with the SPA emitting no client-side telemetry, as it does today.*
- Custom domains and DNS are assumed **not configured**; both environments serve from their default `*.azurewebsites.net` hostnames — *operator/product owner, before EP-8 if a custom domain is required for launch. If unanswered, EP-8's US-804 records "no custom domain for this launch" and prod ships on its default hostname.*
- `AzureAIFoundry:Enabled` remains `true` in the committed `appsettings.json`, and this PRD does not provision an Azure AI Foundry account of its own — the AI Foundry API key is supplied as a `sensitive` Terraform variable on the assumption that an AI Foundry resource already exists or is provisioned out of band. Vetoable because if no such resource exists, `ValidateOnStart` fails on a setting this PRD cannot itself satisfy — *operator/backend engineer, before EP-4's first full boot. If unanswered, this PRD assumes an external AI Foundry resource's key is supplied via `TF_VAR_azure_ai_foundry_api_key` before US-403 runs.*
- `P0v4` is assumed available in the target region for prod, with `P0v3` as the named fallback; the actual choice is deferred to whichever SKU is available when EP-8 provisions prod — *platform engineer, at EP-8 time.*
- Single-instance capacity (one App Service instance per environment) is assumed adequate at current load, consistent with the in-memory job queue and MSAL token cache constraint that caps the API at one instance regardless of the plan's own scale-out capability — *operator, to be revisited once that refactor ships.*
- The SQL admin password is assumed Terraform-generated (`random_password`) rather than customer-supplied — *platform engineer, vetoable if a corporate secrets policy requires a different source.*

**Open questions.**

- **What is the target Azure region?** Gates Azure SQL vector-search feature availability (if `CREATE VECTOR INDEX`/`VECTOR_SEARCH` is adopted) and Azure OpenAI model/quota availability — *operator/product owner, before EP-3's first apply.*
- **What Azure OpenAI model quota (TPM/RPM) is actually available in the chosen region/subscription for the chat and embedding deployments?** An apply can fail on quota rather than configuration — *operator, before EP-3.*
- **Is the Cosmos transcript container created by Terraform or left to `CosmosBootstrapper`?** See the vetoable assumption above — *platform/backend engineer, before US-302.*
- **Should browser-side Application Insights be added for the SPA, and if so, in which PRD?** No SDK exists today; the bundle budget has roughly 3.5 kB of headroom against the 675 kB warning line — *product owner, as a follow-up.*
- **Are custom domains and DNS required for launch, and if so, whose DNS zone do they land in?** Left unconfigured in this PRD — *operator/product owner, before EP-8.*
