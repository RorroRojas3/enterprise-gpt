import { describe, expect, it, vi } from 'vitest';
import { PROBLEM_FIXTURES, TRACE_ID } from '@testing/problem-fixtures';
import { ResponseLike, toAppErrorFromResponse } from './to-app-error-from-response';

const URL = 'https://localhost:7045/api/conversations/abc/stream';

function response(overrides: Partial<ResponseLike> & Pick<ResponseLike, 'status'>): ResponseLike {
  return {
    url: URL,
    bodyUsed: false,
    headers: { get: () => 'application/problem+json' },
    text: async () => '',
    ...overrides,
  };
}

describe('toAppErrorFromResponse', () => {
  it('parses a problem body into the matching arm', async () => {
    const error = await toAppErrorFromResponse(
      response({
        status: 403,
        text: async () => JSON.stringify(PROBLEM_FIXTURES.mcpAuthorizationRequired),
      }),
    );

    expect(error.kind).toBe('mcp-authorization-required');
    expect(error.kind === 'mcp-authorization-required' && error.serverName).toBe('Weather');
    expect(error.traceId).toBe(TRACE_ID);
    expect(error.url).toBe(URL);
  });

  it('parses conversation-busy, the pre-stream 409', async () => {
    const error = await toAppErrorFromResponse(
      response({
        status: 409,
        text: async () => JSON.stringify(PROBLEM_FIXTURES.conversationBusy),
      }),
    );

    expect(error.kind).toBe('conversation-busy');
  });

  it('falls back to http for a non-JSON body', async () => {
    const error = await toAppErrorFromResponse(
      response({
        status: 502,
        headers: { get: () => 'text/html' },
        text: async () => '<html>502 Bad Gateway</html>',
      }),
    );

    expect(error.kind).toBe('http');
    expect(error.status).toBe(502);
  });

  it('handles the empty body of a 499', async () => {
    const error = await toAppErrorFromResponse(
      response({ status: 499, headers: { get: () => null }, text: async () => '' }),
    );

    expect(error.kind).toBe('http');
    expect(error.status).toBe(499);
    expect(error.traceId).toBeNull();
  });

  it('does not read a body that has already been consumed', async () => {
    const text = vi.fn(async () => '');

    const error = await toAppErrorFromResponse(response({ status: 500, bodyUsed: true, text }));

    expect(text).not.toHaveBeenCalled();
    expect(error.kind).toBe('http');
  });

  it('reads the body exactly once, because it is not replayable', async () => {
    const text = vi.fn(async () => JSON.stringify(PROBLEM_FIXTURES.forbidden));

    await toAppErrorFromResponse(response({ status: 403, text }));

    expect(text).toHaveBeenCalledOnce();
  });

  it('resolves rather than rejects when the body read fails mid-stream', async () => {
    const error = await toAppErrorFromResponse(
      response({
        status: 500,
        text: async () => {
          throw new TypeError('network error');
        },
      }),
    );

    expect(error.kind).toBe('http');
    expect(error.status).toBe(500);
  });

  it('resolves when the body is malformed JSON', async () => {
    const error = await toAppErrorFromResponse(
      response({ status: 400, text: async () => '{"type":' }),
    );

    expect(error.kind).toBe('http');
  });

  it('prefers an explicitly supplied request URL', async () => {
    const error = await toAppErrorFromResponse(
      response({ status: 404, url: '', text: async () => '' }),
      'https://localhost:7045/api/conversations?take=20',
    );

    expect(error.url).toBe('https://localhost:7045/api/conversations?take=20');
  });
});
