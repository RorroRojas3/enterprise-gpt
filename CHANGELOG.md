# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Per-user permission cache. Authorization now reads each caller's permission grants from an in-process cache instead of the database, so a warm request runs no permission query and builds no EF context to authorize. Entries are filled when the user signs in (`POST /api/users/me`) and evicted for exactly the affected users whenever grants change — granting, revoking, editing or deactivating a user, deactivating a permission, or deactivating an MCP server. Two paths deliberately keep querying the database: MCP tool acquisition, so a revoked grant stops a third-party tool call on the very next request, and the permitted-MCP-server listing, where the cache would save no round trip. See [docs/permissions/permission-cache.md](docs/permissions/permission-cache.md).
- `Permissions:Cache:EntryLifetime` setting (default 5 minutes, validated at startup) bounding how long a cached grant set is served before it is reloaded.
- Amazon Bedrock as a second LLM provider, so Anthropic Claude models can sit in the catalog beside Azure OpenAI deployments and be chosen per conversation. A model's provider now selects the chat client that serves its turns. Bedrock stays off unless `AmazonBedrock:Enabled` is `true`; when it is on, `Region`, `ApiKey`, and `DefaultModelId` are validated at startup and the application refuses to boot if one is missing or the region is unknown. Authentication uses a long-term Bedrock API key sent as an HTTP bearer token — short-term keys are not supported, because the token is never refreshed. MCP tool calling, SSE streaming, and token accounting work on Bedrock unchanged. See [docs/models/amazon-bedrock.md](docs/models/amazon-bedrock.md).
- 503 response of type `/problems/provider-not-configured`, carrying a `providerId` extension, when a conversation selects a model whose provider has no chat client in this deployment — most often a Bedrock-backed model while `AmazonBedrock:Enabled` is `false`. It is deliberately a 503 rather than a 500 or a 400: nothing the caller changes about the request will help.

### Changed

- Endpoint permission gating takes one or more permission ids and requires **all** of them: `PermissionEndpointFilter.Require(params Guid[])`. The separate display-name argument is gone — names for the 403 body now come from a fixed map that is checked when routes are mapped, so a permission id with no name fails application startup instead of producing a nameless 403 in production. Response shape is unchanged.
- Administrator-only routes are gated by the same generic filter as every other permission (`Require(PermissionIds.Administrator)`); administrators are still not implicitly granted every permission.
- **Breaking:** a model now carries `name` — the label users see, up to 256 characters — and `deploymentName` — the identifier sent to the provider, up to 512 characters — in place of `name` and `displayName`. `deploymentName` holds an Azure OpenAI deployment name, or a Bedrock model id, inference profile id, or ARN. `ModelDto` and both model request bodies change shape, and the two values swap roles, so renaming the field in a client is not enough. Conversation transcripts in Cosmos DB now record the deployment name rather than the label, so an append-only transcript stays meaningful after a model is renamed in the catalog. See [docs/models/model-management.md](docs/models/model-management.md#22-name-and-deploymentname).
- **Operators:** this release ships no database migration and no SQL script, and both gaps fail silently. Existing `Core.Ref.Model` rows hold the label and the deployment identifier the wrong way round and must be swapped, with the column widened to `nvarchar(512)`; and the Amazon Bedrock row must be inserted into `Core.Ref.Provider` by hand, or every attempt to create a Bedrock-backed model returns 404 "Provider not found" regardless of configuration. Steps in [docs/models/amazon-bedrock.md](docs/models/amazon-bedrock.md#10-operational-notes--the-schema-is-not-migrated).

### Removed

- The dedicated admin endpoint filter. All 22 admin routes moved to the generic permission filter with no change to their behaviour or responses.
- `displayName` from the model API — `ModelDto`, `CreateModelActionDto`, and `UpdateModelActionDto`. `name` is now the displayed label, which is also what the model picker has always rendered.

### Security

- Permission changes converge immediately only on the API instance that served them. In a multi-instance deployment, a revoked grant or a deactivated administrator can retain access on other instances for up to `Permissions:Cache:EntryLifetime` (default 5 minutes). Shorten the lifetime before scaling out, and disable the account in Entra ID for immediate offboarding. Single-instance deployments are unaffected.

[Unreleased]: https://github.com/RorroRojas3/enterprise-gpt/commits/master
