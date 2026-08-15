import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { afterEach, describe, expect, it } from 'vitest';
import { ConversationDto } from '@domain/api/conversation';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import {
  PROJECT_CONVERSATIONS_PAGE_SIZE,
  ProjectConversationsStore,
} from './project-conversations-store';
import { ProjectConversations } from './project-conversations';

const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;
const PUT_URL = `${TEST_API_BASE_URL}/api/conversations`;
const PROJECT_ID = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';

describe('ProjectConversations (US-908)', () => {
  let backend: HttpTestingController;
  let fixture: ComponentFixture<ProjectConversations>;

  async function render(
    rows: ConversationDto[] = [conversationFixture({ projectId: PROJECT_ID })],
    totalCount = rows.length,
  ): Promise<HTMLElement> {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        ProjectConversationsStore,
      ],
    });

    backend = TestBed.inject(HttpTestingController);

    // `ProjectDetail` binds this in the application, so the tab strip's count is right
    // from any tab; here the panel is rendered on its own and the spec plays that part.
    const store = TestBed.inject(ProjectConversationsStore);
    TestBed.runInInjectionContext(() => store.bindProject(() => PROJECT_ID));
    TestBed.tick();
    backend
      .expectOne((request) => request.url === SEARCH_URL)
      .flush(conversationPage(rows, { totalCount, pageSize: PROJECT_CONVERSATIONS_PAGE_SIZE }));
    TestBed.tick();

    fixture = TestBed.createComponent(ProjectConversations);
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  afterEach(() => {
    backend.verify();
  });

  function menuItems(host: HTMLElement): HTMLButtonElement[] {
    return [...host.querySelectorAll<HTMLButtonElement>('[appMenuItem]')];
  }

  async function openRowMenu(host: HTMLElement): Promise<void> {
    host.querySelector<HTMLButtonElement>('.menu__trigger')?.click();
    await fixture.whenStable();
  }

  it('renders frame 4g’s four columns, with no search field and no selection', async () => {
    const host = await render([
      conversationFixture({ name: 'Helios release status', projectId: PROJECT_ID }),
    ]);

    expect(host.querySelector('.project-conversations__name')?.textContent?.trim()).toBe(
      'Helios release status',
    );
    // Updated, not the model: frame 4g's second column is a relative timestamp.
    expect(host.querySelector('.project-conversations__updated')?.textContent?.trim()).toBeTruthy();
    expect(host.querySelector('.project-conversations__star')).not.toBeNull();
    expect(host.querySelector('.menu__trigger')).not.toBeNull();

    // The board draws neither, so neither ships.
    expect(host.querySelector('.search__field')).toBeNull();
    expect(host.querySelector('input[type="checkbox"]')).toBeNull();
  });

  it('offers the five row actions, with Remove always present', async () => {
    // Unlike the sidebar and the library, every row here is in a project by
    // construction, so Remove always has something to act on.
    const host = await render();
    await openRowMenu(host);

    expect(menuItems(host).map((item) => item.textContent?.trim())).toEqual([
      'Rename',
      'Favourite',
      'Move to another project',
      'Remove from project',
      'Delete',
    ]);
    expect(menuItems(host).some((item) => item.hasAttribute('disabled'))).toBe(false);
  });

  it('removes a row from the project through toUpdateBody, and drops it', async () => {
    const row = conversationFixture({ name: 'Cutover comms', projectId: PROJECT_ID });
    const host = await render([row, conversationFixture({ projectId: PROJECT_ID })], 2);
    await openRowMenu(host);

    menuItems(host)
      .find((item) => item.textContent?.includes('Remove from project'))
      ?.click();
    await fixture.whenStable();

    const request = backend.expectOne(PUT_URL);
    // Only projectId changes; the name is echoed from the row the server already has.
    expect(request.request.body).toEqual({ id: row.id, name: 'Cutover comms', projectId: null });
    request.flush({ ...row, projectId: null });
    await fixture.whenStable();

    // The `updated` event evicts it: the row no longer belongs to the list it is in.
    expect(
      TestBed.inject(ProjectConversationsStore)
        .entities()
        .map((c) => c.id),
    ).not.toContain(row.id);
  });

  it('opens the project picker from the row menu', async () => {
    const host = await render();
    await openRowMenu(host);

    const move = menuItems(host).find((item) => item.textContent?.includes('Move to another'));
    expect(move?.getAttribute('aria-haspopup')).toBe('dialog');

    move?.click();
    await fixture.whenStable();

    // The panel reads the root lookup store and never fetches for itself — that list is
    // drained once at sign-in, so opening a picker costs no request.
    backend.expectNone((request) => request.url === `${TEST_API_BASE_URL}/api/projects`);
    expect(host.querySelector('.picker')?.getAttribute('role')).toBe('dialog');
  });

  it('points its empty state at the composer above it', async () => {
    // Frame 4g's caption, verbatim: the call to action is the project composer.
    const host = await render([], 0);

    const empty = host.querySelector('app-empty-state');
    expect(empty?.textContent).toContain('No conversations in this project yet');
    expect(empty?.textContent).toContain('composer above');
    expect(host.querySelector('.menu__trigger')).toBeNull();
  });

  it('states no ceiling, because US-907 landed with it', async () => {
    // The interim criterion required "Showing the 500 most recent conversations" above
    // the list. One filtered request means there is nothing to disclaim.
    const host = await render();

    expect(host.textContent).not.toContain('most recent');
  });

  it('shows Load more only while there is more, and appends the next page', async () => {
    const first = Array.from({ length: PROJECT_CONVERSATIONS_PAGE_SIZE }, () =>
      conversationFixture({ projectId: PROJECT_ID }),
    );
    const host = await render(first, PROJECT_CONVERSATIONS_PAGE_SIZE + 1);

    const more = [...host.querySelectorAll('button')].find((button) =>
      button.textContent?.includes('Load more'),
    );
    expect(more).toBeDefined();

    more?.click();
    await fixture.whenStable();
    backend
      .expectOne((request) => request.url === SEARCH_URL)
      .flush(
        conversationPage([conversationFixture({ projectId: PROJECT_ID })], {
          totalCount: PROJECT_CONVERSATIONS_PAGE_SIZE + 1,
          pageSize: PROJECT_CONVERSATIONS_PAGE_SIZE,
          skip: PROJECT_CONVERSATIONS_PAGE_SIZE,
        }),
      );
    await fixture.whenStable();

    // It disappears rather than rendering disabled: a disabled control still reads as
    // "there is more, but not for you".
    expect(
      [...host.querySelectorAll('button')].some((button) =>
        button.textContent?.includes('Load more'),
      ),
    ).toBe(false);
  });

  it('replaces the list with an error panel when the query fails', async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        ProjectConversationsStore,
      ],
    });
    backend = TestBed.inject(HttpTestingController);

    const store = TestBed.inject(ProjectConversationsStore);
    TestBed.runInInjectionContext(() => store.bindProject(() => PROJECT_ID));
    TestBed.tick();
    backend
      .expectOne((request) => request.url === SEARCH_URL)
      .flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
        status: 500,
        statusText: 'Internal Server Error',
      });
    TestBed.tick();

    fixture = TestBed.createComponent(ProjectConversations);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;

    // The rows on screen would belong to a query that failed, so the panel replaces
    // the list rather than sitting beside it.
    expect(host.querySelector('app-error-panel')).not.toBeNull();
    expect(host.querySelector('app-data-table')).toBeNull();
  });

  it('leaves a row alone while its own action is in flight', async () => {
    const row = conversationFixture({ projectId: PROJECT_ID });
    const host = await render([row]);

    const actions = TestBed.inject(ConversationActionsStore);
    actions.toggleFavorite(row);
    const favorite = backend.expectOne(`${TEST_API_BASE_URL}/api/conversations/${row.id}/favorite`);
    await fixture.whenStable();

    await openRowMenu(host);
    // aria-disabled, never the native attribute, which would blur the item the instant
    // it is pressed and strand the panel's roving focus.
    expect(menuItems(host).every((item) => item.getAttribute('aria-disabled') === 'true')).toBe(
      true,
    );

    menuItems(host)
      .find((item) => item.textContent?.includes('Remove from project'))
      ?.click();
    await fixture.whenStable();

    backend.expectNone(PUT_URL);
    favorite.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('keeps a starred row, whose updated event is the app’s one synthesized payload', async () => {
    // The favourite endpoint answers 204 with no body, so `ConversationActionsStore`
    // builds `{ ...target, isFavorite }` itself — the only `updated` payload the app does
    // not take from the server. This panel evicts on any payload whose projectId differs
    // from the route's, so a synthesis that lost projectId would make starring a row
    // delete it from the list.
    const row = conversationFixture({ projectId: PROJECT_ID, isFavorite: false });
    const host = await render([row]);

    host.querySelector<HTMLButtonElement>('.project-conversations__star')?.click();
    await fixture.whenStable();

    backend
      .expectOne(`${TEST_API_BASE_URL}/api/conversations/${row.id}/favorite`)
      .flush(null, { status: 204, statusText: 'No Content' });
    await fixture.whenStable();

    const entities = TestBed.inject(ProjectConversationsStore).entities();
    expect(entities.map((c) => c.id)).toEqual([row.id]);
    expect(entities[0]?.isFavorite).toBe(true);
  });

  it('recovers focus when a row evicts itself', async () => {
    // Remove-from-project destroys the kebab that `Menu.close()` has already focused,
    // optimistically, before the server confirmed. Without the fixup focus lands on a
    // detached node and falls to <body>.
    const row = conversationFixture({ projectId: PROJECT_ID });
    const survivor = conversationFixture({ projectId: PROJECT_ID });
    const host = await render([row, survivor], 2);
    document.body.append(host);

    await openRowMenu(host);
    menuItems(host)
      .find((item) => item.textContent?.includes('Remove from project'))
      ?.click();
    await fixture.whenStable();

    backend.expectOne(PUT_URL).flush({ ...row, projectId: null });
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(document.activeElement).toBe(host.querySelector('.project-conversations__name'));
    host.remove();
  });
});
