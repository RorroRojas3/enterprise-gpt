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
import { ProjectLookupStore } from '@core/projects/project-lookup-store';
import { SessionStore } from '@core/session/session-store';
import { PREFERENCE_KEYS } from '@core/storage/local-preferences';
import { UiStore } from '@core/ui/ui-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { settle } from '@testing/async';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { projectFixture, projectPage } from '@testing/projects';
import { PREFERS_DARK, resetMediaQueries, setMediaQuery } from '@testing/media-query';
import { provideFakeMsal, signedInMsal } from '@testing/msal';
import { provideFakeNavigation } from '@testing/navigation';
import { administratorFixture, userFixture } from '@testing/session';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Shell } from './shell';

const ME_URL = `${TEST_API_BASE_URL}/api/users/me`;
const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;
const PROJECTS_URL = `${TEST_API_BASE_URL}/api/projects`;

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

  /**
   * Releases the root project lookup and answers it, as `SessionBootstrap` would.
   *
   * Every other test in this file leaves it unreleased, which is why the Favorite
   * projects section is absent from all of them without any of them saying so.
   */
  async function loadProjects(
    fixture: ComponentFixture<Shell>,
    items = [projectFixture({ name: 'Data Platform Migration', isFavorite: true })],
  ): Promise<void> {
    TestBed.inject(ProjectLookupStore).ensureLoaded();
    TestBed.tick();
    backend.expectOne((request) => request.url === PROJECTS_URL).flush(projectPage(items));
    await fixture.whenStable();
  }

  /** The one `projectId`-filtered request an expanded sidebar node issues. */
  function expectNested(projectId: string): TestRequest {
    return backend.expectOne(
      (request) => request.url === SEARCH_URL && request.params.get('projectId') === projectId,
    );
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

  describe('favorite projects (US-910)', () => {
    it('does not render the section when nothing is starred', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture, [projectFixture(), projectFixture()]);

      // Absent rather than an empty section with a heading over nothing — and the
      // strip's stand-in goes with it, since expanding to a section that is not there
      // is an affordance with nothing behind it.
      expect(host(fixture).querySelector('app-sidebar-favorites .favorites')).toBeNull();
    });

    it('lists the starred projects under the heading, in the server order', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture, [
        projectFixture({ name: 'Data Platform Migration', isFavorite: true }),
        projectFixture({ name: 'Hiring' }),
        projectFixture({ name: 'RFP Responses', isFavorite: true }),
      ]);
      const element = host(fixture);

      expect(element.querySelector('#sidebar-favorites-heading')?.textContent).toContain(
        'Favorite projects',
      );
      // No device-local notice: US-909 landed in the same batch, so the pins the story's
      // first criterion describes were never built.
      expect(element.querySelector('.favorites')?.textContent ?? '').not.toContain(
        'Pinned on this device',
      );
      const names = [...element.querySelectorAll('.project__title')].map((n) => n.textContent);
      expect(names).toEqual(['Data Platform Migration', 'RFP Responses']);
    });

    it('reads one project-filtered request when a node is expanded, and drops it when closed', async () => {
      const project = projectFixture({ name: 'Data Platform Migration', isFavorite: true });
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture, [project]);
      const element = host(fixture);

      const disclosure = element.querySelector<HTMLButtonElement>('.project__disclosure');
      expect(disclosure?.getAttribute('aria-expanded')).toBe('false');
      // No aria-controls while shut: the panel it would name is not in the DOM.
      expect(disclosure?.getAttribute('aria-controls')).toBeNull();

      disclosure?.click();
      await fixture.whenStable();

      // US-907's filter — one request, no drain over the whole conversation list.
      const request = expectNested(project.id);
      request.flush(conversationPage([conversationFixture({ name: 'Helios 2.4 release status' })]));
      await fixture.whenStable();

      expect(element.querySelector('.project__disclosure')?.getAttribute('aria-controls')).toBe(
        `sidebar-project-${project.id}`,
      );
      expect(element.querySelector('.nested__title')?.textContent).toContain(
        'Helios 2.4 release status',
      );

      element.querySelector<HTMLButtonElement>('.project__disclosure')?.click();
      await fixture.whenStable();

      // The node's store goes with the node, so nothing of the closed project remains.
      expect(element.querySelector('.nested__title')).toBeNull();
    });

    it('offers unfavourite, rename and delete on a project row, and nothing else', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture);
      const element = host(fixture);

      element.querySelector<HTMLButtonElement>('.project__menu button')?.click();
      await fixture.whenStable();

      const items = [...element.querySelectorAll('.project__menu [role="menuitem"]')].map((item) =>
        item.textContent?.trim(),
      );
      expect(items).toEqual(['Unfavourite', 'Rename', 'Delete']);
    });

    it('shows the nested panel’s own empty and failed states', async () => {
      const project = projectFixture({ name: 'Data Platform Migration', isFavorite: true });
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture, [project]);
      const element = host(fixture);

      element.querySelector<HTMLButtonElement>('.project__disclosure')?.click();
      await fixture.whenStable();
      expectNested(project.id).flush(conversationPage([]));
      await fixture.whenStable();

      // A line, not the kit's empty state: this is one sentence inside a disclosure two
      // levels deep, and `app-empty-state` carries a heading and an action slot.
      expect(element.querySelector('.nested__empty')?.textContent).toContain(
        'No conversations yet.',
      );
      expect(element.querySelector('app-sidebar-project-conversations app-empty-state')).toBeNull();

      // Close and reopen, so the node's store is rebuilt and asks again.
      element.querySelector<HTMLButtonElement>('.project__disclosure')?.click();
      await fixture.whenStable();
      element.querySelector<HTMLButtonElement>('.project__disclosure')?.click();
      await fixture.whenStable();

      expectNested(project.id).flush(PROBLEM_FIXTURES.resourceNotFound, {
        status: 404,
        statusText: 'Not Found',
      });
      await fixture.whenStable();

      expect(
        element.querySelector('app-sidebar-project-conversations app-error-panel'),
      ).not.toBeNull();
      // Headingless: the row above already names the project, and a second heading here
      // would put a level under the section's <h2> for one failed sub-request.
      expect(
        element.querySelector('app-sidebar-project-conversations app-error-panel h3'),
      ).toBeNull();
    });

    it('keeps two open nodes on their own results', async () => {
      // The reason the store is provided per node rather than once on the row: two
      // sections open at the same time each need their own projectId-filtered query.
      const first = projectFixture({ name: 'Data Platform Migration', isFavorite: true });
      const second = projectFixture({ name: 'RFP Responses', isFavorite: true });
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture, [first, second]);
      const element = host(fixture);

      const disclosures = () => [
        ...element.querySelectorAll<HTMLButtonElement>('.project__disclosure'),
      ];

      disclosures()[0]?.click();
      await fixture.whenStable();
      expectNested(first.id).flush(
        conversationPage([conversationFixture({ name: 'Cutover plan' })]),
      );
      await fixture.whenStable();

      disclosures()[1]?.click();
      await fixture.whenStable();
      expectNested(second.id).flush(
        conversationPage([conversationFixture({ name: 'Vendor SSO reply' })]),
      );
      await fixture.whenStable();

      const titles = [...element.querySelectorAll('.nested__title')].map((n) => n.textContent);
      expect(titles).toEqual(['Cutover plan', 'Vendor SSO reply']);
    });

    /** Opens the first project kebab and presses one of its items. */
    async function pressRowMenuItem(
      fixture: ComponentFixture<Shell>,
      label: string,
      index = 0,
    ): Promise<void> {
      const element = host(fixture);
      [...element.querySelectorAll<HTMLButtonElement>('.project__menu button')][index]?.click();
      await fixture.whenStable();
      [...element.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')]
        .find((item) => item.textContent?.trim() === label)
        ?.click();
      await fixture.whenStable();
    }

    it('moves focus to the heading when unfavouriting evicts the row it was invoked from', async () => {
      // The write is not optimistic, so the <li> holding the menu trigger focus was
      // returned to is destroyed a round trip after the click. Without the rescue focus
      // lands on <body>, and the next Tab restarts at the top of the page.
      const first = projectFixture({ name: 'Data Platform Migration', isFavorite: true });
      const second = projectFixture({ name: 'RFP Responses', isFavorite: true });
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture, [first, second]);
      const element = host(fixture);

      await pressRowMenuItem(fixture, 'Unfavourite');
      backend
        .expectOne(`${PROJECTS_URL}/${first.id}/favorite`)
        .flush(null, { status: 204, statusText: 'No Content' });
      await fixture.whenStable();

      expect([...element.querySelectorAll('.project__title')].map((n) => n.textContent)).toEqual([
        'RFP Responses',
      ]);
      expect(document.activeElement).toBe(element.querySelector('#sidebar-favorites-heading'));
    });

    it('falls back to the search field when the last favourite takes the section with it', async () => {
      const only = projectFixture({ name: 'Data Platform Migration', isFavorite: true });
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture, [only]);
      const element = host(fixture);

      await pressRowMenuItem(fixture, 'Unfavourite');
      backend
        .expectOne(`${PROJECTS_URL}/${only.id}/favorite`)
        .flush(null, { status: 204, statusText: 'No Content' });
      await fixture.whenStable();

      // The section is gone, so the heading the previous test focuses is gone too. The
      // nearest control above where it stood is the sidebar's own search field.
      expect(element.querySelector('.favorites')).toBeNull();
      expect(document.activeElement).toBe(element.querySelector('.search__field'));
    });

    it('leaves focus alone when the unfavourite fails and the row stays', async () => {
      const project = projectFixture({ name: 'Data Platform Migration', isFavorite: true });
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture, [project]);
      const element = host(fixture);

      await pressRowMenuItem(fixture, 'Unfavourite');
      backend
        .expectOne(`${PROJECTS_URL}/${project.id}/favorite`)
        .flush(PROBLEM_FIXTURES.resourceNotFound, { status: 404, statusText: 'Not Found' });
      await fixture.whenStable();

      // The row survived, so the intent must be dropped rather than left armed to fire
      // against some unrelated later removal.
      expect(element.querySelector('.project__title')?.textContent).toContain(
        'Data Platform Migration',
      );
      expect(document.activeElement).not.toBe(element.querySelector('#sidebar-favorites-heading'));
    });

    it('replaces the tree with a tooltipped star on the 60px strip', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture);
      const element = host(fixture);

      element.querySelector<HTMLButtonElement>('.sidebar__collapse')?.click();
      await fixture.whenStable();

      expect(element.querySelector('.favorites')).toBeNull();
      const star = [...element.querySelectorAll<HTMLButtonElement>('.strip__button')].find(
        (control) => control.getAttribute('aria-label') === 'Favorite projects',
      );
      expect(star).toBeDefined();

      star?.click();
      await fixture.whenStable();

      // Expanding is the whole action: the tree is what the icon stands in for, so
      // pressing it reveals the tree rather than navigating away. Focus follows through
      // two nested render hooks — the sidebar's, which waits for the expanded branch,
      // and the section's, which waits for its own heading.
      expect(element.querySelector('.favorites')).not.toBeNull();
      expect(document.activeElement).toBe(element.querySelector('#sidebar-favorites-heading'));
    });
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
      expect(labels).toEqual(['Chat', 'Conversations', 'Projects', 'Documents']);
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

    it('links Projects at the projects route', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      // US-901 built the route, so the entry the board draws can finally exist — the
      // sidebar renders only the ones that do.
      const link = [...host(fixture).querySelectorAll('a')].find(
        (anchor) => anchor.querySelector('.sidebar__nav-label')?.textContent === 'Projects',
      );
      expect(link?.getAttribute('href')).toBe('/projects');
    });

    it('links Documents at the documents route', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      // US-1002 built the route, so the entry the board draws can finally exist — the
      // sidebar renders only the ones that do.
      const link = [...host(fixture).querySelectorAll('a')].find(
        (anchor) => anchor.querySelector('.sidebar__nav-label')?.textContent === 'Documents',
      );
      expect(link?.getAttribute('href')).toBe('/documents');
    });

    it('offers Admin to an administrator', async () => {
      const fixture = await render(true);
      await loadConversations(fixture);

      const labels = [...host(fixture).querySelectorAll('.sidebar__nav-label')].map(
        (n) => n.textContent,
      );

      expect(labels).toEqual(['Chat', 'Conversations', 'Projects', 'Documents', 'Admin']);
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

  describe('keyboard operability (US-1401)', () => {
    // jsdom implements no `<dialog>`, so the shell's own dialogs need the same
    // shim the overlay specs use. What it cannot stand in for is the top layer,
    // `::backdrop` or native inerting — those are checked in a browser through
    // `/ui-kit`, which is why this spec asserts focus movement and not the trap.
    let attached: HTMLElement | null = null;

    beforeEach(() => {
      installDialogPolyfill();
    });

    // Not at the end of the test: a failing assertion would otherwise leak the
    // fixture host into `document.body` for every spec that follows.
    afterEach(() => {
      attached?.remove();
      attached = null;
    });

    it('runs rename and delete end to end from the keyboard', async () => {
      // Criterion 3's two remaining paths, and criterion 2's focus contract, in
      // the place they actually meet: the sidebar row's kebab and the dialogs
      // US-307 moved onto the shell.
      //
      // Activation is `.click()` throughout, and deliberately: every one of these
      // is a native `<button>`, so Enter and Space are the platform's guarantee
      // rather than the app's, and jsdom synthesises no click from a key event.
      // What this pins is everything the app *is* responsible for — that each
      // step is reachable, that focus lands where the next step needs it, and
      // that it comes back.
      const fixture = await render();
      const conversation = conversationFixture({ name: 'Helios 2.4 release status' });
      await loadConversations(fixture, [conversation]);
      const element = host(fixture);
      // The fixture has to be in the document or `activeElement` is never these.
      document.body.append(element);
      attached = element;

      // Both conversation dialogs mount on the shell (US-307), so a bare
      // `dialog` selector finds whichever is first in the DOM rather than the
      // one that opened.
      const openDialog = () =>
        [...element.querySelectorAll<HTMLDialogElement>('app-modal dialog')].find(
          (node) => node.open,
        );

      const kebab = element.querySelector<HTMLButtonElement>('.row__menu .menu__trigger');
      expect(kebab).not.toBeNull();
      kebab!.focus();

      // ArrowDown opens the menu *and* puts focus on the first item, which is
      // what makes a menu button operable without a pointer at all.
      kebab!.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }),
      );
      await fixture.whenStable();

      const items = [...element.querySelectorAll<HTMLElement>('[role="menuitem"]')];
      expect(items.length).toBeGreaterThan(0);
      expect(document.activeElement).toBe(items[0]);
      expect(kebab!.getAttribute('aria-expanded')).toBe('true');

      const rename = items.find((item) => item.textContent?.includes('Rename'));
      rename!.click();
      await fixture.whenStable();

      // Focus moved into the dialog, onto the field the user came here to edit.
      const dialog = openDialog();
      expect(dialog).toBeDefined();
      expect(dialog?.contains(document.activeElement)).toBe(true);
      expect(dialog?.textContent).toContain('Rename conversation');

      // Closing hands focus back to the control that opened it, rather than to
      // `<body>`, which would drop a keyboard user at the top of the page.
      //
      // `close()`, not Escape: Escape belongs to the native `<dialog>`, and the
      // jsdom shim implements neither it nor `cancel`. That the app does not
      // *refuse* Escape is `modal.spec.ts`'s ("swallows Escape while a save is in
      // flight"); what is the app's own job, and is what this pins, is where
      // focus lands afterwards.
      dialog!.close();
      await fixture.whenStable();

      expect(document.activeElement).toBe(kebab);

      // Delete is the same journey to a different dialog, and the return matters
      // more there: the row it acted on may not exist to fall back to.
      kebab!.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }),
      );
      await fixture.whenStable();

      const remove = [...element.querySelectorAll<HTMLElement>('[role="menuitem"]')].find((item) =>
        item.textContent?.includes('Delete'),
      );
      remove!.click();
      await fixture.whenStable();

      const confirm = openDialog();
      expect(confirm).toBeDefined();
      expect(confirm?.textContent).toContain('Delete conversation?');

      // On **Cancel**, and the identity is the assertion rather than "somewhere
      // inside": Enter on a freshly opened destructive confirm must not destroy
      // anything, and `contains()` would pass just as happily with focus on
      // Delete.
      const cancel = [...confirm!.querySelectorAll<HTMLButtonElement>('button')].find((button) =>
        button.textContent?.includes('Cancel'),
      );
      expect(document.activeElement).toBe(cancel);

      confirm!.close();
      await fixture.whenStable();
      expect(document.activeElement).toBe(kebab);
    });
  });
});
