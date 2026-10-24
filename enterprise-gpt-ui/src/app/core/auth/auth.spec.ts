import { TestBed } from '@angular/core/testing';
import {
  EndSessionRequest,
  HandleRedirectPromiseOptions,
  IPublicClientApplication,
  RedirectRequest,
} from '@azure/msal-browser';
import { Dispatcher, Events } from '@ngrx/signals/events';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { sessionEvents } from '@core/events/session-events';
import { HardNavigation } from '@core/navigation/hard-navigation';
import { SIGNED_OUT_ROUTE } from '@core/navigation/route-paths';
import { provideTestAppConfig, testAppConfig } from '@testing/app-config';
import {
  fakeAccount,
  fakeAuthResult,
  provideFakeMsal,
  signedInMsal,
  signedOutMsal,
} from '@testing/msal';
import { FakeNavigation, provideFakeNavigation } from '@testing/navigation';
import { Auth } from './auth';

describe('Auth', () => {
  function service(instance: IPublicClientApplication): Auth {
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideFakeMsal(instance), provideFakeNavigation()],
    });

    return TestBed.inject(Auth);
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
    // The service reads the account back through MSAL, so the double must honour setActiveAccount.
    expect(auth.account).toEqual(account);
  });

  it('reports live MSAL state rather than the memoized redirect result', async () => {
    // MSAL replays the first redirect response, account included, on every later call.
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
    // The first is the benign "opened /auth directly" case; the second is the loop.
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
    sessionStorage.setItem('egpt.returnUrl', '//evil.test/steal');
    const auth = service(signedInMsal());

    expect(auth.consumeReturnUrl()).toBe('/chat');
  });

  describe('signOut', () => {
    /** The happy path: the browser leaves, so logoutRedirect never settles. */
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
      // buildMsalConfig is the single source of postLogoutRedirectUri.
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
      // The Entra cookie is still live, so landing on /chat would silently re-authenticate.
      const clearCache = vi.fn(() => Promise.resolve());
      const auth = service(
        signedInMsal({
          logoutRedirect: () => Promise.reject(new Error('endpoints_resolution_error')),
          clearCache,
        }),
      );

      await auth.signOut();

      // The bare call clears all browser storage; an account argument would remove only that one.
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
      // signOut dispatches the event this service also subscribes to.
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
