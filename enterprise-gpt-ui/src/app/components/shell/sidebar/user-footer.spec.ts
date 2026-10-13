import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { getByRole, getByText } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ALLOWED_PREFERENCE_KEYS, PREFERENCE_KEYS } from '@shared/utils/storage/local-preferences';
import { AuthService } from '@core/auth/auth-service';
import { SessionStore } from '@core/session/session-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { settle } from '@testing/async';
import { PREFERS_DARK, resetMediaQueries, setMediaQuery } from '@testing/media-query';
import { provideFakeMsal, signedInMsal } from '@testing/msal';
import { provideFakeNavigation } from '@testing/navigation';
import { userFixture } from '@testing/session';
import { UserFooter } from './user-footer';

const ME_URL = `${TEST_API_BASE_URL}/api/users/me`;
const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;
const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;

describe('UserFooter', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    resetMediaQueries();
    setMediaQuery(PREFERS_DARK, false);

    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideFakeMsal(signedInMsal({ logoutRedirect: () => new Promise<void>(() => undefined) })),
        provideFakeNavigation(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    document.documentElement.removeAttribute('data-bs-theme');
    document.documentElement.style.removeProperty('color-scheme');
    vi.restoreAllMocks();
  });

  /** Loads a session the way `sessionGuard` does, then renders the footer against it. */
  async function render(fullName = 'Ada Lovelace'): Promise<ComponentFixture<UserFooter>> {
    const session = TestBed.inject(SessionStore);
    const loaded = session.ensureLoaded();
    await settle();
    backend.expectOne(ME_URL).flush(userFixture({ fullName }));
    await loaded;

    const fixture = TestBed.createComponent(UserFooter);
    await fixture.whenStable();

    return fixture;
  }

  function host(fixture: ComponentFixture<UserFooter>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function signOutButton(fixture: ComponentFixture<UserFooter>): HTMLButtonElement {
    return getByRole(host(fixture), 'button', { name: 'Sign out' });
  }

  it('names the signed-in user and abbreviates them to two initials', async () => {
    const element = host(await render());

    expect(element.textContent).toContain('Ada Lovelace');
    expect(getByText(element, 'AL')).toBeTruthy();
  });

  it('falls back to one initial for a single-word name', async () => {
    const element = host(await render('Prince'));

    expect(getByText(element, 'P')).toBeTruthy();
  });

  it('hides the avatar from assistive technology, which already hears the name', async () => {
    const element = host(await render());

    expect(getByText(element, 'AL').getAttribute('aria-hidden')).toBe('true');
  });

  it('signs out on one press, and refuses a second', async () => {
    const fixture = await render();
    const auth = TestBed.inject(AuthService);
    const signOut = vi.spyOn(auth, 'signOut');
    const button = signOutButton(fixture);

    button.click();
    await fixture.whenStable();

    expect(auth.isSigningOut()).toBe(true);
    // Latched in the DOM too, so there is nothing to press a second time.
    expect(button.disabled).toBe(true);

    button.click();

    expect(signOut).toHaveBeenCalledOnce();
  });

  it('clears the session but keeps the browser preferences', async () => {
    // The theme is written first so the assertion proves survival rather than absence.
    const fixture = await render();
    getByRole(host(fixture), 'button', { name: /Switch to (dark|light) theme/ }).click();
    await fixture.whenStable();

    expect(localStorage.getItem(PREFERENCE_KEYS.theme)).toBe('dark');

    signOutButton(fixture).click();
    await fixture.whenStable();

    expect(localStorage.getItem(PREFERENCE_KEYS.theme)).toBe('dark');
    expect(TestBed.inject(SessionStore).user()).toBeNull();
  });

  it('does not sweep localStorage — the build gate is what keeps user data out of it', async () => {
    // Sign-out clears nothing here: user data stays out because `check-forbidden-apis.mjs`
    // lets only shared/utils/storage/local-preferences.ts write, so a stray key survives.
    const fixture = await render();
    localStorage.setItem('egpt.leftover', 'what a regression looks like');

    signOutButton(fixture).click();
    await fixture.whenStable();

    expect(localStorage.getItem('egpt.leftover')).toBe('what a regression looks like');
    expect(ALLOWED_PREFERENCE_KEYS).not.toContain('egpt.leftover');
  });

  it('does not request the catalogs on render — the bootstrap owns that', async () => {
    await render();

    backend.expectNone(MODELS_URL);
    backend.expectNone(MCPS_URL);
  });
});
