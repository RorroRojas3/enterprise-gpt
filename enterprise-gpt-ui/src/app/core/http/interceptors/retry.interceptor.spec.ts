import {
  HttpErrorResponse,
  HttpEvent,
  HttpHandlerFn,
  HttpRequest,
  HttpResponse,
} from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Observable, defer, firstValueFrom, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FRAMEWORK_PROBLEM_FIXTURES, PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { DEFAULT_RETRY_POLICY, RETRY_POLICY } from '../../errors/retry-policy';
import { retryInterceptor, skipRetry } from './retry.interceptor';

const URL = 'https://localhost:7045/api/conversations';

interface RunResult {
  readonly attempts: number;
  readonly caught: unknown;
}

/** Runs the interceptor with virtual time so the backoff does not slow the suite. */
async function run(
  request: HttpRequest<unknown>,
  failure: HttpErrorResponse | null,
): Promise<RunResult> {
  let attempts = 0;
  // Counted per subscription, not per call: `retry` resubscribes to the observable
  // the handler returned, which is what re-issues the request in the real chain.
  const next: HttpHandlerFn = (): Observable<HttpEvent<unknown>> =>
    defer(() => {
      attempts++;

      return failure
        ? throwError(() => failure)
        : new Observable<HttpEvent<unknown>>((subscriber) => {
            subscriber.next(new HttpResponse({ status: 200, url: URL }));
            subscriber.complete();
          });
    });

  const pending = TestBed.runInInjectionContext(() =>
    firstValueFrom(retryInterceptor(request, next)),
  );

  let caught: unknown = null;
  const settled = pending.catch((error: unknown) => {
    caught = error;
  });

  await vi.runAllTimersAsync();
  await settled;

  return { attempts, caught };
}

function get(): HttpRequest<unknown> {
  return new HttpRequest('GET', URL);
}

function post(): HttpRequest<unknown> {
  return new HttpRequest('POST', URL, {});
}

/** A GET that has opted out — what the conversation export issues. */
function getWithoutRetry(): HttpRequest<unknown> {
  return new HttpRequest('GET', URL, { context: skipRetry() });
}

function failure(body: unknown, status: number): HttpErrorResponse {
  return new HttpErrorResponse({ error: body, status, url: URL });
}

beforeEach(() => {
  vi.useFakeTimers();
  TestBed.configureTestingModule({
    providers: [{ provide: RETRY_POLICY, useValue: { ...DEFAULT_RETRY_POLICY, random: () => 0 } }],
  });
});

afterEach(() => {
  vi.useRealTimers();
});

describe('retryInterceptor', () => {
  /**
   * The opt-out exists for one reason: a `responseType: 'blob'` request's error body is a
   * `Blob` that `parseProblemDetails` rejects, so an application-typed 503 — which must
   * never be replayed — is indistinguishable from a gateway 503, which should be. Three
   * blind round trips delay an actionable message by seconds and cannot succeed.
   */
  it('does not retry a GET that opted out, even on a retriable status', async () => {
    const { attempts, caught } = await run(
      getWithoutRetry(),
      failure({ type: 'about:blank', status: 503 }, 503),
    );

    expect(attempts).toBe(1);
    expect(caught).toBeInstanceOf(HttpErrorResponse);
  });

  it('still retries an ordinary GET beside it', async () => {
    const { attempts } = await run(get(), failure({ type: 'about:blank', status: 503 }, 503));

    expect(attempts).toBeGreaterThan(1);
  });

  it('passes an opted-out success straight through', async () => {
    const { attempts, caught } = await run(getWithoutRetry(), null);

    expect(attempts).toBe(1);
    expect(caught).toBeNull();
  });

  it('retries an untyped 502 on a GET up to the configured count', async () => {
    const { attempts } = await run(get(), failure({ type: 'about:blank', status: 502 }, 502));

    expect(attempts).toBe(DEFAULT_RETRY_POLICY.maxRetries + 1);
  });

  it('retries a transport failure on a GET', async () => {
    const { attempts } = await run(get(), failure(new ProgressEvent('error'), 0));

    expect(attempts).toBe(DEFAULT_RETRY_POLICY.maxRetries + 1);
  });

  it('never retries a 409, at any interval', async () => {
    const { attempts } = await run(get(), failure(PROBLEM_FIXTURES.conversationBusy, 409));

    expect(attempts).toBe(1);
  });

  it('does not retry a 503 that names a deployment misconfiguration', async () => {
    const { attempts } = await run(get(), failure(PROBLEM_FIXTURES.providerNotConfigured, 503));

    expect(attempts).toBe(1);
  });

  it('retries an untyped 503', async () => {
    const { attempts } = await run(get(), failure({ type: 'about:blank', status: 503 }, 503));

    expect(attempts).toBe(DEFAULT_RETRY_POLICY.maxRetries + 1);
  });

  it('passes an mcp-authorization-required 403 through unchanged and unretried', async () => {
    const original = failure(PROBLEM_FIXTURES.mcpAuthorizationRequired, 403);

    const { attempts, caught } = await run(get(), original);

    expect(attempts).toBe(1);
    // The identical instance: downstream handlers must see what the server sent.
    expect(caught).toBe(original);
  });

  it('passes a 401 through without retrying, leaving refresh to the auth layer', async () => {
    const { attempts } = await run(get(), failure(FRAMEWORK_PROBLEM_FIXTURES.unauthorized, 401));

    expect(attempts).toBe(1);
  });

  it('does not retry a non-idempotent method', async () => {
    const { attempts } = await run(post(), failure({ type: 'about:blank', status: 502 }, 502));

    expect(attempts).toBe(1);
  });

  it('leaves a successful response alone', async () => {
    const { attempts, caught } = await run(get(), null);

    expect(attempts).toBe(1);
    expect(caught).toBeNull();
  });

  it('honours a policy that disables retries', async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [{ provide: RETRY_POLICY, useValue: { ...DEFAULT_RETRY_POLICY, maxRetries: 0 } }],
    });

    const { attempts } = await run(get(), failure({ type: 'about:blank', status: 502 }, 502));

    expect(attempts).toBe(1);
  });
});
