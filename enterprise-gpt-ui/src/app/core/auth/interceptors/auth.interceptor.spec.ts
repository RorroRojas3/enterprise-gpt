import { HttpClient, provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { SilentRequest } from '@azure/msal-browser';
import { RETRY_POLICY } from '@core/errors/retry-policy';
import { retryInterceptor } from '@core/http/interceptors/retry.interceptor';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { settle } from '@testing/async';
import { fakeAuthResult, provideFakeMsal, signedInMsal } from '@testing/msal';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { authInterceptor } from './auth.interceptor';

const API_URL = `${TEST_API_BASE_URL}/api/models`;

describe('authInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;
  let acquireTokenSilent: ReturnType<typeof tokenSpy>;

  function tokenSpy() {
    let issued = 0;

    return vi.fn((_request: SilentRequest) =>
      Promise.resolve(fakeAuthResult({ accessToken: `token-${++issued}` })),
    );
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    acquireTokenSilent = tokenSpy();

    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideFakeMsal(signedInMsal({ acquireTokenSilent })),
        provideHttpClient(withFetch(), withInterceptors([retryInterceptor, authInterceptor])),
        provideHttpClientTesting(),
        // No jitter and no delay, so the spec needs no fake timers.
        {
          provide: RETRY_POLICY,
          useValue: { maxRetries: 3, baseDelayMs: 0, capMs: 0, random: () => 0 },
        },
      ],
    });

    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
  });

  it('attaches the bearer token to an API request', async () => {
    const response = firstValueFrom(http.get(API_URL));
    await settle();

    const request = backend.expectOne(API_URL);
    expect(request.request.headers.get('Authorization')).toBe('Bearer token-1');

    request.flush([]);
    await response;
  });

  it('leaves a request to another origin untouched', async () => {
    // The token is a bearer credential for our API alone. An interceptor that
    // attached it to every request would hand it to any third-party host a later
    // feature calls, and to config.json and the icon sprite besides.
    const response = firstValueFrom(http.get('https://example.test/thing'));
    await settle();

    const request = backend.expectOne('https://example.test/thing');
    expect(request.request.headers.has('Authorization')).toBe(false);

    request.flush({});
    await response;
  });

  it('does not attach the token to an origin that merely starts with the API base URL', async () => {
    const lookalike = `${TEST_API_BASE_URL}.evil.test/api/models`;
    const response = firstValueFrom(http.get(lookalike));
    await settle();

    const request = backend.expectOne(lookalike);
    expect(request.request.headers.has('Authorization')).toBe(false);

    request.flush({});
    await response;
  });

  it('replays a 401 once with a freshly refreshed token', async () => {
    const response = firstValueFrom(http.get(API_URL));
    await settle();

    backend.expectOne(API_URL).flush('', { status: 401, statusText: 'Unauthorized' });
    await settle();

    const replay = backend.expectOne(API_URL);
    expect(replay.request.headers.get('Authorization')).toBe('Bearer token-2');
    expect(acquireTokenSilent.mock.calls.map(([request]) => request.forceRefresh)).toEqual([
      false,
      true,
    ]);

    replay.flush([]);
    await response;
  });

  it('gives up after one refresh rather than looping on a persistent 401', async () => {
    const response = firstValueFrom(http.get(API_URL)).catch((error: unknown) => error);
    await settle();

    backend.expectOne(API_URL).flush('', { status: 401, statusText: 'Unauthorized' });
    await settle();

    backend.expectOne(API_URL).flush('', { status: 401, statusText: 'Unauthorized' });
    await settle();

    expect(await response).toMatchObject({ status: 401 });
  });

  it('never refreshes on an MCP consent requirement', async () => {
    // A 403 asking for interactive consent to a downstream server cannot be satisfied
    // by any number of token refreshes, so entering that path would spin forever.
    const response = firstValueFrom(http.get(API_URL)).catch((error: unknown) => error);
    await settle();

    backend
      .expectOne(API_URL)
      .flush(PROBLEM_FIXTURES.mcpAuthorizationRequired, { status: 403, statusText: 'Forbidden' });
    await settle();

    expect(await response).toMatchObject({ status: 403 });
    expect(acquireTokenSilent).toHaveBeenCalledTimes(1);
  });

  it('re-acquires a token for every retried attempt', async () => {
    // The contract retry.interceptor.ts has documented since US-103: retry sits
    // outside authentication, so a replayed request carries a token acquired for that
    // attempt rather than the one that already failed. It holds only because
    // authorize() defers the acquisition until subscription — a bare from(promise)
    // would replay one settled token three times.
    const response = firstValueFrom(http.get(API_URL));
    await settle();

    backend.expectOne(API_URL).flush('', { status: 502, statusText: 'Bad Gateway' });
    await settle();

    const second = backend.expectOne(API_URL);
    expect(second.request.headers.get('Authorization')).toBe('Bearer token-2');
    second.flush('', { status: 502, statusText: 'Bad Gateway' });
    await settle();

    const third = backend.expectOne(API_URL);
    expect(third.request.headers.get('Authorization')).toBe('Bearer token-3');

    third.flush([]);
    await response;
  });
});
