import { describe, expect, it } from 'vitest';
import { PROBLEM_TYPE } from './problem-types';

describe('PROBLEM_TYPE', () => {
  // These strings are opaque identifiers the API's clients match verbatim; a typo
  // here is indistinguishable from the server never emitting the problem at all.
  it('mirrors ProblemTypes.cs verbatim', () => {
    expect(PROBLEM_TYPE).toEqual({
      validationError: '/problems/validation-error',
      uploadTooLarge: '/problems/upload-too-large',
      resourceNotFound: '/problems/resource-not-found',
      forbidden: '/problems/forbidden',
      permissionRequired: '/problems/permission-required',
      conversationBusy: '/problems/conversation-busy',
      mcpServerUnavailable: '/problems/mcp-server-unavailable',
      mcpCredentialRequired: '/problems/mcp-credential-required',
      mcpCredentialRejected: '/problems/mcp-credential-rejected',
      providerNotConfigured: '/problems/provider-not-configured',
      storageNotConfigured: '/problems/storage-not-configured',
      exportRendererNotConfigured: '/problems/export-renderer-not-configured',
    });
  });

  it('declares all twelve types, with no duplicates', () => {
    const values = Object.values(PROBLEM_TYPE);

    expect(values).toHaveLength(12);
    expect(new Set(values).size).toBe(12);
  });

  it('places every type under the shared relative base', () => {
    for (const type of Object.values(PROBLEM_TYPE)) {
      expect(type.startsWith('/problems/')).toBe(true);
    }
  });
});
