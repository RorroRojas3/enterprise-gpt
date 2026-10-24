import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { IPublicClientApplication, RedirectRequest } from '@azure/msal-browser';
import {
  getAllByRole,
  getByRole,
  getByText,
  queryAllByRole,
  queryByRole,
} from '@testing-library/dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { HardNavigation } from '@core/navigation/hard-navigation';
import { SessionStore } from '@core/session/session-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { settle } from '@testing/async';
import {
  fakeAccount,
  fakeAuthResult,
  provideFakeMsal,
  signedInMsal,
  signedOutMsal,
} from '@testing/msal';
import { FakeNavigation, provideFakeNavigation } from '@testing/navigation';
import { iconOf } from '@testing/overlays';
import { PROBLEM_FIXTURES, TRACE_ID } from '@testing/problem-fixtures';
import { userFixture } from '@testing/session';
import { AuthCallback } from './auth-callback';
import { Forbidden } from './forbidden';
import { LoginFailed } from './login-failed';
import { SessionError } from './session-error';
import { SignedOut } from './signed-out';

const ME_URL = `${TEST_API_BASE_URL}/api/users/me`;

function configure(instance: IPublicClientApplication = signedInMsal()): void {
  TestBed.configureTestingModule({
    providers: [
      provideTestAppConfig(),
      provideFakeMsal(instance),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      provideFakeNavigation(),
    ],
  });
}

describe('AuthCallback', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    sessionStorage.clear();
  });

  it('shows the interstitial while the redirect is still resolving', async () => {
    // The copy must be on screen before the handshake resolves, not after.
    configure(signedOutMsal({ handleRedirectPromise: () => new Promise(() => undefined) }));
    const fixture = TestBed.createComponent(AuthCallback);
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    expect(getByRole(host, 'heading', { level: 1, name: 'Signing you in…' })).toBeTruthy();
    expect(getByText(host, 'Finishing up with Microsoft Entra ID')).toBeTruthy();
  });

  it('returns the user to the route they originally asked for', async () => {
    const account = fakeAccount();
    configure(
      signedOutMsal({ handleRedirectPromise: () => Promise.resolve(fakeAuthResult({ account })) }),
    );
    sessionStorage.setItem('egpt.returnUrl', '/chat/abc');
    const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    const fixture = TestBed.createComponent(AuthCallback);
    await fixture.whenStable();
    await settle();

    expect(navigateByUrl).toHaveBeenCalledWith('/chat/abc', { replaceUrl: true });
  });

  it('routes a failed redirect to the sign-in failure page', async () => {
    configure(
      signedOutMsal({ handleRedirectPromise: () => Promise.reject(new Error('state mismatch')) }),
    );
    const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    const fixture = TestBed.createComponent(AuthCallback);
    await fixture.whenStable();
    await settle();

    expect(navigateByUrl).toHaveBeenCalledWith('/login-failed', { replaceUrl: true });
  });
});

describe('LoginFailed', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    sessionStorage.clear();
  });

  it('offers one action and no password field', async () => {
    // No screen in this app has a password field; a sign-in failure is the one tempted to.
    configure(signedOutMsal({ loginRedirect: () => Promise.resolve() }));
    const fixture = TestBed.createComponent(LoginFailed);
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    expect(getByRole(host, 'heading', { level: 1, name: 'Sign-in didn’t complete' })).toBeTruthy();
    // Every input, not the textbox role: a password field has no role at all.
    expect(host.querySelectorAll('input')).toHaveLength(0);
    expect(getAllByRole(host, 'button')).toHaveLength(1);
  });

  it('resolves the pending handshake on arrival, so Try again is not blocked by a stale lock', async () => {
    const handleRedirectPromise = vi.fn(() => Promise.resolve(null));
    configure(signedOutMsal({ handleRedirectPromise, loginRedirect: () => Promise.resolve() }));

    const fixture = TestBed.createComponent(LoginFailed);
    await fixture.whenStable();

    expect(handleRedirectPromise).toHaveBeenCalledTimes(1);
  });

  it('reports a retry that could not reach Entra instead of latching disabled', async () => {
    configure(
      signedOutMsal({ loginRedirect: () => Promise.reject(new Error('interaction_in_progress')) }),
    );
    const fixture = TestBed.createComponent(LoginFailed);
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    const button = getByRole<HTMLButtonElement>(host, 'button', { name: 'Try again' });
    button.click();
    await settle();
    await fixture.whenStable();

    expect(button.disabled).toBe(false);
    expect(getByRole(host, 'alert').textContent).toContain('couldn’t reach Microsoft Entra ID');
    expect(getByRole(host, 'button', { name: 'Reload and try again' })).toBe(button);
  });

  it('re-enters the Entra flow once, however often it is pressed', async () => {
    const loginRedirect = vi.fn((_request: RedirectRequest) => Promise.resolve());
    configure(signedOutMsal({ loginRedirect }));
    const fixture = TestBed.createComponent(LoginFailed);
    await fixture.whenStable();

    const button = getByRole(fixture.nativeElement as HTMLElement, 'button', { name: 'Try again' });
    button.click();
    await fixture.whenStable();
    button.click();
    await fixture.whenStable();

    expect(loginRedirect).toHaveBeenCalledTimes(1);
  });
});

