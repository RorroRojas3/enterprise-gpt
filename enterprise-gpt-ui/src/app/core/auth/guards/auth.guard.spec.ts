import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { provideRouter } from '@angular/router';
import { IPublicClientApplication, RedirectRequest } from '@azure/msal-browser';
import { provideTestAppConfig } from '@testing/app-config';
import { fakeAccount, provideFakeMsal, signedInMsal, signedOutMsal } from '@testing/msal';
import { provideFakeNavigation } from '@testing/navigation';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthService } from '../auth-service';
import { authGuard } from './auth.guard';

describe('authGuard', () => {
  function run(
    instance: IPublicClientApplication,
    url = '/chat',
    before?: (auth: AuthService) => void,
  ) {
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideFakeMsal(instance),
        provideRouter([]),
        provideFakeNavigation(),
      ],
    });

    before?.(TestBed.inject(AuthService));

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

  it('refuses to sign a departing user back in mid-sign-out', async () => {
    // The loop this closes: a navigation during teardown finds the handshake memo
    // already cleared, re-runs handleRedirectPromise — releasing the SIGNOUT lock the
    // logout still needs — finds no account, and starts a sign-in that sends the browser
    // to the authorize endpoint, cancelling the sign-out outright.
    const loginRedirect = vi.fn((_request: RedirectRequest) => Promise.resolve());
    const handleRedirectPromise = vi.fn(() => Promise.resolve(null));
    const guarded = run(
      signedOutMsal({
        loginRedirect,
        handleRedirectPromise,
        logoutRedirect: () => new Promise<void>(() => undefined),
      }),
      '/chat',
      (auth) => void auth.signOut(),
    );

    // Bare false, not a UrlTree: the document is leaving either way, and a UrlTree is
    // one more navigation re-running these same guards.
    await expect(guarded).resolves.toBe(false);
    expect(loginRedirect).not.toHaveBeenCalled();
    expect(handleRedirectPromise).not.toHaveBeenCalled();
  });
});
