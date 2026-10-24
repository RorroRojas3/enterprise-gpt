import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { getAllByRole, getByRole, queryByRole } from '@testing-library/dom';
import { afterEach, describe, expect, it } from 'vitest';
import { ConversationDto } from '@core/api/conversation.model';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import {
  PROJECT_CONVERSATIONS_PAGE_SIZE,
  ProjectConversationsStore,
} from '@core/projects/project-conversations-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { menuItems } from '@testing/overlays';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { ProjectConversations } from './project-conversations';

const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;
const PUT_URL = `${TEST_API_BASE_URL}/api/conversations`;
const PROJECT_ID = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';

describe('ProjectConversations', () => {
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

    // `ProjectDetail` binds this in the app; with the panel rendered alone, the spec does.
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

  /** The open row menu's items. */
  function items(host: HTMLElement): HTMLElement[] {
    return menuItems(getByRole(host, 'menu'));
  }

  /** Opens the first row's kebab. */
  async function openRowMenu(host: HTMLElement): Promise<void> {
    getAllByRole(host, 'button', { name: /^Actions for / })[0]!.click();
    await fixture.whenStable();
  }

  it('renders frame 4g’s four columns, with no search field and no selection', async () => {
    const host = await render([
      conversationFixture({ name: 'Helios release status', projectId: PROJECT_ID }),
    ]);

    const row = getAllByRole(host, 'row')[1]!;
    const cells = getAllByRole(row, 'cell');
    expect(cells).toHaveLength(4);
    expect(getByRole(cells[0]!, 'link').textContent?.trim()).toBe('Helios release status');
    expect(cells[1]?.textContent?.trim()).toBeTruthy();
    expect(
      getByRole(cells[2]!, 'button', { name: 'Favourite Helios release status' }),
    ).toBeTruthy();
    expect(
      getByRole(cells[3]!, 'button', { name: 'Actions for Helios release status' }),
    ).toBeTruthy();

    expect(queryByRole(host, 'searchbox')).toBeNull();
    expect(queryByRole(host, 'checkbox')).toBeNull();
  });

  it('offers the five row actions, with Remove always present', async () => {
    const host = await render();
    await openRowMenu(host);

    expect(items(host).map((item) => item.textContent?.trim())).toEqual([
      'Rename',
      'Favourite',
      'Move to another project',
      'Remove from project',
      'Delete',
    ]);
    expect(items(host).some((item) => item.hasAttribute('disabled'))).toBe(false);
  });

  it('removes a row from the project through toUpdateBody, and drops it', async () => {
    const row = conversationFixture({ name: 'Cutover comms', projectId: PROJECT_ID });
    const host = await render([row, conversationFixture({ projectId: PROJECT_ID })], 2);
    await openRowMenu(host);

    items(host)
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

    const move = items(host).find((item) => item.textContent?.includes('Move to another'));
    expect(move?.getAttribute('aria-haspopup')).toBe('dialog');

    move?.click();
    await fixture.whenStable();

    // The picker reads the root lookup store, drained at sign-in, so opening it costs no request.
    backend.expectNone((request) => request.url === `${TEST_API_BASE_URL}/api/projects`);
    expect(getByRole(host, 'dialog', { name: /to a project$/ })).toBeTruthy();
  });

  it('points its empty state at the composer above it', async () => {
    const host = await render([], 0);

    const empty = host.querySelector('app-empty-state');
    expect(empty?.textContent).toContain('No conversations in this project yet');
    expect(empty?.textContent).toContain('composer above');
    expect(queryByRole(host, 'button', { name: /^Actions for / })).toBeNull();
  });

  it('shows no "most recent" ceiling caption', async () => {
    // The server filters by project and Load more pages the rest, so there is no cap to disclaim.
    const host = await render();

    expect(host.textContent).not.toContain('most recent');
  });

  it('shows Load more only while there is more, and appends the next page', async () => {
    const first = Array.from({ length: PROJECT_CONVERSATIONS_PAGE_SIZE }, () =>
      conversationFixture({ projectId: PROJECT_ID }),
    );
    const host = await render(first, PROJECT_CONVERSATIONS_PAGE_SIZE + 1);

    getByRole(host, 'button', { name: 'Load more' }).click();
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
    expect(queryByRole(host, 'button', { name: /^(Load more|Loading…)$/ })).toBeNull();
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
    expect(items(host).every((item) => item.getAttribute('aria-disabled') === 'true')).toBe(true);

    items(host)
      .find((item) => item.textContent?.includes('Remove from project'))
      ?.click();
    await fixture.whenStable();

    backend.expectNone(PUT_URL);
    favorite.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('keeps a starred row, whose updated event is the app’s one synthesized payload', async () => {
    // The favourite endpoint returns no body, so the store synthesizes the `updated` payload;
    // one that lost projectId would evict the starred row from this list.
    const row = conversationFixture({ projectId: PROJECT_ID, isFavorite: false });
    const host = await render([row]);

    getByRole(host, 'button', { name: `Favourite ${row.name}` }).click();
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
    // Removal destroys the kebab `Menu.close()` just focused, so without the fixup focus falls
    // to <body>.
    const row = conversationFixture({ projectId: PROJECT_ID });
    const survivor = conversationFixture({ projectId: PROJECT_ID });
    const host = await render([row, survivor], 2);
    document.body.append(host);

    await openRowMenu(host);
    items(host)
      .find((item) => item.textContent?.includes('Remove from project'))
      ?.click();
    await fixture.whenStable();

    backend.expectOne(PUT_URL).flush({ ...row, projectId: null });
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(document.activeElement).toBe(getByRole(host, 'link', { name: survivor.name }));
    host.remove();
  });
});
