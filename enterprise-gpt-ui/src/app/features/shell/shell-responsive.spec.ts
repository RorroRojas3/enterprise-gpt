import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { provideRouter } from '@angular/router';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { SessionStore } from '@core/session/session-store';
import { PREFERENCE_KEYS, writePreference } from '@core/storage/local-preferences';
import { UiStore } from '@core/ui/ui-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { settle } from '@testing/async';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import {
  NARROW_VIEWPORT,
  PREFERS_DARK,
  TABLET_VIEWPORT_QUERY,
  resetMediaQueries,
  setMediaQuery,
} from '@testing/media-query';
import { provideFakeMsal, signedInMsal } from '@testing/msal';
import { provideFakeNavigation } from '@testing/navigation';
import { userFixture } from '@testing/session';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Shell } from './shell';

/**
 * The three viewport modes of frames `3e`, `3f` and `3g` (US-1403).
 *
 * A separate file from `shell.spec.ts` on purpose: that suite is the shell's behaviour
 * at one width, and every one of its tests runs with **no media query configured** —
 * which is the proof that "nothing set" still resolves to desktop. Mixing the two would
 * mean one `beforeEach` deciding a viewport for tests that must not have one.
 *
 * jsdom computes no layout, so nothing here asserts a width, an overlap or a scrollbar.
 * Those are browser checks, and US-1405's harness is where they land. What is assertable
 * — and is where the value is — is which branch renders, what each gesture writes, and
 * where focus goes.
 */

const ME_URL = `${TEST_API_BASE_URL}/api/users/me`;
const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;

@Component({
  selector: 'app-blank',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
})
class Blank {}

