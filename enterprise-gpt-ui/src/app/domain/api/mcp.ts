/**
 * An MCP tool server the caller may use, as `GET api/mcps` returns it.
 *
 * The route already returns only the servers whose `PermissionId` the caller holds,
 * so the picker needs no client-side filtering — and must not add any, because the
 * permission ids behind these servers are created at run time and are not knowable
 * from a constant.
 *
 * The administrative representation (`GET api/mcps/all`, `McpServerDto`) carries the
 * url, auth type, scope and permission id; it belongs to EP-12, not here.
 */
export interface McpDto {
  readonly id: string;
  readonly name: string;
  readonly description: string | null;
}
