import { Provider } from '@angular/core';
import {
  AccountInfo,
  AuthenticationResult,
  IPublicClientApplication,
  stubbedPublicClientApplication,
} from '@azure/msal-browser';
import { MSAL_INSTANCE } from '../app/core/auth/msal-instance';

/**
 * A test double for the MSAL application.
 *
 * Built by spreading msal-browser's own `stubbedPublicClientApplication`, which is not
 * usable as a double on its own — every one of its methods rejects. That is exactly
 * what makes it the right base: the spread supplies all of `IPublicClientApplication`
 * so the object typechecks without a cast, and any method the code under test calls
 * that a test did not deliberately stub fails with a *named MSAL error* rather than
 * "undefined is not a function".
 *
 * A real `PublicClientApplication` cannot run here at all — jsdom has no
 * `crypto.subtle` — which is the same conclusion from the other direction. Do not
 * polyfill it; that would only enable a test that should not exist.
 *
 * @param overrides The members this test needs.
 */
export function fakeMsalInstance(
  overrides: Partial<IPublicClientApplication> = {},
): IPublicClientApplication {
  return {
    ...stubbedPublicClientApplication,
    // Called by every code path that touches the instance, and never interesting.
    getLogger: stubbedPublicClientApplication.getLogger,
    ...overrides,
  };
}

/** A representative signed-in account. */
export function fakeAccount(overrides: Partial<AccountInfo> = {}): AccountInfo {
  return {
    homeAccountId: 'f1e2d3c4-b5a6-4978-8a9b-0c1d2e3f4a5b.9c8b7a65-4321-4f0e-9d8c-7b6a5f4e3d2c',
    environment: 'login.microsoftonline.com',
    tenantId: '9c8b7a65-4321-4f0e-9d8c-7b6a5f4e3d2c',
    localAccountId: 'f1e2d3c4-b5a6-4978-8a9b-0c1d2e3f4a5b',
    username: 'ada.lovelace@example.com',
    name: 'Ada Lovelace',
    ...overrides,
  };
}

/** A minimal successful token acquisition. */
export function fakeAuthResult(
  overrides: Partial<AuthenticationResult> = {},
): AuthenticationResult {
  const account = overrides.account ?? fakeAccount();

  return {
    authority: 'https://login.microsoftonline.com/9c8b7a65-4321-4f0e-9d8c-7b6a5f4e3d2c',
    uniqueId: account.localAccountId,
    tenantId: account.tenantId,
    scopes: ['api://b40defa0-0000-4000-8000-000000000000/access_as_user'],
    account,
    idToken: '',
    idTokenClaims: {},
    accessToken: 'access-token',
    fromCache: false,
    expiresOn: null,
    tokenType: 'Bearer',
    correlationId: '00000000-0000-0000-0000-000000000000',
    ...overrides,
  };
}

/**
 * An instance with nobody signed in and no redirect outstanding.
 *
 * `setActiveAccount` mutates what `getActiveAccount` and `getAllAccounts` report,
 * because code under test reads live MSAL state back rather than trusting the result
 * it was handed — a no-op stub would make that indistinguishable from a bug.
 */
export function signedOutMsal(overrides: Partial<IPublicClientApplication> = {}) {
  let active: AccountInfo | null = null;

  return fakeMsalInstance({
    handleRedirectPromise: () => Promise.resolve(null),
    getAllAccounts: () => (active ? [active] : []),
    getActiveAccount: () => active,
    setActiveAccount: (account: AccountInfo | null) => {
      active = account;
    },
    ...overrides,
  });
}

/** An instance with an active account and no redirect outstanding. */
export function signedInMsal(overrides: Partial<IPublicClientApplication> = {}) {
  let active: AccountInfo | null = fakeAccount();

  return fakeMsalInstance({
    handleRedirectPromise: () => Promise.resolve(null),
    getAllAccounts: () => (active ? [active] : []),
    getActiveAccount: () => active,
    setActiveAccount: (account: AccountInfo | null) => {
      active = account;
    },
    acquireTokenSilent: () => Promise.resolve(fakeAuthResult({ account: active ?? fakeAccount() })),
    ...overrides,
  });
}

/** Provides {@link MSAL_INSTANCE} for a `TestBed`. */
export function provideFakeMsal(instance: IPublicClientApplication): Provider {
  return { provide: MSAL_INSTANCE, useValue: instance };
}
