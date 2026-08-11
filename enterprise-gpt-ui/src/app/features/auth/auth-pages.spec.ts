import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { IPublicClientApplication, RedirectRequest } from '@azure/msal-browser';
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
import { PROBLEM_FIXTURES, TRACE_ID } from '@testing/problem-fixtures';
import { userFixture } from '@testing/session';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthCallback } from './auth-callback/auth-callback';
import { Forbidden } from './forbidden/forbidden';
import { LoginFailed } from './login-failed/login-failed';
import { SessionError } from './session-error/session-error';

const ME_URL = `${TEST_API_BASE_URL}/api/users/me`;

function configure(instance: IPublicClientApplication = signedInMsal()): void {
  TestBed.configureTestingModule({
    providers: [
      provideTestAppConfig(),
      provideFakeMsal(instance),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  });
}

describe('AuthCallback', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    sessionStorage.clear();
  });

  it('shows the interstitial while the redirect is still resolving', async () => {
    // "never a blank page and never an empty app shell" is the criterion, and the
    // handshake is what it has to survive — so the copy must be on screen before it
    // resolves, not after.
    configure(signedOutMsal({ handleRedirectPromise: () => new Promise(() => undefined) }));
    const fixture = TestBed.createComponent(AuthCallback);
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    // Focused rather than announced: a live region inserted with its own text
    // announces nothing, so the heading is what carries this to a screen reader.
    expect(host.querySelector('h1')?.textContent).toContain('Signing you in…');
    const text = host.textContent ?? '';
    expect(text).toContain('Finishing up with Microsoft Entra ID');
    expect((fixture.nativeElement as HTMLElement).querySelector('.kit-spinner')).not.toBeNull();
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

    // replaceUrl keeps `/auth#code=…` out of history, so the back button cannot
    // replay a consumed authorization code.
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
    // The board states it outright: there is no password field anywhere in this
    // application, and a sign-in failure is the one screen tempted to grow one.
    configure(signedOutMsal({ loginRedirect: () => Promise.resolve() }));
    const fixture = TestBed.createComponent(LoginFailed);
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('h1')?.textContent).toContain('Sign-in didn’t complete');
    expect(host.querySelectorAll('input')).toHaveLength(0);
    expect(host.querySelectorAll('button')).toHaveLength(1);
  });

  it('resolves the pending handshake on arrival, so Try again is not blocked by a stale lock', async () => {
    // This route is unguarded, so it is the one screen no other code path has cleared
    // MSAL's interaction_in_progress lock for. Without this, a user who abandoned a
    // sign-in finds Try again throwing until the tab is closed.
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

    const button = (fixture.nativeElement as HTMLElement).querySelector('button');
    button?.click();
    await settle();
    await fixture.whenStable();

    expect(button?.disabled).toBe(false);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      'couldn’t reach Microsoft Entra ID',
    );
  });

  it('re-enters the Entra flow once, however often it is pressed', async () => {
    const loginRedirect = vi.fn((_request: RedirectRequest) => Promise.resolve());
    configure(signedOutMsal({ loginRedirect }));
    const fixture = TestBed.createComponent(LoginFailed);
    await fixture.whenStable();

    const button = (fixture.nativeElement as HTMLElement).querySelector('button');
    button?.click();
    await fixture.whenStable();
    button?.click();
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
    // The panel owns the heading, so 6c gets its document outline from role/aria-level.
    const heading = host.querySelector('[role="heading"]');
    expect(heading?.getAttribute('aria-level')).toBe('1');
    expect(document.activeElement).toBe(heading);
    // role="alert" implies aria-atomic, so leaving it on would read the whole panel
    // and then the focused heading again.
    expect(host.querySelector('[role="alert"]')).toBeNull();
    expect(heading?.textContent).toContain('We couldn’t start Enterprise GPT');
    const text = host.textContent ?? '';
    expect(text).toContain(TRACE_ID);
    expect((fixture.nativeElement as HTMLElement).querySelector('button')?.textContent).toContain(
      'Retry',
    );
  });

  it('re-issues the bootstrap and leaves once the session recovers', async () => {
    configure();
    await failSession();
    const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    const fixture = TestBed.createComponent(SessionError);
    await fixture.whenStable();
    (fixture.nativeElement as HTMLElement).querySelector('button')?.click();
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
    expect((fixture.nativeElement as HTMLElement).querySelector('button')).toBeNull();
  });
});

describe('auth page focus', () => {
  // `#heading` + `tabindex="-1"` + `focusOnRender` are three coupled pieces across four
  // components; removing any one silently reverts the fix and nothing else notices.
  // Every one of these pages is reached by a router navigation or a guard redirect, and
  // Angular moves focus for neither — on /forbidden the element that *had* focus is
  // removed from the DOM outright.
  beforeEach(() => {
    TestBed.resetTestingModule();
    sessionStorage.clear();
  });

  it('moves focus to the interstitial heading', async () => {
    configure(signedOutMsal({ handleRedirectPromise: () => new Promise(() => undefined) }));
    const fixture = TestBed.createComponent(AuthCallback);
    await fixture.whenStable();

    expect(document.activeElement).toBe((fixture.nativeElement as HTMLElement).querySelector('h1'));
  });

  it('moves focus to the sign-in failure heading', async () => {
    configure(signedOutMsal());
    const fixture = TestBed.createComponent(LoginFailed);
    await fixture.whenStable();

    expect(document.activeElement).toBe((fixture.nativeElement as HTMLElement).querySelector('h1'));
  });

  it('moves focus to the forbidden heading', async () => {
    configure();
    const fixture = TestBed.createComponent(Forbidden);
    await fixture.whenStable();

    expect(document.activeElement).toBe((fixture.nativeElement as HTMLElement).querySelector('h1'));
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
    expect(host.querySelector('h1')?.textContent).toContain('You don’t have access to Admin');
    expect(host.textContent).toContain('Administrator permission');
    expect(host.querySelector('use')?.getAttribute('href')).toBe('#bi-shield-lock');
    expect(host.querySelectorAll('a')).toHaveLength(1);
    expect(host.querySelector('a')?.getAttribute('href')).toBe('/chat');
  });
});
