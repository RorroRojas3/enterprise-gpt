import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it } from 'vitest';
import { FRAMEWORK_PROBLEM_FIXTURES, PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { AppErrorKind } from './app-error';
import {
  AUTH_DECISIONS,
  AuthErrorDecision,
  DEFAULT_RETRY_POLICY,
  RetryPolicy,
  authErrorDecision,
  computeBackoffDelay,
  isTransientRetriable,
} from './retry-policy';
import { toAppError } from './to-app-error';

function errorFrom(body: unknown, status: number) {
  return toAppError(
    new HttpErrorResponse({ error: body, status, url: 'https://localhost:7045/api/x' }),
  );
}

describe('isTransientRetriable', () => {
  it.each([502, 503, 504])('retries an untyped %d', (status) => {
    expect(isTransientRetriable(errorFrom({ type: 'about:blank', status }, status))).toBe(true);
  });

  it('retries a transport failure', () => {
    expect(isTransientRetriable(toAppError(new TypeError('Failed to fetch')))).toBe(true);
  });

  it('never retries a conversation-busy 409', () => {
    expect(isTransientRetriable(errorFrom(PROBLEM_FIXTURES.conversationBusy, 409))).toBe(false);
  });

  it.each([
    ['provider-not-configured', PROBLEM_FIXTURES.providerNotConfigured],
    ['storage-not-configured', PROBLEM_FIXTURES.storageNotConfigured],
    ['export-renderer-not-configured', PROBLEM_FIXTURES.exportRendererNotConfigured],
  ])('does not retry the 503 %s, a deterministic deployment state', (_label, body) => {
    expect(isTransientRetriable(errorFrom(body, 503))).toBe(false);
  });

  it('does not retry mcp-server-unavailable, whose 502 names a specific server', () => {
    expect(isTransientRetriable(errorFrom(PROBLEM_FIXTURES.mcpServerUnavailable, 502))).toBe(false);
  });

  it.each([400, 401, 403, 404, 409, 500])('does not retry a %d', (status) => {
    expect(isTransientRetriable(errorFrom({ type: 'about:blank', status }, status))).toBe(false);
  });

  it('does not retry an aborted request', () => {
    expect(isTransientRetriable(toAppError(new DOMException('stop', 'AbortError')))).toBe(false);
  });

  it('does not retry a client-side throw', () => {
    expect(isTransientRetriable(toAppError(new Error('boom')))).toBe(false);
  });
});

describe('computeBackoffDelay', () => {
  const policy = (random: () => number): RetryPolicy => ({ ...DEFAULT_RETRY_POLICY, random });

  it('doubles the window per attempt at the jitter upper bound', () => {
    const upper = policy(() => 0.999_999);

    expect(computeBackoffDelay(0, upper)).toBe(499);
    expect(computeBackoffDelay(1, upper)).toBe(999);
    expect(computeBackoffDelay(2, upper)).toBe(1_999);
  });

  it('draws from zero at the jitter lower bound, so retries are not synchronized', () => {
    const lower = policy(() => 0);

    expect(computeBackoffDelay(0, lower)).toBe(0);
    expect(computeBackoffDelay(5, lower)).toBe(0);
  });

  it('caps the window however many attempts have elapsed', () => {
    const upper = policy(() => 0.999_999);

    // Exact, not `toBeLessThan`: a bound would also pass for an implementation
    // that under-delays, or returns zero.
    expect(computeBackoffDelay(20, upper)).toBe(4_999);
  });

  it('keeps the default policy under four seconds of added latency at the upper bound', () => {
    const upper = policy(() => 0.999_999);
    const total = [0, 1, 2].reduce((sum, attempt) => sum + computeBackoffDelay(attempt, upper), 0);

    expect(total).toBeLessThan(4_000);
  });
});

describe('authErrorDecision', () => {
  it('refreshes only on a bare 401', () => {
    expect(authErrorDecision(errorFrom(FRAMEWORK_PROBLEM_FIXTURES.unauthorized, 401))).toBe(
      'refresh',
    );
  });

  it.each([
    ['forbidden', PROBLEM_FIXTURES.forbidden, 403],
    ['permission-required', PROBLEM_FIXTURES.permissionRequired, 403],
    ['resource-not-found', PROBLEM_FIXTURES.resourceNotFound, 404],
    ['conversation-busy', PROBLEM_FIXTURES.conversationBusy, 409],
    ['mcp-server-unavailable', PROBLEM_FIXTURES.mcpServerUnavailable, 502],
  ])('passes %s through', (_label, body, status) => {
    expect(authErrorDecision(errorFrom(body, status))).toBe('passthrough');
  });

  it.each([
    ['network', toAppError(new TypeError('Failed to fetch'))],
    ['aborted', toAppError(new DOMException('stop', 'AbortError'))],
    ['client', toAppError('boom')],
  ])('passes a %s failure through', (_label, error) => {
    expect(authErrorDecision(error)).toBe('passthrough');
  });

  it('passes a non-401 http failure through', () => {
    expect(authErrorDecision(errorFrom(FRAMEWORK_PROBLEM_FIXTURES.serverError, 500))).toBe(
      'passthrough',
    );
  });

  it('decides every arm of the union, and only http may refresh', () => {
    // Asserted against the real table, so adding an arm to AppError fails the
    // suite as well as the compiler. A hard-coded list compared to itself would
    // pass with the implementation deleted.
    const kinds: AppErrorKind[] = [
      'validation-error',
      'upload-too-large',
      'resource-not-found',
      'forbidden',
      'permission-required',
      'conversation-busy',
      'mcp-server-unavailable',
      'mcp-credential-required',
      'mcp-credential-rejected',
      'provider-not-configured',
      'storage-not-configured',
      'export-renderer-not-configured',
      'http',
      'network',
      'aborted',
      'client',
    ];

    expect(Object.keys(AUTH_DECISIONS).sort()).toEqual([...kinds].sort());

    const refreshable = Object.entries(AUTH_DECISIONS)
      .filter(([, decision]) => (decision as AuthErrorDecision) === 'refresh')
      .map(([kind]) => kind);
    expect(refreshable).toEqual(['http']);
  });
});
