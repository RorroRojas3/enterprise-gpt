/**
 * An MCP server the caller may use, from `GET api/mcps`. Already filtered server-side; never
 * filter client-side, since the gating permission ids are created at run time.
 */
export interface McpDto {
  readonly id: string;
  readonly name: string;
  readonly description: string | null;
  /**
   * Brand-mark slug such as `microsoft`, or null for the generic glyph. A key missing from
   * this build's sprite degrades; see `mcpBrandIcon`.
   */
  readonly iconKey: string | null;
  /**
   * Whether the caller must supply their own API key. The auth type itself is a connection
   * detail and stays off this shape.
   */
  readonly requiresUserApiKey: boolean;
  /**
   * Whether the caller has a usable key stored. False once the server has rejected
   * the stored one, which is what puts the row back into its "needs a key" state.
   */
  readonly hasUserApiKey: boolean;
  /** Last four characters of the stored key, or null when none is held. */
  readonly apiKeyHint: string | null;
}

/** Never carries the key or its stored form, only enough to render which one is held. */
export interface McpCredentialStatusDto {
  readonly mcpServerId: string;
  readonly hasApiKey: boolean;
  readonly apiKeyHint: string | null;
  readonly dateModified: string | null;
}

/** The body `PUT api/mcps/{id}/credential` takes. */
export interface McpCredentialWriteBody {
  readonly apiKey: string;
}

/**
 * Mirrors `McpAuthTypes`. Numbers, not strings: the API registers no `JsonStringEnumConverter`,
 * and `IsInEnum` rejects `0`.
 */
export const MCP_AUTH_TYPE = {
  /** The server requires no authentication, and must carry no scope. */
  none: 1,
  /** A Microsoft Entra ID token acquired on behalf of the signed-in user. */
  entraIdOnBehalfOf: 2,
  /**
   * A bearer credential each user supplies, such as a GitHub PAT. Unlike a shared header
   * secret, it is the user's own, stored encrypted, and returned by no route.
   */
  userApiKey: 3,
} as const;

export type McpAuthType = (typeof MCP_AUTH_TYPE)[keyof typeof MCP_AUTH_TYPE];

/**
 * Extra headers sent on every call, by name. Configuration, never credentials: the API refuses
 * `Authorization` and the other headers its transport owns.
 */
export type McpServerHeaders = Readonly<Record<string, string>>;

/**
 * The administrative shape from `GET api/mcps/all`, which includes deactivated servers. The
 * linked permission is named after the server and renamed with it, so a badge can name it from
 * `name` alone.
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
  readonly headers: McpServerHeaders | null;
  /** Null once the server is retired — deactivation cascades to the permission. */
  readonly permissionId: string | null;
  /** Only populated by the administrative `GET api/mcps/all`. */
  readonly dateDeactivated?: string | null;
}

/**
 * The body of both `POST api/mcps` and `PUT api/mcps/{id}`; the server manages the linked
 * permission itself. The PUT is a full representation, so unedited fields must be echoed back.
 */
export interface McpServerWriteBody {
  readonly name: string;
  readonly description: string;
  readonly url: string;
  readonly authType: McpAuthType;
  readonly scope: string | null;
  readonly iconKey: string | null;
  /**
   * Omitting this on an edit clears the stored headers, which for a server registered
   * read-only would silently restore its write tools.
   */
  readonly headers: McpServerHeaders | null;
}