describe('Shell responsive layout (US-1403)', () => {
  let backend: HttpTestingController;
  let attached: HTMLElement | null = null;

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    resetMediaQueries();
    setMediaQuery(PREFERS_DARK, false);
    installDialogPolyfill();

    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: '**', component: Blank }]),
        provideLocationMocks(),
        provideFakeMsal(signedInMsal({ logoutRedirect: () => new Promise<void>(() => undefined) })),
        provideFakeNavigation(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // Not at the end of a test: a failing assertion would otherwise leak the fixture
    // host into `document.body` for every spec that follows.
    attached?.remove();
    attached = null;
    backend.verify();
    vi.restoreAllMocks();
  });

  /** Resolves the session the way `sessionGuard` does, then renders the shell. */
  async function render(): Promise<ComponentFixture<Shell>> {
    const session = TestBed.inject(SessionStore);
    const loaded = session.ensureLoaded();
    await settle();
    backend.expectOne(ME_URL).flush(userFixture());
    await loaded;

    const fixture = TestBed.createComponent(Shell);
    await fixture.whenStable();

    return fixture;
  }

  /** Releases the conversation query and answers it, as the bootstrap would. */
  async function loadConversations(fixture: ComponentFixture<Shell>): Promise<void> {
    TestBed.inject(ConversationListStore).ensureLoaded();
    TestBed.tick();
    backend
      .expectOne((request) => request.url === SEARCH_URL)
      .flush(conversationPage([conversationFixture({ name: 'Helios 2.4 release status' })]));
    await fixture.whenStable();
  }

  function host(fixture: ComponentFixture<Shell>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  /** Puts the fixture in the document, which every `activeElement` assertion needs. */
  function attach(fixture: ComponentFixture<Shell>): HTMLElement {
    const element = host(fixture);
    document.body.append(element);
    attached = element;
    return element;
  }

  /** The two queries are mutually exclusive, so a width is one of three states. */
  async function atWidth(query: string | null, fixture: ComponentFixture<Shell>): Promise<void> {
    setMediaQuery(NARROW_VIEWPORT, query === NARROW_VIEWPORT);
    setMediaQuery(TABLET_VIEWPORT_QUERY, query === TABLET_VIEWPORT_QUERY);
    await fixture.whenStable();
  }

  /**
   * The structural invariant, checked at every width.
   *
   * Both of `Sidebar`'s branches emit `<nav id="sidebar-primary" aria-label="Primary">`,
   * so a second instance is a duplicate id at serious impact *and* two navigations with
   * one name. It is the assertion that catches the trap in `Offcanvas`: its `<dialog>`
   * is unconditionally in the DOM, so content projected into it without an `@if` is
   * present at every width, open or not.
   */
  function expectAtMostOneSidebar(element: HTMLElement): void {
    expect(element.querySelectorAll('#sidebar-primary').length).toBeLessThanOrEqual(1);
    expect(element.querySelectorAll('[aria-label="Primary"]').length).toBeLessThanOrEqual(1);
  }

  it('keeps the sidebar in the flow and renders no navbar at desktop', async () => {
    const fixture = await render();
    await loadConversations(fixture);
    const element = host(fixture);

    expect(element.querySelector('app-mobile-navbar')).toBeNull();
    expect(element.querySelector('.shell__backdrop')).toBeNull();
    expect(element.querySelector('nav.sidebar')).not.toBeNull();
    expectAtMostOneSidebar(element);
  });

  describe('768–1023px (frame 3g)', () => {
    it('defaults to the strip whatever the stored preference says', async () => {
      // Through the one sanctioned writer, not a raw `setItem`: `check:forbidden`
      // enforces that there is exactly one call site, and a spec is not a reason to
      // make it two.
      writePreference(PREFERENCE_KEYS.sidebar, 'expanded');
      const fixture = await render();
      await atWidth(TABLET_VIEWPORT_QUERY, fixture);
      await loadConversations(fixture);
      const element = host(fixture);

      expect(element.querySelector('nav.strip')).not.toBeNull();
      expect(element.querySelector('nav.sidebar')).toBeNull();
      expectAtMostOneSidebar(element);
    });

    it('expands as an overlay with a backdrop, and never writes the preference', async () => {
      const fixture = await render();
      await atWidth(TABLET_VIEWPORT_QUERY, fixture);
      await loadConversations(fixture);
      const element = host(fixture);

      element
        .querySelector<HTMLButtonElement>('.strip__button[aria-label="Expand sidebar"]')
        ?.click();
      await fixture.whenStable();

      expect(element.querySelector('nav.sidebar')).not.toBeNull();
      expect(element.querySelector('.shell__backdrop')).not.toBeNull();
      // The rail holds the 60px track the panel vacated, which is what makes "the main
      // content never reflows" structural rather than arithmetic.
      expect(element.querySelector('.shell__rail')).not.toBeNull();
      expectAtMostOneSidebar(element);

      // The whole point of the mode: at this width the strip is the viewport's default
      // rather than the user's choice, so expanding here must not change the width they
      // chose for a desktop.
      expect(localStorage.getItem(PREFERENCE_KEYS.sidebar)).toBeNull();
      expect(TestBed.inject(UiStore).sidebarCollapsed()).toBe(false);
    });

    it('marks the main content inert while the overlay is up', async () => {
      const fixture = await render();
      await atWidth(TABLET_VIEWPORT_QUERY, fixture);
      await loadConversations(fixture);
      const element = host(fixture);
      const main = element.querySelector('#main-content');

      expect(main?.hasAttribute('inert')).toBe(false);

      element
        .querySelector<HTMLButtonElement>('.strip__button[aria-label="Expand sidebar"]')
        ?.click();
      await fixture.whenStable();

      // A backdrop that is only visual lies to the keyboard: without this the content
      // behind it stays reachable by Tab. jsdom cannot prove the *behaviour* of `inert`,
      // only that the attribute is applied — the rest is a browser check.
      expect(main?.hasAttribute('inert')).toBe(true);
    });

    it('collapses on a backdrop press and hands focus back to the strip', async () => {
      const fixture = await render();
      await atWidth(TABLET_VIEWPORT_QUERY, fixture);
      await loadConversations(fixture);
      const element = attach(fixture);

      element
        .querySelector<HTMLButtonElement>('.strip__button[aria-label="Expand sidebar"]')
        ?.click();
      await fixture.whenStable();

      // `pointerdown`, not `click`: the panel is dismissed on the press, as every other
      // dismissal in the app is.
      element
        .querySelector('.shell__backdrop')
        ?.dispatchEvent(new Event('pointerdown', { bubbles: true }));
      await fixture.whenStable();

      expect(element.querySelector('nav.sidebar')).toBeNull();
      expect(element.querySelector('.shell__backdrop')).toBeNull();
      // Not the document body, where the next Tab restarts at the top of the page.
      expect(document.activeElement).toBe(
        element.querySelector('.strip__button[aria-label="Expand sidebar"]'),
      );
    });
  });

  describe('below 768px (frames 1d and 3e)', () => {
    it('renders the navbar and no sidebar at all until the drawer opens', async () => {
      const fixture = await render();
      await atWidth(NARROW_VIEWPORT, fixture);
      const element = host(fixture);

      expect(element.querySelector('app-mobile-navbar')).not.toBeNull();
      // Not merely hidden: the sidebar is not instantiated, so no second
      // `#sidebar-primary` is sitting inside the always-present `<dialog>`.
      expect(element.querySelector('#sidebar-primary')).toBeNull();
      expectAtMostOneSidebar(element);
    });

    it('opens from the hamburger, focuses the close control, and returns focus to it', async () => {
      const fixture = await render();
      await atWidth(NARROW_VIEWPORT, fixture);
      const element = attach(fixture);

      const hamburger = element.querySelector<HTMLButtonElement>('app-mobile-navbar .navbar__menu');
      hamburger?.focus();
      hamburger?.click();
      await fixture.whenStable();
      await loadConversations(fixture);

      expect(element.querySelector('#sidebar-primary')).not.toBeNull();
      expectAtMostOneSidebar(element);
      // `data-modal-autofocus` puts the opening focus on the close control rather than
      // on the first conversation row.
      expect(document.activeElement).toBe(element.querySelector('.shell__drawer-close'));

      element.querySelector<HTMLButtonElement>('.shell__drawer-close')?.click();
      await fixture.whenStable();

      expect(element.querySelector('#sidebar-primary')).toBeNull();
      // Identity, not `contains`: "somewhere in the navbar" would pass for focus dropped
      // on the bar itself.
      expect(document.activeElement).toBe(hamburger);
    });

    it('suppresses the sidebar’s own collapse chevron inside the drawer', async () => {
      const fixture = await render();
      await atWidth(NARROW_VIEWPORT, fixture);
      const element = host(fixture);

      element.querySelector<HTMLButtonElement>('app-mobile-navbar .navbar__menu')?.click();
      await fixture.whenStable();
      await loadConversations(fixture);

      // There is no collapsed width to go to here, and the shell's close button already
      // occupies that corner (frame 3e).
      expect(element.querySelector('.sidebar__collapse')).toBeNull();
      expect(element.querySelector('.shell__drawer-close')).not.toBeNull();
    });
  });

  it('renders the phone frame on a cold load, not only after a resize', async () => {
    // Every other test here renders at desktop and then narrows, which exercises the
    // `change` listener. A phone opening the app cold takes the other path entirely:
    // `injectMediaQuery` reads `list.matches` at construction, and `MobileNavbar` exists
    // on the *first* render rather than being created later.
    setMediaQuery(NARROW_VIEWPORT, true);

    const fixture = await render();
    const element = host(fixture);

    expect(element.querySelector('app-mobile-navbar')).not.toBeNull();
    expect(element.querySelector('#sidebar-primary')).toBeNull();
    expectAtMostOneSidebar(element);
  });

  it('dismisses the overlay when the viewport crosses a breakpoint', async () => {
    // The overlay is a gesture on one viewport. Carried across it would also change what
    // it *is*: a modal dialog at mobile, a panel in the shell's own box at tablet, with
    // two different focus-return targets.
    const fixture = await render();
    await atWidth(NARROW_VIEWPORT, fixture);
    const element = attach(fixture);

    element.querySelector<HTMLButtonElement>('app-mobile-navbar .navbar__menu')?.click();
    await fixture.whenStable();
    await loadConversations(fixture);
    expect(element.querySelector('#sidebar-primary')).not.toBeNull();

    await atWidth(TABLET_VIEWPORT_QUERY, fixture);

    expect(element.querySelector('nav.strip')).not.toBeNull();
    expect(element.querySelector('nav.sidebar')).toBeNull();
    expect(element.querySelector('.shell__backdrop')).toBeNull();
    expectAtMostOneSidebar(element);
  });
});
