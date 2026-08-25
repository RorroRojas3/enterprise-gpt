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
  /**
   * Slug naming the brand mark to draw, such as `microsoft`; null for the generic
   * glyph. Presentation rather than a connection detail, which is why it crosses
   * to this shape while `url`, `authType` and `scope` do not. The artwork ships in
   * the sprite, so a key this build does not know degrades — see `mcpBrandIcon`.
   */
  readonly iconKey: string | null;
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
 * Extra request headers the API sends on every call to a server, keyed by header name.
 *
 * Configuration, never credentials: the API refuses `Authorization` and the other headers
 * its transport owns, and re-applies that rule when it builds the request. Administrative
 * only — the shape is absent from {@link McpDto} for the same reason `url` and `scope` are.
 */
export type McpServerHeaders = Readonly<Record<string, string>>;

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
  /** The brand mark's slug, or null for the generic glyph. */
  readonly iconKey: string | null;
  /** Extra request headers sent to this server, or null for none. */
  readonly headers: McpServerHeaders | null;
  /** Null once the server is retired — deactivation cascades to the permission. */
  readonly permissionId: string | null;
  /** Only populated by the administrative `GET api/mcps/all`. */
  readonly dateDeactivated?: string | null;
}

/**
 * The body `POST api/mcps` and `PUT api/mcps/{id}` both take.
 *
 * `CreateMcpServerActionDto` and `UpdateMcpServerActionDto` are the same seven fields
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
  readonly iconKey: string | null;
  /**
   * Extra request headers, or null for none.
   *
   * Subject to the full-representation rule above with real consequences: omitting this on
   * an edit **clears** the stored headers, which for a server registered read-only would
   * silently restore its write tools.
   */
  readonly headers: McpServerHeaders | null;
}

/**
 * Whether `GET api/mcps/all` returned this server as retired (US-1208).
 *
 * Named apart from `model.ts`'s `isRetired` because specs and fixtures import both.
 */
export function isMcpServerRetired(server: McpServerDto): boolean {
  return (server.dateDeactivated ?? null) !== null;
}
