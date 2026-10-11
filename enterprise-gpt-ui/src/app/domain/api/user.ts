/** Per-MCP-server permissions are data, not constants: never match a permission by `name`. */
export interface PermissionDto {
  readonly id: string;
  readonly name: string;
  readonly description: string | null;
  readonly isDefault: boolean;
  readonly mcpServerId: string | null;
  /** Only populated by `GET api/permissions/all`; never on a {@link UserDto}'s active grants. */
  readonly dateDeactivated?: string | null;
}

/**
 * From `POST api/users/me` (200 or 201), with grants inline. `id` is the Entra `oid`;
 * `fullName` is computed server-side and must never be sent back.
 */
export interface UserDto {
  readonly id: string;
  /** May be an empty string when Microsoft Graph has no `givenName` for the account. */
  readonly firstName: string;
  /** May be an empty string when Microsoft Graph has no `surname` for the account. */
  readonly lastName: string;
  readonly email: string;
  readonly permissions: readonly PermissionDto[];
  readonly fullName: string;
}
