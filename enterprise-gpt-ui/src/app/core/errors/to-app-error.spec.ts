import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, expectTypeOf, it } from 'vitest';
import { FRAMEWORK_PROBLEM_FIXTURES, PROBLEM_FIXTURES, TRACE_ID } from '@testing/problem-fixtures';
import { AppError, isProblemAppError, isRouteNotFound } from './app-error';
import { toAppError } from './to-app-error';

const URL = 'https://localhost:7045/api/conversations';

function httpError(body: unknown, status: number, statusText = ''): HttpErrorResponse {
  return new HttpErrorResponse({ error: body, status, statusText, url: URL });
}

describe('toAppError — application problem types', () => {
  it('maps a validation problem and exposes its errors dictionary', () => {
    const error = toAppError(httpError(PROBLEM_FIXTURES.validationError, 400));

    expect(error.kind).toBe('validation-error');
    if (error.kind !== 'validation-error') {
      throw new Error('unreachable');
    }
    expectTypeOf(error.errors).toEqualTypeOf<Readonly<Record<string, readonly string[]>>>();
    expect(error.errors['ContextWindowSize']).toEqual([
      "'Context Window Size' must be greater than 0.",
    ]);
    // detail is null on validation problems by design; the messages are in errors.
    expect(error.detail).toBeNull();
    expect(error.traceId).toBe(TRACE_ID);
    expect(error.instance).toBe('/api/models');
    expect(error.url).toBe(URL);
  });

  it('maps upload-too-large from a 400, not a 413, and exposes maxBytes', () => {
    const error = toAppError(httpError(PROBLEM_FIXTURES.uploadTooLarge, 400));

    expect(error.kind).toBe('upload-too-large');
    if (error.kind !== 'upload-too-large') {
      throw new Error('unreachable');
    }
    expectTypeOf(error.maxBytes).toEqualTypeOf<number>();
    expect(error.maxBytes).toBe(52_428_800);
    expect(error.status).toBe(400);
  });

  it('maps resource-not-found', () => {
    expect(toAppError(httpError(PROBLEM_FIXTURES.resourceNotFound, 404)).kind).toBe(
      'resource-not-found',
    );
  });

  it('maps forbidden', () => {
    expect(toAppError(httpError(PROBLEM_FIXTURES.forbidden, 403)).kind).toBe('forbidden');
  });

  it('maps permission-required and exposes the display names', () => {
    const error = toAppError(httpError(PROBLEM_FIXTURES.permissionRequired, 403));

    expect(error.kind).toBe('permission-required');
    if (error.kind !== 'permission-required') {
      throw new Error('unreachable');
    }
    expectTypeOf(error.permissions).toEqualTypeOf<readonly string[]>();
    expect(error.permissions).toEqual(['Upload File']);
  });

  it('maps conversation-busy', () => {
    expect(toAppError(httpError(PROBLEM_FIXTURES.conversationBusy, 409)).kind).toBe(
      'conversation-busy',
    );
  });

  it('maps mcp-server-unavailable and exposes serverName', () => {
    const error = toAppError(httpError(PROBLEM_FIXTURES.mcpServerUnavailable, 502));

    expect(error.kind).toBe('mcp-server-unavailable');
    expect(error.kind === 'mcp-server-unavailable' && error.serverName).toBe('Weather');
  });

  it.each([
    ['mcp-credential-required', PROBLEM_FIXTURES.mcpCredentialRequired],
    ['mcp-credential-rejected', PROBLEM_FIXTURES.mcpCredentialRejected],
  ] as const)('maps %s from a 428 and exposes the server it names', (kind, fixture) => {
    const error = toAppError(httpError(fixture, 428));

    expect(error.kind).toBe(kind);
    if (error.kind !== 'mcp-credential-required' && error.kind !== 'mcp-credential-rejected') {
      throw new Error('unreachable');
    }

    // The id as well as the name: the client opens the key dialog for that server.
    expect(error.mcpServerId).toBe('3f1c9b2a-7d4e-4c5f-8a9b-0c1d2e3f4a5b');
    expect(error.serverName).toBe('GitHub');
  });

  it('maps provider-not-configured from a 503 and exposes providerId', () => {
    const error = toAppError(httpError(PROBLEM_FIXTURES.providerNotConfigured, 503));

    expect(error.kind).toBe('provider-not-configured');
    if (error.kind !== 'provider-not-configured') {
      throw new Error('unreachable');
    }
    expectTypeOf(error.providerId).toEqualTypeOf<string>();
    expect(error.providerId).toBe('c1a2b3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d');
    expect(error.status).toBe(503);
  });

  it('maps export-renderer-not-configured from a 503, keeping the format', () => {
    const error = toAppError(httpError(PROBLEM_FIXTURES.exportRendererNotConfigured, 503));

    expect(error.kind).toBe('export-renderer-not-configured');
    expect(error.kind === 'export-renderer-not-configured' && error.format).toBe('pdf');
  });

  it('maps storage-not-configured from a 503', () => {
    expect(toAppError(httpError(PROBLEM_FIXTURES.storageNotConfigured, 503)).kind).toBe(
      'storage-not-configured',
    );
  });
});

