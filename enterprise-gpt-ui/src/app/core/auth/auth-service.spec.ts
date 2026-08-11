import { TestBed } from '@angular/core/testing';
import { HandleRedirectPromiseOptions, RedirectRequest } from '@azure/msal-browser';
import { provideTestAppConfig, testAppConfig } from '@testing/app-config';
import {
  fakeAccount,
  fakeAuthResult,
  provideFakeMsal,
  signedInMsal,
  signedOutMsal,
} from '@testing/msal';
import { IPublicClientApplication } from '@azure/msal-browser';
import { Dispatcher } from '@ngrx/signals/events';
import { sessionEvents } from '@core/events/session-events';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthService } from './auth-service';

describe('AuthService', () => {
  function service(instance: IPublicClientApplication): AuthService {
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideFakeMsal(instance)],
    });

    return TestBed.inject(AuthService);
  }

  function signOut(): void {
    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    sessionStorage.clear();
  });

  it('turns off navigateToLoginRequestUrl when resolving the redirect', async () => {
    // Left at its v5 default of true, MSAL answers the redirect with a
    // location.replace back to the originating URL — a second full page load, a
    // second config fetch and a second bootstrap. The return trip is the router's.
    const handleRedirectPromise = vi.fn((_options?: HandleRedirectPromiseOptions) =>
      Promise.resolve(null),
    );
    const auth = service(signedOutMsal({ handleRedirectPromise }));

    await auth.ready();

    expect(handleRedirectPromise.mock.calls[0]?.[0]).toMatchObject({
      navigateToLoginRequestUrl: false,
    });
  });

  it('resolves the redirect once however many callers ask', async () => {
    // The handshake is what clears MSAL's interaction_in_progress lock, so it runs on
    // every page load — but only once, or two callers race the same response.
    const handleRedirectPromise = vi.fn(() => Promise.resolve(null));
    const auth = service(signedOutMsal({ handleRedirectPromise }));

    await Promise.all([auth.ready(), auth.ready(), auth.ready()]);

    expect(handleRedirectPromise).toHaveBeenCalledTimes(1);
  });

  it('selects the account the redirect returned as active', async () => {
    const account = fakeAccount();
    const auth = service(
      signedOutMsal({
        handleRedirectPromise: () => Promise.resolve(fakeAuthResult({ account })),
      }),
    );

    await expect(auth.ready()).resolves.toMatchObject({ account, error: null });
    // Read back through MSAL rather than from the result, because that is what the
    // service does — the double therefore has to make setActiveAccount mean something.
    expect(auth.account).toEqual(account);
  });

  it('reports live MSAL state rather than the memoized redirect result', async () => {
    // MSAL caches handleRedirectPromise per hash for the lifetime of the application
    // instance, so a second call replays the first response — account included. Taking
    // the account from that replay would report a user MSAL has since evicted as still
    // signed in, which is precisely what clearing the memo on sign-out exists to stop.
    const account = fakeAccount();
    const instance = signedOutMsal({
      handleRedirectPromise: () => Promise.resolve(fakeAuthResult({ account })),
    });
    const auth = service(instance);

    await auth.ready();
    signOut();
    instance.setActiveAccount(null);

    await expect(auth.ready()).resolves.toMatchObject({ account: null });
  });

  it('drops the handshake on sign-out so the next user is resolved afresh', async () => {
    const handleRedirectPromise = vi.fn(() => Promise.resolve(null));
    const auth = service(signedOutMsal({ handleRedirectPromise }));

    await auth.ready();
    signOut();
    await auth.ready();

    expect(handleRedirectPromise).toHaveBeenCalledTimes(2);
  });

  it('lets one inconclusive handshake through, then stops', async () => {
    // A handshake with neither an account nor an error is a real MSAL outcome, and
    // passing every one of them onward is an unbounded /auth → /chat → Entra loop. The
    // first is the benign "opened /auth directly" case; the second is the loop.
    const auth = service(signedOutMsal());

    expect(auth.noteInconclusiveHandshake()).toBe(false);
    expect(auth.noteInconclusiveHandshake()).toBe(true);
  });

  it('forgets inconclusive handshakes once an account is established', async () => {
    const account = fakeAccount();
    const auth = service(
      signedOutMsal({
        handleRedirectPromise: () => Promise.resolve(fakeAuthResult({ account })),
      }),
    );

    auth.noteInconclusiveHandshake();
    await auth.ready();

    expect(auth.noteInconclusiveHandshake()).toBe(false);
  });

  it('reports a failed redirect as a normalized error rather than throwing', async () => {
    const auth = service(
      signedOutMsal({ handleRedirectPromise: () => Promise.reject(new Error('state mismatch')) }),
    );

    const { account, error } = await auth.ready();

    expect(account).toBeNull();
    expect(error?.kind).toBe('client');
  });

  it('requests the API scopes at sign-in so consent is gathered once', async () => {
    const loginRedirect = vi.fn((_request: RedirectRequest) => Promise.resolve());
    const auth = service(signedOutMsal({ loginRedirect }));

    await auth.signIn('/chat/abc');

    expect(loginRedirect.mock.calls[0]?.[0].scopes).toEqual(testAppConfig().auth.apiScopes);
  });

  it('carries the requested route across the trip to Entra', async () => {
    const auth = service(signedOutMsal({ loginRedirect: () => Promise.resolve() }));

    await auth.signIn('/chat/abc?tab=files');

    expect(auth.consumeReturnUrl()).toBe('/chat/abc?tab=files');
  });

  it('consumes the stored route, so a later sign-in does not reuse it', async () => {
    const auth = service(signedInMsal({ loginRedirect: () => Promise.resolve() }));

    await auth.signIn('/chat/abc');
    auth.consumeReturnUrl();

    expect(auth.consumeReturnUrl()).toBe('/chat');
  });

  it('refuses a stored route that is not an in-app path', async () => {
    // The value survives a round trip through the browser, so it is treated as
    // untrusted: a protocol-relative or absolute URL here is an open redirect.
    sessionStorage.setItem('egpt.returnUrl', '//evil.test/steal');
    const auth = service(signedInMsal());

    expect(auth.consumeReturnUrl()).toBe('/chat');
  });
});
