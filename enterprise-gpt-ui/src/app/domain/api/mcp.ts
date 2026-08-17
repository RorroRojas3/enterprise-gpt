/**
 * An MCP tool server the caller may use, as `GET api/mcps` returns it.
 *
 * The route already returns only the servers whose `PermissionId` the caller holds,
 * so the picker needs no client-side filtering — and must not add any, because the
 * permission ids behind these servers are created at run time and are not knowable
 * from a constant.
 *
 * The administrative representation is {@link McpServerDto} below (US-1208).
 */
export interface McpDto {
  readonly id: string;
  readonly name: string;
  readonly description: string | null;
}

/**
 * How the API authenticates against an MCP server, mirroring `McpAuthTypes`.
 *
 * **These travel as numbers, not strings.** The API registers no
 * `JsonStringEnumConverter`, so the enum serializes as its underlying `int` — and
 * the validator's `IsInEnum` rejects `0`, so a form must always send one of the two.
 */
export const MCP_AUTH_TYPE = {
  /** The server requires no authentication, and must carry no scope. */
  none: 1,
  /** A Microsoft Entra ID token acquired on behalf of the signed-in user. */
  entraIdOnBehalfOf: 2,
} as const;

/** One of {@link MCP_AUTH_TYPE}'s values. */
export type McpAuthType = (typeof MCP_AUTH_TYPE)[keyof typeof MCP_AUTH_TYPE];

/**
 * An MCP server as `GET api/mcps/all` returns one — the administrative shape (US-1208).
 *
 * That route is Administrator-gated, unpaginated, ordered by name, and **returns
 * deactivated servers too**, which is why `dateDeactivated` is meaningful here and
 * absent from {@link McpDto}.
 *
 * `permissionId` is the server's own gating permission, which `McpServerService`
 * creates alongside the server and **names after it**, re-syncing that name on every
 * rename. The two share one uniqueness space, which is what lets a badge name the
 * permission from `name` alone rather than from a second request.
 */
export interface McpServerDto {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly url: string;
  readonly authType: number;
  /** Required for {@link MCP_AUTH_TYPE.entraIdOnBehalfOf}, and rejected for `none`. */
  readonly scope: string | null;
  /** Null once the server is retired — deactivation cascades to the permission. */
  readonly permissionId: string | null;
  /** Only populated by the administrative `GET api/mcps/all`. */
  readonly dateDeactivated?: string | null;
}

/**
 * The body `POST api/mcps` and `PUT api/mcps/{id}` both take.
 *
 * `CreateMcpServerActionDto` and `UpdateMcpServerActionDto` are the same five fields
 * server-side, so one shape serves both. Neither carries a permission field: the
 * server creates and renames the linked permission itself, which is why frame `5h`'s
 * "Linked permission" select does not exist here.
 *
 * **The PUT is a full representation, not a patch.**
 * `FromUpdateMcpServerActionDtoToMcpServer` assigns every field unconditionally, so
 * anything the form does not edit still has to be echoed back — the same rule
 * `ModelWriteBody` and `UpdateUserActionDto` impose.
 */
export interface McpServerWriteBody {
  readonly name: string;
  readonly description: string;
  readonly url: string;
  readonly authType: McpAuthType;
  readonly scope: string | null;
}

/**
 * Whether `GET api/mcps/all` returned this server as retired (US-1208).
 *
 * Named apart from `model.ts`'s `isRetired` because specs and fixtures import both.
 */
export function isMcpServerRetired(server: McpServerDto): boolean {
  return (server.dateDeactivated ?? null) !== null;
}