describe('toAppError — framework problems', () => {
  it('maps a 401 challenge to http', () => {
    const error = toAppError(httpError(FRAMEWORK_PROBLEM_FIXTURES.unauthorized, 401));

    expect(error.kind).toBe('http');
    expect(error.status).toBe(401);
    expect(error.traceId).toBe(TRACE_ID);
  });

  it('keeps a routing 404 distinct from resource-not-found', () => {
    // The routing 404 carries the RFC 9110 link, so it means "no such endpoint"
    // rather than "you may not see that row".
    const error = toAppError(httpError(FRAMEWORK_PROBLEM_FIXTURES.routeNotFound, 404));

    expect(error.kind).toBe('http');
    expect(error.kind).not.toBe('resource-not-found');
  });

  it('maps a bare 413 to http with no maxBytes', () => {
    // Produced by Kestrel when a chunked upload bypasses the size filter, so the
    // upload UI has to cope without the extension.
    const error = toAppError(httpError(FRAMEWORK_PROBLEM_FIXTURES.payloadTooLarge, 413));

    expect(error.kind).toBe('http');
    expect(error).not.toHaveProperty('maxBytes');
  });

  it('maps a 500 to http', () => {
    expect(toAppError(httpError(FRAMEWORK_PROBLEM_FIXTURES.serverError, 500)).kind).toBe('http');
  });
});

describe('toAppError — bodiless and malformed responses', () => {
  it('maps a 499 with no body to http and no trace id', () => {
    const error = toAppError(httpError(null, 499));

    expect(error.kind).toBe('http');
    expect(error.status).toBe(499);
    expect(error.traceId).toBeNull();
  });

  it('survives the parse-failure wrapper HttpClient supplies for a non-JSON body', () => {
    const error = toAppError(
      httpError({ error: new SyntaxError('Unexpected token <'), text: '<html>502</html>' }, 502),
    );

    expect(error.kind).toBe('http');
    expect(error.status).toBe(502);
  });

  it('maps status 0 to network', () => {
    expect(toAppError(httpError(new ProgressEvent('error'), 0)).kind).toBe('network');
  });

  it('maps an aborted status 0 to aborted', () => {
    const abort = new DOMException('The operation was aborted.', 'AbortError');

    expect(toAppError(httpError(abort, 0)).kind).toBe('aborted');
  });
});

