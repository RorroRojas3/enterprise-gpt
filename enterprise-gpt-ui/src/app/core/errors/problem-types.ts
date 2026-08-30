/**
 * Prefix shared by every application-specific problem type URI the API emits.
 *
 * These are relative URI references. RFC 9457 §3.1.1 permits that, and the API
 * treats them as opaque identifiers rather than links, so the client must match
 * them verbatim and never resolve them. Changing one is a breaking API change.
 */
export const PROBLEM_BASE = '/problems/';

/**
 * The twelve application-specific problem types, mirroring
 * `Enterprise.Gpt.Api/Problems/ProblemTypes.cs`.
 *
 * A response carrying any other `type` — typically an RFC 9110 status-section
 * link — is a framework-level problem, not a domain one.
 */
export const PROBLEM_TYPE = {
  validationError: '/problems/validation-error',
  uploadTooLarge: '/problems/upload-too-large',
  resourceNotFound: '/problems/resource-not-found',
  forbidden: '/problems/forbidden',
  permissionRequired: '/problems/permission-required',
  conversationBusy: '/problems/conversation-busy',
  mcpServerUnavailable: '/problems/mcp-server-unavailable',
  /** A 428: the server takes an API key the user supplies and none is stored. */
  mcpCredentialRequired: '/problems/mcp-credential-required',
  /** A 428: the server refused the key the user stored. */
  mcpCredentialRejected: '/problems/mcp-credential-rejected',
  providerNotConfigured: '/problems/provider-not-configured',
  storageNotConfigured: '/problems/storage-not-configured',
  exportRendererNotConfigured: '/problems/export-renderer-not-configured',
} as const;

/** One of the twelve application-specific problem type URIs. */
export type ProblemTypeUri = (typeof PROBLEM_TYPE)[keyof typeof PROBLEM_TYPE];

const PROBLEM_TYPE_URIS: readonly string[] = Object.values(PROBLEM_TYPE);

/**
 * Whether a `type` is one of the twelve application-specific problem types.
 *
 * @param type The raw `type` member of a problem body.
 */
export function isKnownProblemType(type: string | undefined | null): type is ProblemTypeUri {
  return typeof type === 'string' && PROBLEM_TYPE_URIS.includes(type);
}
