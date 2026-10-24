import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { getByRole, queryByRole } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { PREFERENCE_KEYS, writePreference } from '@core/preferences/local-preferences';
import { SessionStore } from '@core/session/session-store';
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
import { Shell } from './shell';
import { UiStore } from './ui-store';

/**
 * Separate from `shell.spec.ts`, whose tests run with no media query set to prove that resolves
 * to desktop. jsdom computes no layout, so this asserts branches, writes and focus, not widths.
 */

const ME_URL = `${TEST_API_BASE_URL}/api/users/me`;
const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;

@Component({
  selector: 'app-blank',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
})
class Blank {}

describe('Shell responsive layout', () => {
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

  function isExpanded(element: HTMLElement): boolean {
    return queryByRole(element, 'searchbox', { name: 'Search conversations' }) !== null;
  }

  function expandButton(element: HTMLElement): HTMLButtonElement | null {
    return queryByRole(element, 'button', { name: 'Expand sidebar' });
  }

  function hamburger(element: HTMLElement): HTMLButtonElement {
    return getByRole(element, 'button', { name: 'Open navigation' });
  }

  function drawerClose(element: HTMLElement): HTMLButtonElement | null {
    return queryByRole(element, 'button', { name: 'Close navigation' });
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
   * Checked at every width: a second sidebar would duplicate its nav id and "Primary" landmark,
   * the trap in `Offcanvas`, whose always-present dialog holds unguarded content at every width.
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
    expect(isExpanded(element)).toBe(true);
    expectAtMostOneSidebar(element);
  });

  describe('768–991.98px', () => {
    it('defaults to the strip whatever the stored preference says', async () => {
      // Through the one sanctioned writer: `check:forbidden` allows a single `setItem` call site.
      writePreference(PREFERENCE_KEYS.sidebar, 'expanded');
      const fixture = await render();
      await atWidth(TABLET_VIEWPORT_QUERY, fixture);
      await loadConversations(fixture);
      const element = host(fixture);

      expect(expandButton(element)).not.toBeNull();
      expect(isExpanded(element)).toBe(false);
      expectAtMostOneSidebar(element);
    });

    it('expands as an overlay with a backdrop, and never writes the preference', async () => {
      const fixture = await render();
      await atWidth(TABLET_VIEWPORT_QUERY, fixture);
      await loadConversations(fixture);
      const element = host(fixture);

      expandButton(element)!.click();
      await fixture.whenStable();

      expect(isExpanded(element)).toBe(true);
      expect(element.querySelector('.shell__backdrop')).not.toBeNull();
      // The rail holds the 60px track the panel vacated, so the main content never reflows.
      expect(element.querySelector('.shell__rail')).not.toBeNull();
      expectAtMostOneSidebar(element);

      // At this width the strip is the default, not the user's choice, so the preference stays.
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

      expandButton(element)!.click();
      await fixture.whenStable();

      // jsdom can only prove `inert` is applied; its behaviour is a browser check.
      expect(main?.hasAttribute('inert')).toBe(true);
    });

    it('collapses on a backdrop press and hands focus back to the strip', async () => {
      const fixture = await render();
      await atWidth(TABLET_VIEWPORT_QUERY, fixture);
      await loadConversations(fixture);
      const element = attach(fixture);

      expandButton(element)!.click();
      await fixture.whenStable();

      // `pointerdown`, not `click`: the panel dismisses on the press.
      element
        .querySelector('.shell__backdrop')!
        .dispatchEvent(new Event('pointerdown', { bubbles: true }));
      await fixture.whenStable();

      expect(isExpanded(element)).toBe(false);
      expect(element.querySelector('.shell__backdrop')).toBeNull();
      expect(document.activeElement).toBe(expandButton(element));
    });
  });

  describe('below 768px (frames 1d and 3e)', () => {
    it('renders the navbar and no sidebar at all until the drawer opens', async () => {
      const fixture = await render();
      await atWidth(NARROW_VIEWPORT, fixture);
      const element = host(fixture);

      expect(element.querySelector('app-mobile-navbar')).not.toBeNull();
      // Not instantiated, rather than hidden inside the always-present `<dialog>`.
      expect(element.querySelector('#sidebar-primary')).toBeNull();
      expectAtMostOneSidebar(element);
    });

    it('opens from the hamburger, focuses the close control, and returns focus to it', async () => {
      const fixture = await render();
      await atWidth(NARROW_VIEWPORT, fixture);
      const element = attach(fixture);

      const menu = hamburger(element);
      menu.focus();
      menu.click();
      await fixture.whenStable();
      await loadConversations(fixture);

      expect(element.querySelector('#sidebar-primary')).not.toBeNull();
      expectAtMostOneSidebar(element);
      expect(document.activeElement).toBe(drawerClose(element));

      drawerClose(element)!.click();
      await fixture.whenStable();

      expect(element.querySelector('#sidebar-primary')).toBeNull();
      // Identity, not `contains`: "somewhere in the navbar" would pass for focus dropped
      // on the bar itself.
      expect(document.activeElement).toBe(menu);
    });

    it('suppresses the sidebar’s own collapse chevron inside the drawer', async () => {
      const fixture = await render();
      await atWidth(NARROW_VIEWPORT, fixture);
      const element = host(fixture);

      hamburger(element).click();
      await fixture.whenStable();
      await loadConversations(fixture);

      // There is no collapsed width to go to here, and the shell's close button already
      // occupies that corner.
      expect(isExpanded(element)).toBe(true);
      expect(queryByRole(element, 'button', { name: 'Collapse sidebar' })).toBeNull();
      expect(drawerClose(element)).not.toBeNull();
    });
  });

  it('renders the phone frame on a cold load, not only after a resize', async () => {
    // Other tests narrow after a desktop render, exercising the `change` listener; a cold load
    // reads `list.matches` at construction instead.
    setMediaQuery(NARROW_VIEWPORT, true);

    const fixture = await render();
    const element = host(fixture);

    expect(element.querySelector('app-mobile-navbar')).not.toBeNull();
    expect(element.querySelector('#sidebar-primary')).toBeNull();
    expectAtMostOneSidebar(element);
  });

  it('dismisses the overlay when the viewport crosses a breakpoint', async () => {
    // Carried across, the overlay would change what it is: a modal dialog at mobile, a panel at
    // tablet, with different focus-return targets.
    const fixture = await render();
    await atWidth(NARROW_VIEWPORT, fixture);
    const element = attach(fixture);

    hamburger(element).click();
    await fixture.whenStable();
    await loadConversations(fixture);
    expect(element.querySelector('#sidebar-primary')).not.toBeNull();

    await atWidth(TABLET_VIEWPORT_QUERY, fixture);

    expect(expandButton(element)).not.toBeNull();
    expect(isExpanded(element)).toBe(false);
    expect(element.querySelector('.shell__backdrop')).toBeNull();
    expectAtMostOneSidebar(element);
  });
});
