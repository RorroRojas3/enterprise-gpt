/**
 * Mirrors `Enterprise.Gpt.Api/Problems/ProblemTypes.cs`. Opaque relative URIs: match verbatim,
 * never resolve. Any other `type` is a framework-level problem.
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

export type ProblemTypeUri = (typeof PROBLEM_TYPE)[keyof typeof PROBLEM_TYPE];
