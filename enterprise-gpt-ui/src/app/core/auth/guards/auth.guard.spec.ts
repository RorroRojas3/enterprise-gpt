import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { provideRouter } from '@angular/router';
import { IPublicClientApplication, RedirectRequest } from '@azure/msal-browser';
import { provideTestAppConfig } from '@testing/app-config';
import { fakeAccount, provideFakeMsal, signedInMsal, signedOutMsal } from '@testing/msal';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { authGuard } from './auth.guard';

describe('authGuard', () => {
  function run(instance: IPublicClientApplication, url = '/chat') {
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideFakeMsal(instance), provideRouter([])],
    });

    const state = { url } as RouterStateSnapshot;

    return TestBed.runInInjectionContext(() =>
      authGuard({} as ActivatedRouteSnapshot, state),
    ) as Promise<boolean | UrlTree>;
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    sessionStorage.clear();
  });

  it('activates for a signed-in account', async () => {
    await expect(run(signedInMsal())).resolves.toBe(true);
  });

  it('starts the Entra redirect when nobody is signed in', async () => {
    const loginRedirect = vi.fn((_request: RedirectRequest) => Promise.resolve());

    await run(signedOutMsal({ loginRedirect }));

    expect(loginRedirect).toHaveBeenCalledTimes(1);
  });

  it('preserves the requested URL, query string included, across the redirect', async () => {
    const loginRedirect = vi.fn((_request: RedirectRequest) => Promise.resolve());

    await run(signedOutMsal({ loginRedirect }), '/chat/abc?tab=files');

    expect(sessionStorage.getItem('egpt.returnUrl')).toBe('/chat/abc?tab=files');
  });

  it('routes a failed redirect to the sign-in failure page rather than looping', async () => {
    const result = await run(
      signedOutMsal({ handleRedirectPromise: () => Promise.reject(new Error('state mismatch')) }),
    );

    expect(result).toBeInstanceOf(UrlTree);
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe('/login-failed');
  });

  it('routes to the failure page when the redirect cannot even be started', async () => {
    // MSAL takes its interaction lock and discovers the authority before navigating,
    // so this rejects on an unreachable Entra or a lock left by an abandoned attempt.
    // Unhandled, the guard's promise rejects, no route activates, and the startup
    // shell is torn down over nothing — a genuinely blank page.
    const result = await run(
      signedOutMsal({ loginRedirect: () => Promise.reject(new Error('interaction_in_progress')) }),
    );

    expect(result).toBeInstanceOf(UrlTree);
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe('/login-failed');
  });

  it('adopts the account the redirect returned without a second handshake', async () => {
    const account = fakeAccount();
    const handleRedirectPromise = vi.fn(() => Promise.resolve(null));

    await expect(
      run(signedInMsal({ handleRedirectPromise, getAllAccounts: () => [account] })),
    ).resolves.toBe(true);
    expect(handleRedirectPromise).toHaveBeenCalledTimes(1);
  });
});