describe('toAppError — non-HttpErrorResponse values', () => {
  it('maps an AbortError to aborted', () => {
    expect(toAppError(new DOMException('aborted', 'AbortError')).kind).toBe('aborted');
  });

  it('maps a TimeoutError to network, not client, so it stays retriable', () => {
    // AbortSignal.timeout() raises a TimeoutError, which is neither an AbortError
    // nor a TypeError. Classifying it as a client bug would both mislead the user
    // and take it out of the retry path.
    expect(toAppError(new DOMException('timed out', 'TimeoutError')).kind).toBe('network');
  });

  it('still maps a timeout to network when its signal is supplied', () => {
    // AbortSignal.timeout() sets `aborted` and rejects with a TimeoutError, so a
    // classifier that tested the signal first would call every timeout a
    // cancellation — and only for the callers that passed the signal.
    const signal = AbortSignal.timeout(0);

    return new Promise<void>((resolve) => {
      signal.addEventListener('abort', () => {
        expect(toAppError(signal.reason, { signal }).kind).toBe('network');
        resolve();
      });
    });
  });

  it('maps a transport failure to network even when the signal has since aborted', () => {
    // A component torn down in the same tick as a genuine fetch failure must not
    // turn that failure into "the user pressed Stop".
    const controller = new AbortController();
    controller.abort();

    expect(toAppError(new TypeError('Failed to fetch'), { signal: controller.signal }).kind).toBe(
      'network',
    );
  });

  it('maps an abort carrying a reason to aborted when the signal is supplied', () => {
    // abort(reason) rejects with the reason itself — often a plain string — so the
    // signal is the only evidence that the user pressed Stop rather than the
    // request failing.
    const controller = new AbortController();
    controller.abort('user stopped');

    expect(toAppError('user stopped', { signal: controller.signal }).kind).toBe('aborted');
  });

  it('maps a fetch TypeError to network', () => {
    expect(toAppError(new TypeError('Failed to fetch')).kind).toBe('network');
  });

  it('carries a caller-supplied url onto the transport arms', () => {
    expect(toAppError(new TypeError('Failed to fetch'), { url: URL }).url).toBe(URL);
  });

  it('maps a bare Response to http from its status alone', () => {
    const response = { status: 503, url: URL, text: async () => '' };

    const error = toAppError(response);

    expect(error.kind).toBe('http');
    expect(error.status).toBe(503);
    expect(error.url).toBe(URL);
  });

  it.each([
    ['an Error', new Error('boom')],
    ['a string', 'boom'],
    ['null', null],
    ['undefined', undefined],
    ['an empty object', {}],
    ['zero', 0],
  ])('maps %s to client without throwing', (_label, value) => {
    let error!: AppError;

    expect(() => (error = toAppError(value))).not.toThrow();
    expect(error.kind).toBe('client');
  });
});

describe('toAppError — shared contract', () => {
  const everyValue: unknown[] = [
    ...Object.values(PROBLEM_FIXTURES).map((body) => httpError(body, body.status)),
    ...Object.values(FRAMEWORK_PROBLEM_FIXTURES).map((body) => httpError(body, body.status)),
    httpError(null, 499),
    httpError(new ProgressEvent('error'), 0),
    new TypeError('Failed to fetch'),
    new DOMException('aborted', 'AbortError'),
    'boom',
    null,
  ];

  it('always produces a message, and a nullable trace id and url', () => {
    for (const value of everyValue) {
      const error = toAppError(value);

      expect(typeof error.message).toBe('string');
      expect(error.message.length).toBeGreaterThan(0);
      expect(error).toHaveProperty('traceId');
      expect(error).toHaveProperty('url');
      expect(error.cause).toBe(value);
    }
  });

  it('classifies every arm as problem-derived or not', () => {
    for (const value of everyValue) {
      expect(typeof isProblemAppError(toAppError(value))).toBe('boolean');
    }
  });
});

describe('isProblemAppError / isRouteNotFound', () => {
  it('reports an application problem as problem-derived', () => {
    expect(isProblemAppError(toAppError(httpError(PROBLEM_FIXTURES.forbidden, 403)))).toBe(true);
  });

  it.each([
    ['a 401', httpError(FRAMEWORK_PROBLEM_FIXTURES.unauthorized, 401)],
    ['a transport failure', new TypeError('Failed to fetch')],
    ['an abort', new DOMException('stop', 'AbortError')],
    ['a client throw', new Error('boom')],
  ])('does not report %s as problem-derived', (_label, value) => {
    expect(isProblemAppError(toAppError(value))).toBe(false);
  });

  it('separates a routing 404 from a domain not-found', () => {
    // The domain 404 is the API's deliberate answer for someone else's row and must
    // never be reported as "deleted"; the routing 404 is a client bug.
    expect(
      isRouteNotFound(toAppError(httpError(FRAMEWORK_PROBLEM_FIXTURES.routeNotFound, 404))),
    ).toBe(true);
    expect(isRouteNotFound(toAppError(httpError(PROBLEM_FIXTURES.resourceNotFound, 404)))).toBe(
      false,
    );
  });
});