describe('SessionError', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  async function failSession(): Promise<void> {
    const store = TestBed.inject(SessionStore);
    const backend = TestBed.inject(HttpTestingController);
    const loaded = store.ensureLoaded();
    await settle();
    backend
      .expectOne(ME_URL)
      .flush(PROBLEM_FIXTURES.resourceNotFound, { status: 404, statusText: 'Not Found' });
    await loaded;
  }

  it('names the failure and its trace id, and offers a retry', async () => {
    configure();
    await failSession();

    const fixture = TestBed.createComponent(SessionError);
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    // The panel owns the heading, so the page's outline comes from role/aria-level.
    const heading = getByRole(host, 'heading', {
      level: 1,
      name: 'We couldn’t start Enterprise GPT',
    });
    expect(document.activeElement).toBe(heading);
    // role="alert" implies aria-atomic, so leaving it on would read the whole panel
    // and then the focused heading again.
    expect(queryByRole(host, 'alert')).toBeNull();
    expect(host.textContent).toContain(TRACE_ID);
    expect(getByRole(host, 'button', { name: 'Retry' })).toBeTruthy();
  });

  it('re-issues the bootstrap and leaves once the session recovers', async () => {
    configure();
    await failSession();
    const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    const fixture = TestBed.createComponent(SessionError);
    await fixture.whenStable();
    getByRole(fixture.nativeElement as HTMLElement, 'button', { name: 'Retry' }).click();
    await settle();

    TestBed.inject(HttpTestingController).expectOne(ME_URL).flush(userFixture());
    await settle();

    expect(navigateByUrl).toHaveBeenCalledWith('/chat', { replaceUrl: true });
  });

  it('has nothing to say when there is no failure, so it leaves', async () => {
    configure();
    const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    const fixture = TestBed.createComponent(SessionError);
    await fixture.whenStable();

    expect(navigateByUrl).toHaveBeenCalledWith('/chat', { replaceUrl: true });
    expect(queryByRole(fixture.nativeElement as HTMLElement, 'button')).toBeNull();
  });
});

describe('auth page focus', () => {
  // `#heading`, `tabindex="-1"` and `focusOnRender` are coupled across four components, and the
  // router moves no focus, so dropping any one silently strands keyboard users.
  function heading(host: HTMLElement): HTMLElement {
    return getByRole(host, 'heading', { level: 1 });
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    sessionStorage.clear();
  });

  it('moves focus to the interstitial heading', async () => {
    configure(signedOutMsal({ handleRedirectPromise: () => new Promise(() => undefined) }));
    const fixture = TestBed.createComponent(AuthCallback);
    await fixture.whenStable();

    expect(document.activeElement).toBe(heading(fixture.nativeElement as HTMLElement));
  });

  it('moves focus to the sign-in failure heading', async () => {
    configure(signedOutMsal());
    const fixture = TestBed.createComponent(LoginFailed);
    await fixture.whenStable();

    expect(document.activeElement).toBe(heading(fixture.nativeElement as HTMLElement));
  });

  it('moves focus to the forbidden heading', async () => {
    configure();
    const fixture = TestBed.createComponent(Forbidden);
    await fixture.whenStable();

    expect(document.activeElement).toBe(heading(fixture.nativeElement as HTMLElement));
  });

  it('moves focus to the signed-out heading', async () => {
    configure(signedOutMsal());
    const fixture = TestBed.createComponent(SignedOut);
    await fixture.whenStable();

    expect(document.activeElement).toBe(heading(fixture.nativeElement as HTMLElement));
  });
});

describe('SignedOut', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    sessionStorage.clear();
  });

  it('states what was cleared and what was kept, and offers no automatic return', async () => {
    configure(signedOutMsal());
    const fixture = TestBed.createComponent(SignedOut);
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    expect(getByRole(host, 'heading', { level: 1, name: 'You’re signed out' })).toBeTruthy();
    expect(host.textContent).toContain('cleared from this browser');
    // No routerLink: after a failed sign-out any guarded route re-authenticates the departing user.
    expect(queryAllByRole(host, 'link')).toHaveLength(0);
    expect(getAllByRole(host, 'button')).toHaveLength(1);
  });

  it('starts a fresh sign-in only when the user asks, and latches', async () => {
    const loginRedirect = vi.fn((_request: RedirectRequest) => Promise.resolve());
    configure(signedOutMsal({ loginRedirect }));
    const fixture = TestBed.createComponent(SignedOut);
    await fixture.whenStable();

    const button = getByRole<HTMLButtonElement>(fixture.nativeElement as HTMLElement, 'button', {
      name: 'Sign in again',
    });
    button.click();
    await fixture.whenStable();

    expect(loginRedirect).toHaveBeenCalledOnce();
    expect(button.disabled).toBe(true);
  });

  it('reloads when the redirect cannot even start, because the lock outlives this page', async () => {
    configure(signedOutMsal({ loginRedirect: () => Promise.reject(new Error('in progress')) }));
    const fixture = TestBed.createComponent(SignedOut);
    await fixture.whenStable();

    getByRole(fixture.nativeElement as HTMLElement, 'button', { name: 'Sign in again' }).click();
    await fixture.whenStable();

    expect(
      (TestBed.inject(HardNavigation) as unknown as FakeNavigation).reload,
    ).toHaveBeenCalledOnce();
  });
});

describe('Forbidden', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('names the permission and offers exactly one way back', async () => {
    configure();
    const fixture = TestBed.createComponent(Forbidden);
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    expect(
      getByRole(host, 'heading', { level: 1, name: 'You don’t have access to Admin' }),
    ).toBeTruthy();
    expect(host.textContent).toContain('Administrator permission');
    expect(iconOf(host)).toBe('bi-shield-lock');
    expect(getAllByRole(host, 'link')).toHaveLength(1);
    expect(getByRole(host, 'link', { name: 'Back to Chat' }).getAttribute('href')).toBe('/chat');
  });
});
