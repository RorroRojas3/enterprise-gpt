/**
 * A permission grant, as `Enterprise.Gpt.Dto.PermissionDto` puts it on the wire.
 *
 * `mcpServerId` is non-null on the permissions the platform creates per MCP server.
 * Those ids are data, not constants, which is why nothing in this application may
 * match a permission by `name` — see `permission-ids.ts`.
 */
export interface PermissionDto {
  readonly id: string;
  readonly name: string;
  readonly description: string | null;
  readonly isDefault: boolean;
  readonly mcpServerId: string | null;
  /**
   * Only ever populated by `GET api/permissions/all`. The permissions nested in a
   * {@link UserDto} are the caller's *active* grants, so this is always absent there.
   */
  readonly dateDeactivated?: string | null;
}

/**
 * The signed-in user, as `POST api/users/me` returns it on both 200 and 201.
 *
 * `permissions` arrives inline, so resolving a session costs one request rather than
 * two, and an empty array is a valid answer — a user with no grants at all.
 *
 * `id` is the Entra object id (`oid`): the API keys its user rows on it directly.
 * `fullName` is computed and serialized server-side; it is read-only and must never
 * be sent back.
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
