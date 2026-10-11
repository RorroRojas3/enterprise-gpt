/** Mirrors `McpCredentialRules`, so a rejection from the API is the exception. */
export const MCP_CREDENTIAL_LIMITS = {
  min: 8,
  max: 512,
} as const;

/** Mirrors the server's rule: printable ASCII with no whitespace. */
const API_KEY_PATTERN = /^[\x21-\x7E]+$/;

/**
 * All of `McpCredentialRules` except emptiness, which `required` owns. Whitespace gets its own
 * message: a pasted newline is invisible in a masked field.
 */
export function apiKeyError(raw: string): string | null {
  if (raw === '') {
    return null;
  }

  if (raw.length < MCP_CREDENTIAL_LIMITS.min) {
    return 'That is too short to be an access token.';
  }

  if (raw.length > MCP_CREDENTIAL_LIMITS.max) {
    return `Access tokens are limited to ${MCP_CREDENTIAL_LIMITS.max} characters.`;
  }

  if (raw !== raw.trim() || /\s/.test(raw)) {
    return 'Remove the spaces or line breaks — paste the token on its own.';
  }

  return API_KEY_PATTERN.test(raw) ? null : 'That does not look like an access token.';
}
