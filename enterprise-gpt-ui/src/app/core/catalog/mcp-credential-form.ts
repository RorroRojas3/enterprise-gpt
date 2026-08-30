/** Mirrors `McpCredentialRules`, so a rejection from the API is the exception. */
export const MCP_CREDENTIAL_LIMITS = {
  min: 8,
  max: 512,
} as const;

/** Mirrors the server's rule: printable ASCII with no whitespace. */
const API_KEY_PATTERN = /^[\x21-\x7E]+$/;

/**
 * Validates a user-supplied API key, returning the reason it is unusable or null.
 *
 * The whole of `McpCredentialRules`, so a second caller reaching for this gets every rule
 * rather than the subset the dialog's schema does not already cover. An empty field is the
 * one thing left to the `required` rule, which owns the copy for it.
 *
 * Whitespace is called out on its own because it is the failure people actually hit: a
 * paste that caught a trailing newline or a surrounding quote looks correct in a masked
 * field, and "must be printable ASCII" would not tell them what to fix.
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
