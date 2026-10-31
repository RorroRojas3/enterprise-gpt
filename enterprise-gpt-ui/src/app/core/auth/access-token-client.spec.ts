import { TestBed } from '@angular/core/testing';
import {
  BrowserAuthError,
  BrowserAuthErrorCodes,
  InteractionRequiredAuthError,
  SilentRequest,
} from '@azure/msal-browser';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { provideTestAppConfig, testAppConfig } from '@testing/app-config';
import {
  fakeAccount,
  fakeAuthResult,
  provideFakeMsal,
  signedInMsal,
  signedOutMsal,
} from '@testing/msal';
import { armUnloadPrompt, wouldPromptOnUnload } from '@testing/unload-prompt';
import { AccessTokenClient, InteractiveSignInRequiredError } from './access-token-client';

const CORRELATION_ID = '00000000-0000-0000-0000-000000000000';

describe('AccessTokenClient', () => {
  function service(instance: ReturnType<typeof signedInMsal>): AccessTokenClient {
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideFakeMsal(instance)],
    });

    return TestBed.inject(AccessTokenClient);
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('returns the access token for the active account', async () => {
    const tokens = service(signedInMsal());

    await expect(tokens.getToken()).resolves.toBe('access-token');
  });

  it('requests the API scopes from configuration rather than a hard-coded value', async () => {
    const acquireTokenSilent = vi.fn((_request: SilentRequest) =>
      Promise.resolve(fakeAuthResult()),
    );
    const tokens = service(signedInMsal({ acquireTokenSilent }));

    await tokens.getToken();

    expect(acquireTokenSilent.mock.calls[0]?.[0].scopes).toEqual(testAppConfig().auth.apiScopes);
  });

  it('shares one acquisition between concurrent callers', async () => {
    const acquireTokenSilent = vi.fn((_request: SilentRequest) =>
      Promise.resolve(fakeAuthResult()),
    );
    const tokens = service(signedInMsal({ acquireTokenSilent }));

    await Promise.all([tokens.getToken(), tokens.getToken(), tokens.getToken()]);

    expect(acquireTokenSilent).toHaveBeenCalledTimes(1);
  });

  it('re-acquires for a later caller, so a retried request never replays a rejected token', async () => {
    // The single-flight memo is scoped to one settle, not to the service's lifetime.
    const acquireTokenSilent = vi.fn((_request: SilentRequest) =>
      Promise.resolve(fakeAuthResult()),
    );
    const tokens = service(signedInMsal({ acquireTokenSilent }));

    await tokens.getToken();
    await tokens.getToken();

    expect(acquireTokenSilent).toHaveBeenCalledTimes(2);
  });

  it('does not join an in-flight acquisition when a refresh is forced', async () => {
    const acquireTokenSilent = vi.fn((_request: SilentRequest) =>
      Promise.resolve(fakeAuthResult()),
    );
    const tokens = service(signedInMsal({ acquireTokenSilent }));

    await Promise.all([tokens.getToken(), tokens.getToken({ forceRefresh: true })]);

    expect(acquireTokenSilent).toHaveBeenCalledTimes(2);
    expect(acquireTokenSilent.mock.calls.map(([request]) => request.forceRefresh)).toEqual([
      false,
      true,
    ]);
  });

  it('redirects for interaction when a token cannot be renewed silently', async () => {
    const acquireTokenRedirect = vi.fn(() => Promise.resolve());
    const tokens = service(
      signedInMsal({
        acquireTokenSilent: () =>
          Promise.reject(new InteractionRequiredAuthError('interaction_required', CORRELATION_ID)),
        acquireTokenRedirect,
      }),
    );

    await expect(tokens.getToken()).rejects.toBeInstanceOf(InteractiveSignInRequiredError);
    expect(acquireTokenRedirect).toHaveBeenCalledTimes(1);
  });

  it('redirects for interaction without a leave prompt that would strand its lock', async () => {
    let prompted: boolean | null = null;
    const tokens = service(
      signedInMsal({
        acquireTokenSilent: () =>
          Promise.reject(new InteractionRequiredAuthError('interaction_required', CORRELATION_ID)),
        acquireTokenRedirect: () => {
          prompted = wouldPromptOnUnload();
          return Promise.resolve();
        },
      }),
    );
    armUnloadPrompt();

    await expect(tokens.getToken()).rejects.toBeInstanceOf(InteractiveSignInRequiredError);

    expect(prompted).toBe(false);
  });

  it('keeps the guard down when another redirect already holds the lock', async () => {
    const tokens = service(
      signedInMsal({
        acquireTokenSilent: () =>
          Promise.reject(new InteractionRequiredAuthError('interaction_required', CORRELATION_ID)),
        acquireTokenRedirect: () =>
          Promise.reject(
            new BrowserAuthError(BrowserAuthErrorCodes.interactionInProgress, CORRELATION_ID),
          ),
      }),
    );
    armUnloadPrompt();

    await expect(tokens.getToken()).rejects.toBeInstanceOf(InteractiveSignInRequiredError);

    // The page is still leaving for the other redirect; a prompt now would strand its lock.
    expect(wouldPromptOnUnload()).toBe(false);
  });

  it('rejects without redirecting when nobody is signed in', async () => {
    // A guard should have run first, so this is a programming error, not a sign-in prompt.
    const acquireTokenRedirect = vi.fn(() => Promise.resolve());
    const tokens = service(signedOutMsal({ acquireTokenRedirect }));

    await expect(tokens.getToken()).rejects.toBeInstanceOf(InteractiveSignInRequiredError);
    expect(acquireTokenRedirect).not.toHaveBeenCalled();
  });

  it('falls back to the first account when none has been marked active', async () => {
    const account = fakeAccount();
    const tokens = service(
      signedInMsal({ getActiveAccount: () => null, getAllAccounts: () => [account] }),
    );

    await expect(tokens.getToken()).resolves.toBe('access-token');
  });
});
