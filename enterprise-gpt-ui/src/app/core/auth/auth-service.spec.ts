import { TestBed } from '@angular/core/testing';
import {
  EndSessionRequest,
  HandleRedirectPromiseOptions,
  RedirectRequest,
} from '@azure/msal-browser';
import { provideTestAppConfig, testAppConfig } from '@testing/app-config';
import {
  fakeAccount,
  fakeAuthResult,
  provideFakeMsal,
  signedInMsal,
  signedOutMsal,
} from '@testing/msal';
import { IPublicClientApplication } from '@azure/msal-browser';
import { Dispatcher, Events } from '@ngrx/signals/events';
import { sessionEvents } from '@core/events/session-events';
import { FakeNavigation, provideFakeNavigation } from '@testing/navigation';
import { HardNavigation } from '@core/navigation/hard-navigation';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthService } from './auth-service';
import { SIGNED_OUT_ROUTE } from './auth-routes';

describe('AuthService', () => {
  function service(instance: IPublicClientApplication): AuthService {
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideFakeMsal(instance), provideFakeNavigation()],
    });

    return TestBed.inject(AuthService);
  }

  function navigation(): FakeNavigation {
    return TestBed.inject(HardNavigation) as unknown as FakeNavigation;
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

  describe('signOut', () => {
    /** A logoutRedirect that behaves like the happy path: the browser leaves, so it never settles. */
    const leavesThePage = (_request?: EndSessionRequest): Promise<void> =>
      new Promise<void>(() => undefined);

    function signedOutEvents(): ReturnType<typeof vi.fn> {
      const seen = vi.fn();
      TestBed.inject(Events).on(sessionEvents.signedOut).subscribe(seen);
      return seen;
    }

    it('ends the session before it asks Entra to, and latches', () => {
      const auth = service(signedInMsal({ logoutRedirect: leavesThePage }));
      const seen = signedOutEvents();

      void auth.signOut();

      expect(seen).toHaveBeenCalledOnce();
      expect(auth.isSigningOut()).toBe(true);
    });

    it('logs out the account that was active before the cache was cleared', () => {
      const logoutRedirect = vi.fn(leavesThePage);
      const auth = service(signedInMsal({ logoutRedirect }));
      const account = auth.account;

      void auth.signOut();

      expect(logoutRedirect).toHaveBeenCalledWith({ account });
      // Never the request's own postLogoutRedirectUri: buildMsalConfig already resolves
      // it absolutely, and a second source is a drift waiting to happen.
      expect(logoutRedirect.mock.calls[0]?.[0]).not.toHaveProperty('postLogoutRedirectUri');
    });

    it('ignores a second press rather than starting a second redirect', () => {
      const logoutRedirect = vi.fn(leavesThePage);
      const auth = service(signedInMsal({ logoutRedirect }));
      const seen = signedOutEvents();

      void auth.signOut();
      void auth.signOut();

      expect(logoutRedirect).toHaveBeenCalledOnce();
      expect(seen).toHaveBeenCalledOnce();
    });

    it('wipes local state outright and lands on the signed-out page when Entra is unreachable', async () => {
      // The failure that matters: MSAL has already cleared its cache by the time
      // authority discovery throws, and the Entra session cookie is still live. Landing
      // on postLogoutRedirectUri would take the user through /chat and authGuard
      // straight back into a silent re-authentication.
      const clearCache = vi.fn(() => Promise.resolve());
      const auth = service(
        signedInMsal({
          logoutRedirect: () => Promise.reject(new Error('endpoints_resolution_error')),
          clearCache,
        }),
      );

      await auth.signOut();

      // No argument: with an account this removes only that account, where the bare
      // call clears browser storage and the crypto keystore.
      expect(clearCache).toHaveBeenCalledWith();
      expect(navigation().assign).toHaveBeenCalledWith(SIGNED_OUT_ROUTE);
    });

    it('still leaves the page when even the local wipe fails', async () => {
      const auth = service(
        signedInMsal({
          logoutRedirect: () => Promise.reject(new Error('interaction_in_progress')),
          clearCache: () => Promise.reject(new Error('storage denied')),
        }),
      );

      await expect(auth.signOut()).resolves.toBeUndefined();
      expect(navigation().assign).toHaveBeenCalledWith(SIGNED_OUT_ROUTE);
    });

    it('stays latched after a failed logout, so nothing re-activates on the way out', async () => {
      const auth = service(
        signedInMsal({ logoutRedirect: () => Promise.reject(new Error('nope')) }),
      );

      await auth.signOut();

      expect(auth.isSigningOut()).toBe(true);
    });

    it('clears the handshake memo through its own event', async () => {
      // The self-loop: signOut dispatches the event this service also subscribes to.
      // Clearing the memo is what lets the next ready() release a stuck SIGNOUT lock.
      const handleRedirectPromise = vi.fn(() => Promise.resolve(null));
      const auth = service(signedInMsal({ handleRedirectPromise, logoutRedirect: leavesThePage }));

      await auth.ready();
      void auth.signOut();
      await auth.ready();

      expect(handleRedirectPromise).toHaveBeenCalledTimes(2);
    });

    it('forgets the return URL and the attempt counter', async () => {
      const auth = service(
        signedInMsal({ logoutRedirect: leavesThePage, loginRedirect: () => Promise.resolve() }),
      );

      await auth.signIn('/chat/abc');
      auth.noteInconclusiveHandshake();
      void auth.signOut();

      expect(sessionStorage.getItem('egpt.returnUrl')).toBeNull();
      expect(sessionStorage.getItem('egpt.authAttempts')).toBeNull();
    });
  });
});
