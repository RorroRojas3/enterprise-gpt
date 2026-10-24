import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { getAllByRole, getByRole, getByText, queryByRole, queryByText } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { PREFERENCE_KEYS } from '@core/preferences/local-preferences';
import { ProjectLookupStore } from '@core/projects/project-lookup-store';
import { SessionStore } from '@core/session/session-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { settle } from '@testing/async';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { PREFERS_DARK, resetMediaQueries, setMediaQuery } from '@testing/media-query';
import { provideFakeMsal, signedInMsal } from '@testing/msal';
import { provideFakeNavigation } from '@testing/navigation';
import { dialogNamed, menuItems, openMenu } from '@testing/overlays';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { projectFixture, projectPage } from '@testing/projects';
import { administratorFixture, userFixture } from '@testing/session';
import { Shell } from './shell';
import { UiStore } from './ui-store';

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
   * Releases the root project lookup and answers it, as `SessionBootstrap` would. Other tests
   * leave it unreleased, which is why they show no Favorite projects section.
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

  /** The expanded panel's own control; the strip has no search field. */
  function searchField(element: HTMLElement): HTMLInputElement {
    return getByRole(element, 'searchbox', { name: 'Search conversations' });
  }

  function isExpanded(element: HTMLElement): boolean {
    return queryByRole(element, 'searchbox', { name: 'Search conversations' }) !== null;
  }

  function isStrip(element: HTMLElement): boolean {
    return queryByRole(element, 'button', { name: 'Expand sidebar' }) !== null;
  }

  function collapseButton(element: HTMLElement): HTMLButtonElement {
    return getByRole(element, 'button', { name: 'Collapse sidebar' });
  }

  function favoritesSection(element: HTMLElement): HTMLElement | null {
    return queryByRole(element, 'region', { name: 'Favorite projects' });
  }

  function favoritesHeading(element: HTMLElement): HTMLButtonElement {
    return getByRole(element, 'button', { name: 'Favorite projects' });
  }

  function disclosureFor(element: HTMLElement, name: string): HTMLButtonElement {
    return getByRole(element, 'button', { name: `Conversations in ${name}` });
  }

  /** The panel a node's disclosure controls, while it is open. */
  function nestedPanel(element: HTMLElement, projectId: string): HTMLElement | null {
    return element.querySelector<HTMLElement>(`#sidebar-project-${projectId}`);
  }

  function linkNames(root: HTMLElement): (string | undefined)[] {
    return getAllByRole(root, 'link').map((link) => link.textContent?.trim());
  }

  function conversationsSection(element: HTMLElement): HTMLElement {
    return getByRole(element, 'region', { name: 'Conversations' });
  }

  /** The conversation rows, by their link titles. */
  function rowTitles(element: HTMLElement): (string | undefined)[] {
    return linkNames(conversationsSection(element));
  }

  function loadMore(element: HTMLElement): HTMLButtonElement | null {
    return queryByRole(element, 'button', { name: 'Load more conversations' });
  }

  /** The first list in the expanded panel is its section navigation. */
  function sectionLabels(element: HTMLElement): (string | undefined)[] {
    const primary = getByRole(element, 'navigation', { name: 'Primary' });

    return linkNames(getAllByRole(primary, 'list')[0]!);
  }

  it('puts a skip link ahead of everything else in the tab order', async () => {
    const fixture = await render();
    const element = host(fixture);
    const skip = getByRole(element, 'link', { name: 'Skip to main content' });

    expect(skip.getAttribute('href')).toBe('#main-content');
    expect(element.querySelector('#main-content')?.getAttribute('tabindex')).toBe('-1');

    skip.click();
    await fixture.whenStable();

    // Focus moves, and the fragment never reaches the router.
    expect(document.activeElement).toBe(element.querySelector('#main-content'));
    expect(TestBed.inject(Router).url).not.toContain('#');
  });

  describe('favorite projects', () => {
    it('does not render the section when nothing is starred', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture, [projectFixture(), projectFixture()]);

      expect(favoritesSection(host(fixture))).toBeNull();
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

      expect(favoritesHeading(element).id).toBe('sidebar-favorites-heading');
      // No device-local pins notice: pins were never built.
      expect(queryByText(element, /Pinned on this device/)).toBeNull();
      expect(linkNames(favoritesSection(element)!)).toEqual([
        'Data Platform Migration',
        'RFP Responses',
      ]);
    });

    it('reads one project-filtered request when a node is expanded, and drops it when closed', async () => {
      const project = projectFixture({ name: 'Data Platform Migration', isFavorite: true });
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture, [project]);
      const element = host(fixture);

      const disclosure = disclosureFor(element, project.name);
      expect(disclosure.getAttribute('aria-expanded')).toBe('false');
      // No aria-controls while shut: the panel it would name is not in the DOM.
      expect(disclosure.getAttribute('aria-controls')).toBeNull();

      disclosure.click();
      await fixture.whenStable();

      // One project-filtered request, not a drain over the whole conversation list.
      const request = expectNested(project.id);
      request.flush(conversationPage([conversationFixture({ name: 'Cutover plan' })]));
      await fixture.whenStable();

      expect(disclosure.getAttribute('aria-expanded')).toBe('true');
      expect(disclosure.getAttribute('aria-controls')).toBe(`sidebar-project-${project.id}`);
      expect(linkNames(nestedPanel(element, project.id)!)).toEqual(['Cutover plan']);

      disclosure.click();
      await fixture.whenStable();

      // The node's store goes with the node, so nothing of the closed project remains.
      expect(nestedPanel(element, project.id)).toBeNull();
      expect(queryByRole(element, 'link', { name: 'Cutover plan' })).toBeNull();
    });

    it('offers unfavourite, rename and delete on a project row, and nothing else', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture);

      const menu = await openMenu(fixture, 'Actions for Data Platform Migration');

      expect(menuItems(menu).map((item) => item.textContent?.trim())).toEqual([
        'Unfavourite',
        'Rename',
        'Delete',
      ]);
    });

    it('shows the nested panel’s own empty and failed states', async () => {
      const project = projectFixture({ name: 'Data Platform Migration', isFavorite: true });
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture, [project]);
      const element = host(fixture);

      disclosureFor(element, project.name).click();
      await fixture.whenStable();
      expectNested(project.id).flush(conversationPage([]));
      await fixture.whenStable();

      // A line, not the kit's empty state: this is one sentence inside a disclosure two
      // levels deep, and `app-empty-state` carries a heading and an action slot.
      const panel = nestedPanel(element, project.id)!;
      expect(getByText(panel, 'No conversations yet.')).toBeTruthy();
      expect(panel.querySelector('app-empty-state')).toBeNull();

      // Close and reopen, so the node's store is rebuilt and asks again.
      disclosureFor(element, project.name).click();
      await fixture.whenStable();
      disclosureFor(element, project.name).click();
      await fixture.whenStable();

      expectNested(project.id).flush(PROBLEM_FIXTURES.resourceNotFound, {
        status: 404,
        statusText: 'Not Found',
      });
      await fixture.whenStable();

      const failed = nestedPanel(element, project.id)!;
      expect(getByRole(failed, 'alert').textContent).toContain('Conversations couldn’t load');
      // Headingless: the row above already names the project, and a second heading here
      // would put a level under the section's <h2> for one failed sub-request.
      expect(queryByRole(failed, 'heading')).toBeNull();
    });

    it('keeps two open nodes on their own results', async () => {
      // Why the store is provided per node: two open sections each need their own query.
      const first = projectFixture({ name: 'Data Platform Migration', isFavorite: true });
      const second = projectFixture({ name: 'RFP Responses', isFavorite: true });
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture, [first, second]);
      const element = host(fixture);

      disclosureFor(element, first.name).click();
      await fixture.whenStable();
      expectNested(first.id).flush(
        conversationPage([conversationFixture({ name: 'Cutover plan' })]),
      );
      await fixture.whenStable();

      disclosureFor(element, second.name).click();
      await fixture.whenStable();
      expectNested(second.id).flush(
        conversationPage([conversationFixture({ name: 'Vendor SSO reply' })]),
      );
      await fixture.whenStable();

      expect(linkNames(nestedPanel(element, first.id)!)).toEqual(['Cutover plan']);
      expect(linkNames(nestedPanel(element, second.id)!)).toEqual(['Vendor SSO reply']);
    });

    /** Opens a project's kebab and presses one of its items. */
    async function pressRowMenuItem(
      fixture: ComponentFixture<Shell>,
      label: string,
      project = 'Data Platform Migration',
    ): Promise<void> {
      const menu = await openMenu(fixture, `Actions for ${project}`);
      getByRole(menu, 'menuitem', { name: label }).click();
      await fixture.whenStable();
    }

    it('moves focus to the heading when unfavouriting evicts the row it was invoked from', async () => {
      // The write isn't optimistic, so the row focus was returned to is destroyed a round trip
      // after the click; without a rescue, focus lands on <body>.
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

      expect(linkNames(favoritesSection(element)!)).toEqual(['RFP Responses']);
      expect(document.activeElement).toBe(favoritesHeading(element));
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

      // The heading went with the section; the search field is the nearest control above it.
      expect(favoritesSection(element)).toBeNull();
      expect(document.activeElement).toBe(searchField(element));
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

      // The row survived, so the focus intent must be dropped, not left armed.
      expect(linkNames(favoritesSection(element)!)).toEqual(['Data Platform Migration']);
      expect(document.activeElement).not.toBe(favoritesHeading(element));
    });

    it('replaces the tree with a tooltipped star on the 60px strip', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      await loadProjects(fixture);
      const element = host(fixture);

      collapseButton(element).click();
      await fixture.whenStable();

      expect(favoritesSection(element)).toBeNull();
      // Named by its tooltip, the only text an icon-only strip control has.
      const star = getByRole(element, 'button', { name: 'Favorite projects' });
      expect(star.getAttribute('aria-label')).toBe('Favorite projects');

      star.click();
      await fixture.whenStable();

      // The star expands rather than navigates; focus follows through two nested render hooks.
      expect(favoritesSection(element)).not.toBeNull();
      expect(document.activeElement).toBe(favoritesHeading(element));
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
      const region = getByRole(element, 'region', { name: 'Conversations' });
      expect(region.querySelector('[aria-busy="true"]')).not.toBeNull();

      expectSearch().flush(conversationPage([]));
    });

    it('renders the rows the server returned, in its order', async () => {
      const fixture = await render();
      await loadConversations(fixture, [
        conversationFixture({ name: 'Newest' }),
        conversationFixture({ name: 'Older' }),
      ]);

      expect(rowTitles(host(fixture))).toEqual(['Newest', 'Older']);
    });

    it('marks the open conversation as the current page', async () => {
      const open = conversationFixture({ name: 'Open one' });
      const other = conversationFixture({ name: 'Another' });
      const fixture = await render();
      await loadConversations(fixture, [open, other]);

      await TestBed.inject(Router).navigateByUrl(`/chat/${open.id}`);
      await fixture.whenStable();

      // The row wrapper paints the selected surface; `aria-current` stays on the link.
      const links = getAllByRole(conversationsSection(host(fixture)), 'link');
      expect(links[0]?.getAttribute('aria-current')).toBe('page');
      expect(links[0]?.parentElement?.classList.contains('active')).toBe(true);
      // Exact matching, or every row would light up under /chat.
      expect(links[1]?.getAttribute('aria-current')).toBeNull();
      expect(links[1]?.parentElement?.classList.contains('active')).toBe(false);

      // And only the link claims `page`: the Chat nav link stays active under
      // /chat/{id} by prefix match, so it announces `true` instead of competing.
      expect(getAllByRole(host(fixture), 'link', { current: 'page' })).toEqual([links[0]]);
      expect(getByRole(host(fixture), 'link', { current: true }).textContent?.trim()).toBe('Chat');
    });

    it('scrolls the list on its own rather than the whole sidebar', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      // The overflow utility is the contract under test here.
      const region = getByRole(host(fixture), 'region', { name: 'Conversations' });
      const list = region.querySelector('.overflow-y-auto');
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
      expect(getByRole(element, 'heading', { name: 'No conversations yet' })).toBeTruthy();
    });

    it('says a search matched nothing differently from having nothing', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      TestBed.inject(ConversationListStore).search('nothing matches');
      TestBed.tick();
      expectSearch().flush(conversationPage([]));
      await fixture.whenStable();

      const element = host(fixture);
      expect(getByRole(element, 'heading', { name: 'No matching conversations' })).toBeTruthy();
      expect(queryByRole(element, 'heading', { name: 'No conversations yet' })).toBeNull();
    });

    it('reports a failed load with a retry rather than as an empty list', async () => {
      const fixture = await render();
      TestBed.inject(ConversationListStore).ensureLoaded();
      TestBed.tick();
      expectSearch().flush(PROBLEM_FIXTURES.forbidden, { status: 403, statusText: 'Forbidden' });
      await fixture.whenStable();
      const element = host(fixture);

      expect(getByRole(element, 'alert').textContent).toContain('Conversations couldn’t load');
      expect(element.querySelector('app-empty-state')).toBeNull();
    });

    it('offers Load more only while the server says there is more', async () => {
      const fixture = await render();
      await loadConversations(fixture, [conversationFixture()], 1);

      expect(loadMore(host(fixture))).toBeNull();

      TestBed.inject(ConversationListStore).search('x');
      TestBed.tick();
      expectSearch().flush(conversationPage([conversationFixture()], { totalCount: 40 }));
      await fixture.whenStable();

      expect(loadMore(host(fixture))).not.toBeNull();
    });

    it('searches by name from the sidebar field, once the user pauses', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      const field = searchField(host(fixture));
      field.value = 'helios';
      field.dispatchEvent(new Event('input'));
      await fixture.whenStable();

      // Nothing yet: `SearchInput` debounces, so the store carries no debounce of its own.
      backend.expectNone((request) => request.url === SEARCH_URL);

      // Real timers: faking the clock also stalls the scheduler `whenStable` awaits.
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

      getByRole(host(fixture), 'button', { name: 'Clear search' }).click();
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

      loadMore(host(fixture))!.click();
      await fixture.whenStable();

      const request = expectSearch();
      expect(request.request.params.get('skip')).toBe('1');
      request.flush(
        conversationPage([conversationFixture({ name: 'Second' })], { totalCount: 40 }),
      );
      await fixture.whenStable();

      expect(rowTitles(host(fixture))).toEqual(['First', 'Second']);
    });

    it('keeps Load more focusable while its page is in flight', async () => {
      // `disabled` would blur the button just pressed, dropping focus to <body>.
      const fixture = await render();
      await loadConversations(fixture, [conversationFixture()], 40);

      const more = loadMore(host(fixture))!;
      more.focus();
      more.click();
      await fixture.whenStable();

      expect(document.activeElement).toBe(more);
      expect(more.getAttribute('aria-disabled')).toBe('true');
      expect(more.textContent).toContain('Loading…');

      expectSearch().flush(conversationPage([conversationFixture()], { totalCount: 40 }));
      await fixture.whenStable();
    });
  });

  describe('navigation', () => {
    it('omits the Admin entry entirely for a non-administrator', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      expect(sectionLabels(host(fixture))).toEqual([
        'Chat',
        'Conversations',
        'Projects',
        'Documents',
      ]);
    });

    it('links Conversations at the library route', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      const link = getByRole(host(fixture), 'link', { name: 'Conversations' });
      expect(link.getAttribute('href')).toBe('/conversations');
    });

    it('links Projects at the projects route', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      const link = getByRole(host(fixture), 'link', { name: 'Projects' });
      expect(link.getAttribute('href')).toBe('/projects');
    });

    it('links Documents at the documents route', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      const link = getByRole(host(fixture), 'link', { name: 'Documents' });
      expect(link.getAttribute('href')).toBe('/documents');
    });

    it('offers Admin to an administrator', async () => {
      const fixture = await render(true);
      await loadConversations(fixture);

      expect(sectionLabels(host(fixture))).toEqual([
        'Chat',
        'Conversations',
        'Projects',
        'Documents',
        'Admin',
      ]);
    });

    it('links New conversation at the chat route without creating anything', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      const link = getByRole(host(fixture), 'link', { name: 'New conversation' });
      expect(link.getAttribute('href')).toBe('/chat');

      link.click();
      await fixture.whenStable();

      // Nothing is posted until the first prompt.
      backend.verify();
    });
  });

  describe('collapse', () => {
    it('swaps the expanded panel for the 60px strip', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      const element = host(fixture);

      expect(isExpanded(element)).toBe(true);
      expect(element.classList.contains('shell--collapsed')).toBe(false);

      collapseButton(element).click();
      await fixture.whenStable();

      expect(isStrip(element)).toBe(true);
      expect(isExpanded(element)).toBe(false);
      expect(element.classList.contains('shell--collapsed')).toBe(true);
    });

    it('reports its state to assistive technology on the control that changes it', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      const element = host(fixture);

      const collapse = collapseButton(element);
      expect(collapse.getAttribute('aria-expanded')).toBe('true');
      // No aria-controls: the id is on the <nav> containing the toggle, so it would name the
      // element the user is already in.
      expect(collapse.hasAttribute('aria-controls')).toBe(false);

      collapse.click();
      await fixture.whenStable();

      const expand = getByRole(element, 'button', { name: 'Expand sidebar' });
      expect(expand.getAttribute('aria-expanded')).toBe('false');
    });

    it('names every icon-only control in the strip', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      const element = host(fixture);

      collapseButton(element).click();
      await fixture.whenStable();

      // The tooltip directive supplies each name; the footer's controls carry their own.
      const strip = getByRole(element, 'navigation', { name: 'Primary' });
      const controls = [...getAllByRole(strip, 'button'), ...getAllByRole(strip, 'link')];
      expect(controls.length).toBeGreaterThan(0);
      for (const control of controls) {
        expect(control.getAttribute('aria-label')).toBeTruthy();
      }
    });

    it('expands and puts the caret in the field when the strip’s search is used', async () => {
      const fixture = await render();
      await loadConversations(fixture);
      const element = host(fixture);

      collapseButton(element).click();
      await fixture.whenStable();

      // Selected by the accessible name the previous test proved exists, so adding a
      // control to the strip cannot silently repoint this.
      getByRole(element, 'button', { name: 'Search conversations' }).click();
      await fixture.whenStable();

      expect(isExpanded(element)).toBe(true);
      expect(document.activeElement).toBe(searchField(element));
    });

    it('survives a reload', async () => {
      const fixture = await render();
      await loadConversations(fixture);

      collapseButton(host(fixture)).click();
      await fixture.whenStable();

      expect(localStorage.getItem(PREFERENCE_KEYS.sidebar)).toBe('collapsed');

      // A fresh store is what a reload produces; the preference is what carries across.
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [] });
      expect(TestBed.inject(UiStore).sidebarCollapsed()).toBe(true);
    });

    it('withholds the width transition until after the first render', async () => {
      // Or a restored collapsed sidebar would animate shut on every load.
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

  describe('keyboard operability', () => {
    // jsdom has no `<dialog>`, hence the shim; the top layer and native inerting are checked in
    // a browser, so this asserts focus movement, not the trap.
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
      // `.click()` throughout: these are native buttons, so Enter and Space are the platform's
      // job, and jsdom synthesises no click from a key event. This pins reachability and focus.
      const fixture = await render();
      const conversation = conversationFixture({ name: 'Helios 2.4 release status' });
      await loadConversations(fixture, [conversation]);
      const element = host(fixture);
      // The fixture has to be in the document or `activeElement` is never these.
      document.body.append(element);
      attached = element;

      const kebab = getByRole(element, 'button', { name: `Actions for ${conversation.name}` });
      kebab.focus();

      // ArrowDown opens the menu and focuses its first item.
      kebab.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }),
      );
      await fixture.whenStable();

      const items = menuItems(getByRole(element, 'menu'));
      expect(items.length).toBeGreaterThan(0);
      expect(document.activeElement).toBe(items[0]);
      expect(kebab.getAttribute('aria-expanded')).toBe('true');

      items.find((item) => item.textContent?.includes('Rename'))!.click();
      await fixture.whenStable();

      // Focus moved into the dialog, onto the field the user came here to edit.
      const dialog = dialogNamed('Rename conversation') as HTMLDialogElement;
      expect(dialog.contains(document.activeElement)).toBe(true);

      // `close()`, not Escape: the jsdom shim implements neither Escape nor `cancel`. What this
      // pins is that focus returns to the kebab.
      dialog.close();
      await fixture.whenStable();

      expect(document.activeElement).toBe(kebab);

      // Delete is the same journey to a different dialog, and the return matters
      // more there: the row it acted on may not exist to fall back to.
      kebab.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }),
      );
      await fixture.whenStable();

      getByRole(getByRole(element, 'menu'), 'menuitem', { name: 'Delete' }).click();
      await fixture.whenStable();

      const confirm = dialogNamed('Delete conversation?') as HTMLDialogElement;

      // Exactly Cancel, not merely inside: Enter on a freshly opened destructive confirm must
      // not destroy anything.
      expect(document.activeElement).toBe(getByRole(confirm, 'button', { name: 'Cancel' }));

      confirm.close();
      await fixture.whenStable();
      expect(document.activeElement).toBe(kebab);
    });
  });
});
