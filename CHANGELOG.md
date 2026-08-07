# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Per-user permission cache. Authorization now reads each caller's permission grants from an in-process cache instead of the database, so a warm request runs no permission query and builds no EF context to authorize. Entries are filled when the user signs in (`POST /api/users/me`) and evicted for exactly the affected users whenever grants change — granting, revoking, editing or deactivating a user, deactivating a permission, or deactivating an MCP server. Two paths deliberately keep querying the database: MCP tool acquisition, so a revoked grant stops a third-party tool call on the very next request, and the permitted-MCP-server listing, where the cache would save no round trip. See [docs/permissions/permission-cache.md](docs/permissions/permission-cache.md).
- `Permissions:Cache:EntryLifetime` setting (default 5 minutes, validated at startup) bounding how long a cached grant set is served before it is reloaded.

### Changed

- Endpoint permission gating takes one or more permission ids and requires **all** of them: `PermissionEndpointFilter.Require(params Guid[])`. The separate display-name argument is gone — names for the 403 body now come from a fixed map that is checked when routes are mapped, so a permission id with no name fails application startup instead of producing a nameless 403 in production. Response shape is unchanged.
- Administrator-only routes are gated by the same generic filter as every other permission (`Require(PermissionIds.Administrator)`); administrators are still not implicitly granted every permission.

### Removed

- The dedicated admin endpoint filter. All 22 admin routes moved to the generic permission filter with no change to their behaviour or responses.

### Security

- Permission changes converge immediately only on the API instance that served them. In a multi-instance deployment, a revoked grant or a deactivated administrator can retain access on other instances for up to `Permissions:Cache:EntryLifetime` (default 5 minutes). Shorten the lifetime before scaling out, and disable the account in Entra ID for immediate offboarding. Single-instance deployments are unaffected.

[Unreleased]: https://github.com/RorroRojas3/enterprise-gpt/commits/master
