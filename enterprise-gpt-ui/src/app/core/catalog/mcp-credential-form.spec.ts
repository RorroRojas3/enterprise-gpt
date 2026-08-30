import { describe, expect, it } from 'vitest';
import { MCP_CREDENTIAL_LIMITS, apiKeyError } from './mcp-credential-form';

describe('apiKeyError', () => {
  it.each([
    ['a classic token', 'ghp_16C7e42F292c6912E7710c838347Ae178B4a'],
    ['a fine-grained token', 'github_pat_11ABCDEFG0abcdefghijkl_ABCDEFGHIJ0123456789'],
  ])('accepts %s as issued', (_label, apiKey) => {
    expect(apiKeyError(apiKey)).toBeNull();
  });

  it('says nothing about an empty field, which the required rule owns', () => {
    expect(apiKeyError('')).toBeNull();
  });

  it.each([
    ['a trailing newline', 'ghp_token\n'],
    ['a leading space', ' ghp_token'],
    ['an interior space', 'ghp_to ken'],
  ])('names whitespace as the problem for %s, since that is what a paste catches', (_l, apiKey) => {
    expect(apiKeyError(apiKey)).toBe(
      'Remove the spaces or line breaks — paste the token on its own.',
    );
  });

  it('refuses a value outside printable ASCII', () => {
    expect(apiKeyError('ghp_tokén')).toBe('That does not look like an access token.');
  });

  it('refuses a value the column could not hold', () => {
    expect(apiKeyError('a'.repeat(MCP_CREDENTIAL_LIMITS.max))).toBeNull();
    expect(apiKeyError('a'.repeat(MCP_CREDENTIAL_LIMITS.max + 1))).toBe(
      `Access tokens are limited to ${MCP_CREDENTIAL_LIMITS.max} characters.`,
    );
  });
});
