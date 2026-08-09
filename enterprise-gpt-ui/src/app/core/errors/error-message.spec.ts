import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it } from 'vitest';
import { FRAMEWORK_PROBLEM_FIXTURES, PROBLEM_FIXTURES, TRACE_ID } from '@testing/problem-fixtures';
import { shouldNotify, traceLine, userMessage } from './error-message';
import { toAppError } from './to-app-error';

function errorFrom(body: unknown, status: number) {
  return toAppError(
    new HttpErrorResponse({ error: body, status, url: 'https://localhost:7045/api/x' }),
  );
}

describe('traceLine', () => {
  it('shows the trace id from the problem body', () => {
    expect(traceLine(errorFrom(PROBLEM_FIXTURES.forbidden, 403))).toBe(`Trace ID: ${TRACE_ID}`);
  });

  it('says so plainly when there is no trace id', () => {
    // A 499 carries no body at all, and the trace id is body-only — the API's CORS
    // policy exposes no header to read it from.
    expect(traceLine(errorFrom(null, 499))).toBe('No trace ID was returned.');
  });

  it('says so plainly for a transport failure', () => {
    expect(traceLine(toAppError(new TypeError('Failed to fetch')))).toBe(
      'No trace ID was returned.',
    );
  });
});

describe('userMessage', () => {
  it('names the MCP server that needs authorization', () => {
    expect(userMessage(errorFrom(PROBLEM_FIXTURES.mcpAuthorizationRequired, 403))).toContain(
      'Weather',
    );
  });

  it('names the MCP server that could not be reached', () => {
    expect(userMessage(errorFrom(PROBLEM_FIXTURES.mcpServerUnavailable, 502))).toContain('Weather');
  });

  it('names the missing permission by its display name', () => {
    expect(userMessage(errorFrom(PROBLEM_FIXTURES.permissionRequired, 403))).toContain(
      'Upload File',
    );
  });

  it('formats the upload size limit in megabytes', () => {
    expect(userMessage(errorFrom(PROBLEM_FIXTURES.uploadTooLarge, 400))).toContain('50 MB');
  });

  it('points at the fields rather than repeating the server title for a validation error', () => {
    expect(userMessage(errorFrom(PROBLEM_FIXTURES.validationError, 400))).toMatch(/fields/i);
  });

  it('explains an expired session for a 401', () => {
    expect(userMessage(errorFrom(FRAMEWORK_PROBLEM_FIXTURES.unauthorized, 401))).toMatch(
      /sign in again/i,
    );
  });

  it('explains a transport failure', () => {
    expect(userMessage(toAppError(new TypeError('Failed to fetch')))).toMatch(/connection/i);
  });

  it('produces a non-empty sentence for every fixture', () => {
    for (const body of [
      ...Object.values(PROBLEM_FIXTURES),
      ...Object.values(FRAMEWORK_PROBLEM_FIXTURES),
    ]) {
      expect(userMessage(errorFrom(body, body.status)).length).toBeGreaterThan(0);
    }
  });
});

describe('shouldNotify', () => {
  it('stays silent when the user pressed Stop', () => {
    // Toasting a cancellation tells the user their own deliberate action failed.
    expect(shouldNotify(toAppError(new DOMException('stop', 'AbortError')))).toBe(false);
  });

  it('stays silent for a validation error, which renders against its own fields', () => {
    expect(shouldNotify(errorFrom(PROBLEM_FIXTURES.validationError, 400))).toBe(false);
  });

  it.each([
    ['forbidden', PROBLEM_FIXTURES.forbidden, 403],
    ['conversation-busy', PROBLEM_FIXTURES.conversationBusy, 409],
    ['mcp-server-unavailable', PROBLEM_FIXTURES.mcpServerUnavailable, 502],
    ['a server error', FRAMEWORK_PROBLEM_FIXTURES.serverError, 500],
  ])('notifies for %s', (_label, body, status) => {
    expect(shouldNotify(errorFrom(body, status))).toBe(true);
  });

  it('notifies for a transport failure', () => {
    expect(shouldNotify(toAppError(new TypeError('Failed to fetch')))).toBe(true);
  });
});
