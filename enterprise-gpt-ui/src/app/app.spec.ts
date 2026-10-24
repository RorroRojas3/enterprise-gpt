import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import { Auth } from '@core/auth/auth';
import { SessionStore } from '@core/session/session-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { settle } from '@testing/async';
import { provideFakeMsal, signedInMsal } from '@testing/msal';
import { provideFakeNavigation } from '@testing/navigation';
import { userFixture } from '@testing/session';
import { App } from './app';

const ME_URL = `${TEST_API_BASE_URL}/api/users/me`;

describe('App', () => {
  let backend: HttpTestingController;

  async function render(): Promise<ComponentFixture<App>> {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    return fixture;
  }

  /** Resolves the session the way `sessionGuard` does. */
  async function session(): Promise<void> {
    const loaded = TestBed.inject(SessionStore).ensureLoaded();
    await settle();
    backend.expectOne(ME_URL).flush(userFixture());
    await loaded;
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideFakeNavigation(),
        provideFakeMsal(
          signedInMsal({
            // Never resolves: on the happy path the browser leaves the page, so the
            // interstitial has to hold on its own rather than because a promise settled.
            logoutRedirect: () => new Promise<void>(() => undefined),
          }),
        ),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
  });

  it('creates the root component', async () => {
    expect((await render()).componentInstance).toBeInstanceOf(App);
  });

  it('runs without zone.js loaded', () => {
    expect((globalThis as Record<string, unknown>)['Zone']).toBeUndefined();
  });

  it('latches and empties the session in one synchronous step', async () => {
    // Asserted before any await: a render between the latch and the reset would paint a
    // signed-in shell over a cleared SessionStore.
    await session();
    const auth = TestBed.inject(Auth);

    expect(TestBed.inject(SessionStore).user()).not.toBeNull();

    void auth.signOut();

    expect(auth.isSigningOut()).toBe(true);
    expect(TestBed.inject(SessionStore).user()).toBeNull();
  });

  it('replaces the shell with the interstitial for the whole of sign-out', async () => {
    const fixture = await render();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('router-outlet')).not.toBeNull();

    void TestBed.inject(Auth).signOut();
    await fixture.whenStable();

    expect(host.querySelector('router-outlet')).toBeNull();
    expect(host.querySelector('app-user-footer')).toBeNull();
    expect(host.querySelector('app-signing-out')).not.toBeNull();
  });

  it('renders no signed-in chrome of its own, session or not', async () => {
    // The chrome belongs to `Shell`; rendered here it would cover the chrome-free public routes.
    const fixture = await render();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('app-user-footer')).toBeNull();
    expect(host.querySelector('app-sidebar')).toBeNull();

    await session();
    await fixture.whenStable();

    expect(host.querySelector('app-user-footer')).toBeNull();
    expect(host.querySelector('app-sidebar')).toBeNull();
  });

  it('keeps the toast region mounted throughout, so the first toast is announced', async () => {
    const fixture = await render();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('app-toast-region')).not.toBeNull();

    void TestBed.inject(Auth).signOut();
    await fixture.whenStable();

    expect(host.querySelector('app-toast-region')).not.toBeNull();
  });

  it('moves focus to the interstitial heading the departing user cannot see', async () => {
    // The button that was pressed is removed from the DOM one render later; without
    // this, focus lands on <body> and a screen reader is told nothing at all.
    const fixture = await render();

    void TestBed.inject(Auth).signOut();
    await fixture.whenStable();

    expect(document.activeElement).toBe((fixture.nativeElement as HTMLElement).querySelector('h1'));
  });
});
