import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { Router, provideRouter } from '@angular/router';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { SessionStore } from '@core/session/session-store';
import { PREFERENCE_KEYS } from '@core/storage/local-preferences';
import { UiStore } from '@core/ui/ui-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { settle } from '@testing/async';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { PREFERS_DARK, resetMediaQueries, setMediaQuery } from '@testing/media-query';
import { provideFakeMsal, signedInMsal } from '@testing/msal';
import { provideFakeNavigation } from '@testing/navigation';
import { administratorFixture, userFixture } from '@testing/session';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Shell } from './shell';

const ME_URL = `${TEST_API_BASE_URL}/api/users/me`;
const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;

/** `SearchInput`'s default, which the sidebar does not override. */
const SEARCH_DEBOUNCE_MS = 300;

@Component({
  selector: 'app-blank',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
})
class Blank {}

describe('Shell', () => {
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
        provideRouter([{ path: '**', component: Blank }]),
        provideLocationMocks(),
        provideFakeMsal(signedInMsal({ logoutRedirect: () => new Promise<void>(() => undefined) })),
        provideFakeNavigation(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // Without this an unexpected or unflushed request passes silently, including the
    // "creates nothing" guarantee outside the one test that verifies inline.
    backend.verify();
    document.documentElement.removeAttribute('data-bs-theme');
    document.documentElement.style.removeProperty('color-scheme');
    vi.restoreAllMocks();
  });

  function expectSearch(): TestRequest {
    return backend.expectOne((request) => request.url === SEARCH_URL);
  }

  /** Resolves the session the way `sessionGuard` does, then renders the shell. */
  async function render(admin = false): Promise<ComponentFixture<Shell>> {
    const session = TestBed.inject(SessionStore);
    const loaded = session.ensureLoaded();
    await settle();
    backend.expectOne(ME_URL).flush(admin ? administratorFixture() : userFixture());
    await loaded;

    const fixture = TestBed.createComponent(Shell);
    await fixture.whenStable();

    return fixture;
  }

  /** Releases the conversation query and answers it, as the bootstrap would. */
  async function loadConversations(
    fixture: ComponentFixture<Shell>,
    items = [conversationFixture({ name: 'Helios 2.4 release status' })],
    totalCount?: number,
  ): Promise<void> {
    TestBed.inject(ConversationListStore).ensureLoaded();
    TestBed.tick();
    expectSearch().flush(conversationPage(items, { totalCount: totalCount ?? items.length }));
    await fixture.whenStable();
  }

  function host(fixture: ComponentFixture<Shell>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  it('puts a skip link ahead of everything else in the tab order', async () => {
    const fixture = await render();
    const element = host(fixture);

    expect(element.querySelector('a.skip-link')?.getAttribute('href')).toBe('#main-content');
    expect(element.querySelector('#main-content')?.getAttribute('tabindex')).toBe('-1');

    element.querySelector<HTMLAnchorElement>('a.skip-link')?.click();
    await fixture.whenStable();

    // Focus really moves, and the fragment never reaches the router — a bare href
    // would navigate to /chat#main-content and re-run the guards.
    expect(document.activeElement).toBe(element.querySelector('#main-content'));
    expect(TestBed.inject(Router).url).not.toContain('#');
  });

  describe('conversation list', () => {
    it('shows a skeleton for the first page rather than an empty state', async () => {
      const fixture = await render();
      TestBed.inject(ConversationListStore).ensureLoaded();
      await fixture.whenStable();
      const element = host(fixture);

      expect(element.querySelectorAll('app-skeleton').length).toBeGreaterThan(0);
      expect(element.querySelector('app-empty-state')).toBeNull();
      expect(element.querySelector('.sidebar__list')?.getAttribute('aria-busy')).toBe('true');

      expectSearch().flush(conversationPage([]));
    });

    it('renders the rows the server returned, in its order', async () => {
      const fixture = await render();
      await loadConversations(fixture, [
        conversationFixture({ name: 'Newest' }),
        conversationFixture({ name: 'Older' }),
      ]);

      const titles = [...host(fixture).querySelectorAll('.row__title')].map((n) => n.textContent);
      expect(titles).toEqual(['Newest', 'Older']);
    });

    it('marks the open conversation as the current page', async () => {
      const open = conversationFixture({ name: 'Open one' });
      const other = conversationFixture({ name: 'Another' });
      const fixture = await render();
      await loadConversations(fixture, [open, other]);

      await TestBed.inject(Router).navigateByUrl(`/chat/${open.id}`);
      await fixture.whenStable();

      // Since US-304 the row is a wrapper around the link and its kebab: the wrapper
      // paints the selected surface, while `aria-current` stays on the link element.
      const rows = [...host(fixture).querySelectorAll<HTMLElement>('.row')];
      const links = [...host(fixture).querySelectorAll<HTMLAnchorElement>('a.row__link')];
      expect(links[0]?.getAttribute('aria-current')).toBe('page');
      expect(rows[0]?.classList.contains('row--selected')).toBe(true);
      // Exact matching, or every row would light up under /chat.
      expect(links[1]?.getAttribute('aria-current')).toBeNull();
      expect(rows[1]?.classList.contains('row--selected')).toBe(false);

      // And only the link claims `page`: the Chat nav link stays active under
      // /chat/{id} by prefix match, so it announces `true` instead of competing.
      const current = [...host(fixture).querySelectorAll('[aria-current="page"]')];
      expect(current).toHaveLength(1);
      expect(current[0]).toBe(links[0]);
    });

    it('scrolls the list on its own rather than the whole sidebar', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      const list = host(fixture).querySelector('.sidebar__list');
      expect(list).not.toBeNull();
      // The rows and the footer are siblings under the sidebar, so a scrolling list
      // cannot take the brand or the user footer with it.
      expect(list?.querySelector('app-user-footer')).toBeNull();
    });

    it('offers a designed empty state, distinct from the skeleton', async () => {
      const fixture = await render();
      await loadConversations(fixture, []);
      const element = host(fixture);

      expect(element.querySelector('app-skeleton')).toBeNull();
      expect(element.querySelector('app-empty-state')?.textContent).toContain(
        'No conversations yet',
      );
    });

    it('says a search matched nothing differently from having nothing', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      TestBed.inject(ConversationListStore).search('nothing matches');
      TestBed.tick();
      expectSearch().flush(conversationPage([]));
      await fixture.whenStable();

      expect(host(fixture).querySelector('app-empty-state')?.textContent).toContain(
        'No matching conversations',
      );
    });

    it('reports a failed load with a retry rather than as an empty list', async () => {
      const fixture = await render();
      TestBed.inject(ConversationListStore).ensureLoaded();
      TestBed.tick();
      expectSearch().flush(PROBLEM_FIXTURES.forbidden, { status: 403, statusText: 'Forbidden' });
      await fixture.whenStable();
      const element = host(fixture);

      expect(element.querySelector('app-error-panel')).not.toBeNull();
      expect(element.querySelector('app-empty-state')).toBeNull();
    });

    it('offers Load more only while the server says there is more', async () => {
      const fixture = await render();
      await loadConversations(fixture, [conversationFixture()], 1);

      expect(host(fixture).querySelector('.sidebar__more')).toBeNull();

      TestBed.inject(ConversationListStore).search('x');
      TestBed.tick();
      expectSearch().flush(conversationPage([conversationFixture()], { totalCount: 40 }));
      await fixture.whenStable();

      expect(host(fixture).querySelector('.sidebar__more')).not.toBeNull();
    });

    it('searches by name from the sidebar field, once the user pauses', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      const field = host(fixture).querySelector<HTMLInputElement>('.search__field');
      expect(field).not.toBeNull();
      field!.value = 'helios';
      field!.dispatchEvent(new Event('input'));
      await fixture.whenStable();

      // Nothing yet — SearchInput debounces, which is why the store deliberately
      // carries no second debounce of its own.
      backend.expectNone((request) => request.url === SEARCH_URL);

      // Real timers rather than vi.useFakeTimers(): the debounce is a setTimeout inside
      // an effect, and faking the clock also stalls the scheduler `whenStable` awaits.
      await new Promise((resolve) => setTimeout(resolve, SEARCH_DEBOUNCE_MS + 50));
      await fixture.whenStable();

      const request = expectSearch();
      expect(request.request.params.get('name')).toBe('helios');
      request.flush(conversationPage([]));
      await fixture.whenStable();
    });

    it('clears the search from the no-matches empty state', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      TestBed.inject(ConversationListStore).search('nothing matches');
      TestBed.tick();
      expectSearch().flush(conversationPage([]));
      await fixture.whenStable();

      host(fixture).querySelector<HTMLButtonElement>('app-empty-state button')?.click();
      TestBed.tick();

      const request = expectSearch();
      expect(request.request.params.has('name')).toBe(false);
      request.flush(conversationPage([conversationFixture()]));
      await fixture.whenStable();

      expect(host(fixture).querySelector('app-empty-state')).toBeNull();
    });

    it('fetches the next page when Load more is pressed', async () => {
      const fixture = await render();
      await loadConversations(fixture, [conversationFixture({ name: 'First' })], 40);

      host(fixture).querySelector<HTMLButtonElement>('.sidebar__more')?.click();
      await fixture.whenStable();

      const request = expectSearch();
      expect(request.request.params.get('skip')).toBe('1');
      request.flush(
        conversationPage([conversationFixture({ name: 'Second' })], { totalCount: 40 }),
      );
      await fixture.whenStable();

      const titles = [...host(fixture).querySelectorAll('.row__title')].map((n) => n.textContent);
      expect(titles).toEqual(['First', 'Second']);
    });

    it('keeps Load more focusable while its page is in flight', async () => {
      // `disabled` on the element just activated blurs it, dropping the user back to
      // <body> inside a scrolling list they then have to find again.
      const fixture = await render();
      await loadConversations(fixture, [conversationFixture()], 40);

      const more = host(fixture).querySelector<HTMLButtonElement>('.sidebar__more');
      more?.focus();
      more?.click();
      await fixture.whenStable();

      expect(document.activeElement).toBe(more);
      expect(more?.getAttribute('aria-disabled')).toBe('true');

      expectSearch().flush(conversationPage([conversationFixture()], { totalCount: 40 }));
      await fixture.whenStable();
    });
  });

  describe('navigation', () => {
    it('omits the Admin entry entirely for a non-administrator', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      const labels = [...host(fixture).querySelectorAll('.sidebar__nav-label')].map(
        (n) => n.textContent,
      );

      // US-203: absent, not shown-and-disabled.
      expect(labels).toEqual(['Chat', 'Conversations']);
    });

    it('links Conversations at the library route', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      // US-701 built the route, so the entry the board draws can finally exist —
      // the sidebar renders only the ones that do.
      const link = [...host(fixture).querySelectorAll('a')].find(
        (anchor) => anchor.querySelector('.sidebar__nav-label')?.textContent === 'Conversations',
      );
      expect(link?.getAttribute('href')).toBe('/conversations');
    });

    it('offers Admin to an administrator', async () => {
      const fixture = await render(true);
      await loadConversations(fixture);

      const labels = [...host(fixture).querySelectorAll('.sidebar__nav-label')].map(
        (n) => n.textContent,
      );

      expect(labels).toEqual(['Chat', 'Conversations', 'Admin']);
    });

    it('links New conversation at the chat route without creating anything', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      const link = host(fixture).querySelector<HTMLAnchorElement>('.sidebar__new');
      expect(link?.getAttribute('href')).toBe('/chat');

      link?.click();
      await fixture.whenStable();

      // US-303: nothing is posted until the first prompt.
      backend.verify();
    });
  });

  describe('collapse', () => {
    it('swaps the expanded panel for the 60px strip', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      const element = host(fixture);

      expect(element.querySelector('nav.sidebar')).not.toBeNull();
      expect(fixture.debugElement.nativeElement.classList.contains('shell--collapsed')).toBe(false);

      element.querySelector<HTMLButtonElement>('.sidebar__collapse')?.click();
      await fixture.whenStable();

      expect(element.querySelector('nav.strip')).not.toBeNull();
      expect(element.querySelector('nav.sidebar')).toBeNull();
      expect(fixture.debugElement.nativeElement.classList.contains('shell--collapsed')).toBe(true);
    });

    it('reports its state to assistive technology on the control that changes it', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      const element = host(fixture);

      const collapse = element.querySelector<HTMLButtonElement>('.sidebar__collapse');
      expect(collapse?.getAttribute('aria-expanded')).toBe('true');
      // No aria-controls: both branches put the id on the <nav> that *contains* the
      // toggle, so the reference would resolve to the element the user is already in
      // and tell them nothing. aria-expanded alone is the honest claim — the list, the
      // search field and the labels really are gone in the strip.
      expect(collapse?.hasAttribute('aria-controls')).toBe(false);

      collapse?.click();
      await fixture.whenStable();

      const expand = element.querySelector<HTMLButtonElement>('.strip__button');
      expect(expand?.getAttribute('aria-expanded')).toBe('false');
      expect(expand?.getAttribute('aria-label')).toBe('Expand sidebar');
    });

    it('names every icon-only control in the strip', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      const element = host(fixture);

      element.querySelector<HTMLButtonElement>('.sidebar__collapse')?.click();
      await fixture.whenStable();

      // The tooltip directive supplies the accessible name for a control that has no
      // text of its own, which is what keeps the strip usable by screen reader.
      for (const control of element.querySelectorAll('.strip__button')) {
        expect(control.getAttribute('aria-label')).toBeTruthy();
      }
    });

    it('expands and puts the caret in the field when the strip’s search is used', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      const element = host(fixture);

      element.querySelector<HTMLButtonElement>('.sidebar__collapse')?.click();
      await fixture.whenStable();

      // Selected by the accessible name the previous test proved exists, so adding a
      // control to the strip cannot silently repoint this.
      element
        .querySelector<HTMLButtonElement>('.strip__button[aria-label="Search conversations"]')
        ?.click();
      await fixture.whenStable();

      expect(element.querySelector('nav.sidebar')).not.toBeNull();
      expect(document.activeElement).toBe(element.querySelector('.search__field'));
    });

    it('survives a reload', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      host(fixture).querySelector<HTMLButtonElement>('.sidebar__collapse')?.click();
      await fixture.whenStable();

      expect(localStorage.getItem(PREFERENCE_KEYS.sidebar)).toBe('collapsed');

      // A fresh store is what a reload produces; the preference is what carries across.
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [] });
      expect(TestBed.inject(UiStore).sidebarCollapsed()).toBe(true);
    });

    it('withholds the width transition until after the first render', async () => {
      // Otherwise a returning user with a collapsed sidebar watches it animate shut on
      // every load — an animation of a state change that never happened.
      const fixture = TestBed.createComponent(Shell);

      expect((fixture.nativeElement as HTMLElement).classList.contains('shell--animated')).toBe(
        false,
      );

      await fixture.whenStable();

      expect((fixture.nativeElement as HTMLElement).classList.contains('shell--animated')).toBe(
        true,
      );
    });
  });
});
