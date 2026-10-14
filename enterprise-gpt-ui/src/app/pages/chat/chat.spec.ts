import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  Router,
  RouterOutlet,
  UrlSegment,
  provideRouter,
  withComponentInputBinding,
} from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Dispatcher } from '@ngrx/signals/events';
import {
  getAllByRole,
  getByRole,
  getByText,
  queryByRole,
  queryByText,
  within,
} from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CHAT_ROLE, ConversationDetailDto } from '@shared/models/api/conversation.model';
import { TokenService } from '@core/auth/token-service';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { conversationEvents } from '@core/events/conversation-events';
import { turnEvents } from '@core/events/turn-events';
import { CONVERSATION_ID_PARAM, chatMatcher } from '@core/navigation/chat-matcher';
import { ShellChrome } from '@core/ui/shell-chrome-service';
import { STREAM_FETCH } from '@services/conversations/stream-fetch.token';
import { ConversationActionsStore } from '@state/conversation-actions-store';
import { UploadStore } from '@state/documents/upload-store';
import { TurnSettingsStore } from '@state/turn-settings-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpFixture, modelFixture } from '@testing/catalog';
import {
  conversationDetailFixture,
  conversationFixture,
  conversationPage,
  messageFixture,
} from '@testing/conversations';
import { NARROW_VIEWPORT, resetMediaQueries, setMediaQuery } from '@testing/media-query';
import { iconOf, menuItems, openMenu } from '@testing/overlays';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { streamingResponse } from '@testing/stream-frames';
import { FakeUploadXhrQueue, provideFakeUploadXhr } from '@testing/upload-xhr';
import { Chat } from './chat';

const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;
const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;
const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;
const CREATE_URL = `${TEST_API_BASE_URL}/api/conversations`;

function segments(...paths: string[]): UrlSegment[] {
  return paths.map((path) => new UrlSegment(path, {}));
}

describe('chatMatcher', () => {
  it('matches the bare chat route and consumes only its own segment', () => {
    const result = chatMatcher(segments('chat'));

    expect(result?.consumed).toHaveLength(1);
    expect(result?.posParams).toBeUndefined();
  });

  it('matches a conversation and posts its id', () => {
    const id = '0f8fad5b-d9cb-469f-a165-70867728950e';
    const result = chatMatcher(segments('chat', id));

    expect(result?.consumed).toHaveLength(2);
    expect(result?.posParams?.[CONVERSATION_ID_PARAM]?.path).toBe(id);
  });

  it('refuses a second segment that is not a conversation id', () => {
    expect(chatMatcher(segments('chat', 'garbage'))).toBeNull();
  });

  it('refuses anything deeper, and anything that is not chat', () => {
    expect(
      chatMatcher(segments('chat', '0f8fad5b-d9cb-469f-a165-70867728950e', 'extra')),
    ).toBeNull();
    expect(chatMatcher(segments('projects'))).toBeNull();
    expect(chatMatcher(segments())).toBeNull();
  });
});

@Component({
  selector: 'app-test-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
class TestHost {}

describe('Chat', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;
  let streamFetch: ReturnType<typeof vi.fn>;
  let uploadXhr: FakeUploadXhrQueue;

  const conversation = conversationFixture({ name: 'Helios 2.4 release status' });

  beforeEach(async () => {
    // Never resolves, so a sent turn stays in its thinking state unless a test swaps it.
    streamFetch = vi.fn(() => new Promise<Response>(() => {}));
    uploadXhr = new FakeUploadXhrQueue();
    // Or a width set by one test leaks into every later one.
    resetMediaQueries();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        // Pinned off: diagrams or math would load libraries jsdom cannot run.
        provideTestAppConfig({ features: { diagrams: false, math: false, rawStreamCodec: false } }),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: STREAM_FETCH, useValue: streamFetch },
        provideFakeUploadXhr(uploadXhr),
        { provide: TokenService, useValue: { getToken: vi.fn().mockResolvedValue('token-1') } },
        provideRouter(
          [
            { matcher: chatMatcher, component: Chat },
            { path: '**', component: TestHost },
          ],
          withComponentInputBinding(),
        ),
        provideLocationMocks(),
        // Optional in `Chat`; provided so the navbar slot is assertable.
        ShellChrome,
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => {
    // Drain the transcript replay reads unanswered; `turn-store.spec.ts` covers the replay.
    backend.match((request) => request.url.endsWith('/messages'));
    backend.match((request) => request.url.includes('file-extensions'));
    backend.verify();
  });

  function detailUrl(id: string): string {
    return `${TEST_API_BASE_URL}/api/conversations/${id}`;
  }

  function element(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  /** The page's one level-one heading: the conversation name, or the landing prompt. */
  function title(): HTMLElement {
    return getByRole(element(), 'heading', { level: 1 });
  }

  function kebab(): HTMLButtonElement | null {
    return queryByRole<HTMLButtonElement>(element(), 'button', { name: /^Actions for / });
  }

  async function openKebab(): Promise<HTMLElement> {
    return openMenu(harness.fixture, /^Actions for /, element());
  }

  /** Named for what pressing it does, so "Unfavourite …" means it is on. */
  function star(): HTMLButtonElement | null {
    return queryByRole<HTMLButtonElement>(element(), 'button', { name: /favourite /i });
  }

  function expectStarOn(): void {
    expect(star()?.getAttribute('aria-label')).toMatch(/^Unfavourite /);
    expect(iconOf(star()!)).toBe('bi-star-fill');
  }

  function prompt(): HTMLTextAreaElement {
    return getByRole<HTMLTextAreaElement>(element(), 'textbox', { name: 'Message Enterprise GPT' });
  }

  function suggestions(): HTMLElement | null {
    return queryByRole(element(), 'group', { name: 'Suggested prompts' });
  }

  it('retires a finished attachment chip once its turn is done', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(detailUrl(conversation.id))
      .flush(conversationDetailFixture({ ...conversation }));
    await harness.fixture.whenStable();

    const uploads = harness.routeDebugElement!.injector.get(UploadStore);
    uploads.attach([new File(['x'], 'notes.txt', { type: 'text/plain' })]);
    // Files wait for a send; this stands in for one.
    uploads.startPending();
    await harness.fixture.whenStable();

    uploadXhr.last.respond(202, { id: 'job-1' });
    await harness.fixture.whenStable();

    // `UploadStatusClient` waits out its first staged delay before reading.
    await new Promise((resolve) => setTimeout(resolve, 600));
    backend
      .expectOne((candidate) => candidate.url.includes('upload-status'))
      .flush({
        id: 'job-1',
        state: 'Succeeded',
        status: 'Processed',
        progress: 100,
        message: null,
        completedUnits: null,
        totalUnits: null,
        documentId: '9c8b7a65-4321-4fed-8cba-0987654321fe',
        errorMessage: null,
        updatedAt: '2026-08-14T09:14:23.117+00:00',
      });
    await harness.fixture.whenStable();

    expect(uploads.attachments()).toHaveLength(0);
    // Retiring is not retracting: a DELETE belongs to a cancel.
    backend.expectNone((candidate) => candidate.method === 'DELETE');
  });

  it('renders the empty state with no header bar on the bare chat route', async () => {
    await harness.navigateByUrl('/chat', Chat);

    expect(element().querySelector('header')).toBeNull();
    expect(title().textContent).toContain('How can I help you today?');
    expect(element().querySelector('app-brand-logo')).not.toBeNull();
  });

  it('creates nothing when the empty state opens', async () => {
    await harness.navigateByUrl('/chat', Chat);

    // No POST until the first prompt.
    backend.verify();
  });

  it('shows the conversation name in a header once one is open', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);

    backend
      .expectOne(detailUrl(conversation.id))
      .flush(conversationDetailFixture({ ...conversation }));
    await harness.fixture.whenStable();

    // Only one h1 on the screen: the empty state steps down while a conversation is open.
    expect(title().textContent).toContain('Helios 2.4 release status');
  });

  describe('the mobile navbar slot', () => {
    async function openConversation(): Promise<void> {
      await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
      backend
        .expectOne(detailUrl(conversation.id))
        .flush(conversationDetailFixture({ ...conversation }));
      await harness.fixture.whenStable();
    }

    it('renders the star and kebab in the header, and publishes them for the navbar', async () => {
      await openConversation();

      // By name: the composer renders two menus of its own.
      const header = element().querySelector('header')!;
      expect(getAllByRole(element(), 'button', { name: /^Actions for / })).toHaveLength(1);
      expect(within(header).getByRole('button', { name: /^Actions for / })).not.toBeNull();
      expect(within(header).getByRole('button', { name: /favourite /i })).not.toBeNull();
      expect(TestBed.inject(ShellChrome).state().actions).not.toBeNull();
    });

    it('gives the header up below 768px and renders the controls only in the navbar', async () => {
      setMediaQuery(NARROW_VIEWPORT, true);
      await openConversation();

      expect(kebab()).toBeNull();
      // The star too, the template's other half.
      expect(star()).toBeNull();
      expect(TestBed.inject(ShellChrome).state().actions).not.toBeNull();
    });

    it('keeps the level-one heading at every width', async () => {
      setMediaQuery(NARROW_VIEWPORT, true);
      await openConversation();

      // Hidden, not removed: the navbar shows the name, and the page still needs its heading.
      expect(title().textContent).toContain('Helios 2.4 release status');
      expect(title().classList).toContain('visually-hidden');
    });

    it('clears the slot on destroy, so the kebab does not outlive the route', async () => {
      await openConversation();
      const chrome = TestBed.inject(ShellChrome);
      expect(chrome.state().actions).not.toBeNull();

      harness.fixture.destroy();

      expect(chrome.state().actions).toBeNull();
      expect(chrome.state().title).toBeNull();
    });
  });

  it('borrows the name from the sidebar list while the detail is in flight', async () => {
    const list = TestBed.inject(ConversationListStore);
    list.ensureLoaded();
    TestBed.tick();
    backend
      .expectOne((request) => request.url === SEARCH_URL)
      .flush(conversationPage([conversation]));
    TestBed.tick();

    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);

    expect(title().textContent).toContain('Helios 2.4 release status');

    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();
  });

  it('keeps the component instance across opening and closing a conversation', async () => {
    const first = await harness.navigateByUrl('/chat', Chat);

    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    const second = harness.routeDebugElement?.componentInstance;
    expect(second).toBe(first);
  });

  it('closes the conversation and cancels its request when the id goes away', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    const request = backend.expectOne(detailUrl(conversation.id));

    await harness.navigateByUrl('/chat', Chat);

    expect(request.cancelled).toBe(true);
    expect(element().querySelector('header')).toBeNull();
    expect(title().textContent).toContain('How can I help you today?');
  });

  it('explains a conversation that does not exist, and offers no Retry', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(detailUrl(conversation.id))
      .flush(PROBLEM_FIXTURES.resourceNotFound, { status: 404, statusText: 'Not Found' });
    await harness.fixture.whenStable();

    const panel = element().querySelector('app-error-panel');
    expect(panel).not.toBeNull();
    expect(panel?.textContent).toContain(PROBLEM_FIXTURES.resourceNotFound.traceId);
    expect(panel?.querySelector('button')).toBeNull();
  });

  it('offers a Retry for a failure that could go the other way', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(detailUrl(conversation.id))
      .flush('', { status: 503, statusText: 'Service Unavailable' });
    await harness.fixture.whenStable();

    element().querySelector<HTMLButtonElement>('app-error-panel button')?.click();
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    expect(element().querySelector('app-error-panel')).toBeNull();
    expect(title().textContent).toContain('Helios 2.4');
  });

  it('re-renders the header when a rename is announced for the open conversation', async () => {
    // The list never held this row, so the event's DTO alone must carry the rename.
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    TestBed.inject(Dispatcher).dispatch(
      conversationEvents.updated({ ...conversation, name: 'Renamed elsewhere' }),
    );
    await harness.fixture.whenStable();

    expect(title().textContent).toContain('Renamed elsewhere');
  });

  it('ignores an update announced for a different conversation', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    TestBed.inject(Dispatcher).dispatch(
      conversationEvents.updated(conversationFixture({ name: 'Someone else’s rename' })),
    );
    await harness.fixture.whenStable();

    expect(title().textContent).toContain('Helios 2.4 release status');
  });

  it('navigates to the empty chat only after a delete of the open conversation completes', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    const actions = TestBed.inject(ConversationActionsStore);
    actions.beginDelete(conversation);
    actions.confirmDelete();
    await harness.fixture.whenStable();

    // Navigation waits for the 204: a failed delete restores the row the user is still on.
    expect(TestBed.inject(Router).url).toBe(`/chat/${conversation.id}`);

    backend
      .expectOne({ method: 'DELETE', url: detailUrl(conversation.id) })
      .flush(null, { status: 204, statusText: 'No Content' });
    await harness.fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/chat');
    expect(element().querySelector('header')).toBeNull();
    expect(title().textContent).toContain('How can I help you today?');
  });

  it('moves fallen-through focus to the main landmark after a header-initiated delete', async () => {
    const main = document.createElement('main');
    main.id = 'main-content';
    main.tabIndex = -1;
    document.body.appendChild(main);

    try {
      await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
      backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
      await harness.fixture.whenStable();

      const trigger = kebab()!;
      trigger.focus();
      expect(document.activeElement).toBe(trigger);

      const actions = TestBed.inject(ConversationActionsStore);
      actions.beginDelete(conversation);
      actions.confirmDelete();
      backend
        .expectOne({ method: 'DELETE', url: detailUrl(conversation.id) })
        .flush(null, { status: 204, statusText: 'No Content' });
      await harness.fixture.whenStable();

      expect(TestBed.inject(Router).url).toBe('/chat');
      expect(document.activeElement).toBe(main);
    } finally {
      main.remove();
    }
  });

  it('stays put when the deletion announced is someone else’s', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    TestBed.inject(Dispatcher).dispatch(conversationEvents.deleted(conversationFixture().id));
    await harness.fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe(`/chat/${conversation.id}`);
    expect(title().textContent).toContain('Helios 2.4 release status');
  });

  it('offers rename and delete from the header kebab, routed through the shared store', async () => {
    // A distinctive projectId proves the kebab hands over the detail DTO, which rename echoes.
    const projectId = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(detailUrl(conversation.id))
      .flush(conversationDetailFixture({ ...conversation, projectId }));
    await harness.fixture.whenStable();

    const menu = await openKebab();

    const items = menuItems(menu);
    const danger = items.filter((item) => item.classList.contains('text-danger-emphasis'));
    expect(danger).toHaveLength(1);
    expect(danger[0]?.textContent).toContain('Delete');

    getByRole(menu, 'menuitem', { name: 'Rename' }).click();
    await harness.fixture.whenStable();

    expect(TestBed.inject(ConversationActionsStore).renameTarget()?.projectId).toBe(projectId);
  });

  it('offers the project moves from the header kebab, and removes in one request', async () => {
    const projectId = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(detailUrl(conversation.id))
      .flush(conversationDetailFixture({ ...conversation, projectId }));
    await harness.fixture.whenStable();

    const items = menuItems(await openKebab());
    expect(items.map((item) => item.textContent?.trim())).toEqual([
      'Rename',
      'Move to project',
      'Remove from project',
      'Delete',
    ]);
    const move = items.find((item) => item.textContent?.includes('Move to project'));
    expect(move?.getAttribute('aria-haspopup')).toBe('dialog');

    items.find((item) => item.textContent?.includes('Remove from project'))?.click();
    await harness.fixture.whenStable();

    // Removal is an explicit null, not an omission.
    const request = backend.expectOne(`${TEST_API_BASE_URL}/api/conversations`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      id: conversation.id,
      name: conversation.name,
      projectId: null,
    });
    request.flush({ ...conversation, projectId: null });
  });

  it('withholds Remove from project on a standalone conversation', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(detailUrl(conversation.id))
      .flush(conversationDetailFixture({ ...conversation, projectId: null }));
    await harness.fixture.whenStable();

    const labels = menuItems(await openKebab()).map((item) => item.textContent?.trim());
    expect(labels).not.toContain('Remove from project');
    expect(labels).toContain('Move to project');
  });

  it('disables the header kebab items while the conversation’s own action is in flight', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    TestBed.inject(ConversationListStore).setRowPending(conversation.id, true);
    await harness.fixture.whenStable();

    // Still focusable, so the menu's roving focus keeps working.
    const items = menuItems(await openKebab()) as HTMLButtonElement[];
    expect(items.length).toBeGreaterThan(0);
    expect(items.every((item) => item.getAttribute('aria-disabled') === 'true')).toBe(true);
    expect(items.every((item) => !item.disabled)).toBe(true);

    items[0]?.click();
    await harness.fixture.whenStable();
    expect(TestBed.inject(ConversationActionsStore).renameTarget()).toBeNull();
  });

  it('withholds the kebab for a deep link until the detail supplies a full DTO', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);

    expect(kebab()).toBeNull();

    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    expect(kebab()).not.toBeNull();
  });

  it('fills the header star from the sidebar copy of a favourited conversation', async () => {
    const list = TestBed.inject(ConversationListStore);
    list.ensureLoaded();
    TestBed.tick();
    backend
      .expectOne((request) => request.url === SEARCH_URL)
      .flush(conversationPage([{ ...conversation, isFavorite: true }]));
    TestBed.tick();

    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(detailUrl(conversation.id))
      .flush(conversationDetailFixture({ ...conversation, isFavorite: true }));
    await harness.fixture.whenStable();

    expectStarOn();
    expect(star()?.getAttribute('aria-label')).toBe('Unfavourite Helios 2.4 release status');
  });

  it('flips the header star and the sidebar row together, before the server answers', async () => {
    // The header reads the list's optimistically patched copy.
    const list = TestBed.inject(ConversationListStore);
    list.ensureLoaded();
    TestBed.tick();
    backend
      .expectOne((request) => request.url === SEARCH_URL)
      .flush(conversationPage([conversation]));
    TestBed.tick();

    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    expect(iconOf(star()!)).toBe('bi-star');
    star()?.click();
    await harness.fixture.whenStable();

    expectStarOn();
    expect(list.entities()[0]?.isFavorite).toBe(true);

    const request = backend.expectOne({
      method: 'PUT',
      url: `${detailUrl(conversation.id)}/favorite`,
    });
    expect(request.request.body).toEqual({ isFavorite: true });
    request.flush(null, { status: 204, statusText: 'No Content' });
    await harness.fixture.whenStable();

    expectStarOn();
  });

  it('disables the header star while the conversation’s own action is in flight', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    TestBed.inject(ConversationListStore).setRowPending(conversation.id, true);
    await harness.fixture.whenStable();

    // Native disabled, unlike the kebab's items: the star is not in a roving-focus panel.
    expect(star()?.disabled).toBe(true);
  });

  it('fills the star from the detail DTO for a deep link the list never held', async () => {
    // Unlike GET {id}/messages, the detail reports isFavorite truthfully.
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);

    expect(star()).toBeNull();

    backend
      .expectOne(detailUrl(conversation.id))
      .flush(conversationDetailFixture({ ...conversation, isFavorite: true }));
    await harness.fixture.whenStable();

    expectStarOn();
  });

  it('follows a favourite announced for the open conversation', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    TestBed.inject(Dispatcher).dispatch(
      conversationEvents.updated({ ...conversation, isFavorite: true }),
    );
    await harness.fixture.whenStable();

    expectStarOn();
  });

  it('renders the composer on the empty route and on an open conversation alike', async () => {
    await harness.navigateByUrl('/chat', Chat);
    expect(element().querySelector('app-composer')).not.toBeNull();

    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    expect(element().querySelector('app-composer')).not.toBeNull();
  });

  it('withholds the composer from a conversation that failed to open', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(detailUrl(conversation.id))
      .flush(PROBLEM_FIXTURES.resourceNotFound, { status: 404, statusText: 'Not Found' });
    await harness.fixture.whenStable();

    expect(element().querySelector('app-composer')).toBeNull();
  });

  it('offers the four suggested prompts on the landing screen only', async () => {
    await harness.navigateByUrl('/chat', Chat);

    const labels = within(suggestions()!)
      .getAllByRole('button')
      .map((chip) => chip.textContent?.trim() ?? '');
    expect(labels).toEqual([
      'Summarize a document',
      'Draft a status update',
      'Draft an email',
      'Generate a document',
    ]);

    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    expect(suggestions()).toBeNull();
  });

  it('seeds the composer when a suggested prompt is chosen, creating nothing', async () => {
    await harness.navigateByUrl('/chat', Chat);

    within(suggestions()!).getByRole('button', { name: 'Summarize a document' }).click();
    await harness.fixture.whenStable();

    expect(prompt().value).toBe('Summarize a document');
    expect(document.activeElement).toBe(prompt());
    backend.expectNone(CREATE_URL);
  });

  it('mounts the turn status region outside the transcript, empty, from first render', async () => {
    await harness.navigateByUrl('/chat', Chat);

    // Present and empty before any turn: a region created with its content may not announce.
    const regions = [...element().querySelectorAll('[role="status"]')];
    expect(regions.length).toBeGreaterThan(0);
    expect(regions.map((node) => node.textContent?.trim())).toEqual(regions.map(() => ''));
    expect(element().querySelector('app-transcript')).toBeNull();

    streamFetch.mockImplementation((_input: unknown, init?: RequestInit) => {
      const handle = streamingResponse();
      handle.abortOn(init?.signal);
      return Promise.resolve(handle.response);
    });

    const models = TestBed.inject(ModelCatalogStore).ensureLoaded();
    backend.expectOne(MODELS_URL).flush([modelFixture({ isDefault: true })]);
    await models;
    await harness.fixture.whenStable();

    prompt().value = 'First prompt';
    prompt().dispatchEvent(new Event('input', { bubbles: true }));
    await harness.fixture.whenStable();
    getByRole(element(), 'button', { name: 'Send' }).click();
    await harness.fixture.whenStable();
    const created = conversationFixture();
    backend.expectOne(CREATE_URL).flush(created);
    await harness.fixture.whenStable();
    // The replaceUrl navigation opens the conversation; unanswered, it fails `verify()`.
    backend.expectOne(detailUrl(created.id)).flush(conversationDetailFixture(created));
    await harness.fixture.whenStable();

    // With the transcript mounted, the separation below cannot pass on absence.
    const transcript = element().querySelector('app-transcript');
    expect(transcript).not.toBeNull();
    expect(transcript?.getAttribute('aria-busy')).toBe('true');

    const speaking = [...element().querySelectorAll('[role="status"]')].filter(
      (node) => (node.textContent?.trim() ?? '') !== '',
    );
    expect(speaking).toHaveLength(1);

    // Outside the aria-busy transcript and the scroll container. The store spec pins the
    // other `turnStatus` strings; this asserts only the wiring.
    const live = speaking[0]!;
    expect(live.textContent?.trim()).toBe('Waiting for a response');
    expect(transcript?.contains(live)).toBe(false);
    expect(element().querySelector('.chat__body')?.contains(live)).toBe(false);
  });

  it('runs the first send end to end: create, prepend, replace the URL, stream', async () => {
    streamFetch.mockImplementation((_input: unknown, init?: RequestInit) => {
      const handle = streamingResponse();
      handle.abortOn(init?.signal);
      return Promise.resolve(handle.response);
    });
    await harness.navigateByUrl('/chat', Chat);

    const models = TestBed.inject(ModelCatalogStore).ensureLoaded();
    backend.expectOne(MODELS_URL).flush([modelFixture({ isDefault: true })]);
    await models;
    await harness.fixture.whenStable();

    prompt().value = 'First prompt';
    prompt().dispatchEvent(new Event('input', { bubbles: true }));
    await harness.fixture.whenStable();
    getByRole(element(), 'button', { name: 'Send' }).click();
    await harness.fixture.whenStable();

    const created = conversationFixture({ name: 'New conversation' });
    const create = backend.expectOne(CREATE_URL);
    expect(create.request.body).toEqual({ projectId: null });
    create.flush(created);
    await harness.fixture.whenStable();

    // The route input caught up, and the equality guard kept the live turn alive.
    expect(TestBed.inject(Router).url).toBe(`/chat/${created.id}`);
    expect(TestBed.inject(ConversationListStore).entities()[0]?.id).toBe(created.id);
    expect(streamFetch).toHaveBeenCalledTimes(1);

    expect(element().querySelector('.transcript__bubble')?.textContent).toBe('First prompt');
    expect(element().querySelector('app-ridgeline')).not.toBeNull();
    expect(element().querySelector('app-transcript')?.getAttribute('aria-busy')).toBe('true');
    expect(suggestions()).toBeNull();

    backend.expectOne(detailUrl(created.id)).flush(conversationDetailFixture(created));
    await harness.fixture.whenStable();
    expect(title().textContent).toContain('New conversation');
  });

  describe('picking up the name the server generates', () => {
    async function openAndComplete(wasFirstTurn: boolean): Promise<void> {
      const list = TestBed.inject(ConversationListStore);
      list.ensureLoaded();
      TestBed.tick();
      backend
        .expectOne((request) => request.url === SEARCH_URL)
        .flush(
          conversationPage([conversationFixture({ ...conversation, name: 'New conversation' })]),
        );
      TestBed.tick();

      await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
      backend
        .expectOne(detailUrl(conversation.id))
        .flush(conversationDetailFixture({ ...conversation, name: 'New conversation' }));
      backend
        .expectOne(`${detailUrl(conversation.id)}/messages`)
        .flush({ id: conversation.id, name: 'New conversation', messages: [] });
      await harness.fixture.whenStable();

      TestBed.inject(Dispatcher).dispatch(
        turnEvents.completed({ conversationId: conversation.id, wasFirstTurn }),
      );
      await harness.fixture.whenStable();
    }

    it('refetches after the first turn and shows the generated name everywhere', async () => {
      await openAndComplete(true);

      backend
        .expectOne(detailUrl(conversation.id))
        .flush(conversationDetailFixture({ ...conversation, name: 'Helios 2.4 release status' }));
      await harness.fixture.whenStable();

      expect(title().textContent).toContain('Helios 2.4 release status');
      expect(TestBed.inject(ConversationListStore).entityMap()[conversation.id]?.name).toBe(
        'Helios 2.4 release status',
      );
    });

    it('leaves the header showing the old name while the refetch is in flight', async () => {
      await openAndComplete(true);

      // Refreshing through `open` would clear the detail and flash the sidebar's copy back.
      expect(title().textContent).toContain('New conversation');
      backend
        .expectOne(detailUrl(conversation.id))
        .flush(conversationDetailFixture({ ...conversation }));
    });

    it('issues nothing after a later turn, whose name cannot have changed', async () => {
      await openAndComplete(false);

      backend.expectNone(detailUrl(conversation.id));
    });
  });

  describe('the header download control', () => {
    /** Opens a conversation with a transcript, which is what a download needs. */
    async function openWithTranscript(
      messages: { text: string; role: number }[] = [{ text: 'Hello', role: 3 }],
    ): Promise<void> {
      await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
      backend
        .expectOne(detailUrl(conversation.id))
        .flush(conversationDetailFixture({ ...conversation }));
      backend
        .expectOne(`${detailUrl(conversation.id)}/messages`)
        .flush({ id: conversation.id, name: conversation.name, messages });
      await harness.fixture.whenStable();
    }

    /** Deliberately two: the header and the composer offer the same shared menu. */
    it('renders the control in the header and in the composer, from one declaration each', async () => {
      await openWithTranscript();

      expect(element().querySelectorAll('app-conversation-download-menu')).toHaveLength(2);
      expect(element().querySelectorAll('header app-conversation-download-menu')).toHaveLength(1);
      expect(
        element().querySelectorAll('app-composer app-conversation-download-menu'),
      ).toHaveLength(1);
    });

    it('is absent for a conversation with nothing in it', async () => {
      await openWithTranscript([]);

      expect(element().querySelector('app-conversation-download-menu')).toBeNull();
      // The star and the kebab stay: they act on the row, which exists from creation.
      expect(star()).not.toBeNull();
      expect(kebab()).not.toBeNull();
    });
  });

  describe('resuming a conversation', () => {
    async function openWith(detail: Partial<ConversationDetailDto>): Promise<void> {
      await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
      backend
        .expectOne(detailUrl(conversation.id))
        .flush(conversationDetailFixture({ ...conversation, ...detail }));
      backend
        .expectOne(`${detailUrl(conversation.id)}/messages`)
        .flush({ id: conversation.id, name: conversation.name, messages: [] });
      await harness.fixture.whenStable();
    }

    it('restores the model and the tool servers the conversation last used', async () => {
      const previous = modelFixture({ name: 'Claude Sonnet' });
      const server = mcpFixture({ name: 'Jira Cloud' });
      const catalogs = TestBed.inject(ModelCatalogStore).ensureLoaded();
      TestBed.inject(McpCatalogStore).ensureLoaded();
      backend.expectOne(MODELS_URL).flush([modelFixture({ isDefault: true }), previous]);
      backend.expectOne(MCPS_URL).flush([server, mcpFixture()]);
      await catalogs;

      await openWith({ modelId: previous.id, mcpServerIds: [server.id] });

      const settings = TestBed.inject(TurnSettingsStore);
      expect(settings.selectedModel()?.id).toBe(previous.id);
      expect(settings.effectiveMcpServerIds()).toEqual([server.id]);
      expect(getByRole(element(), 'button', { name: /^Model:/ }).textContent).toContain(
        'Claude Sonnet',
      );
      expect(getByRole(element(), 'button', { name: /^Tool servers:/ }).textContent).toContain(
        '1 Tool',
      );
    });

    it('keeps the selection the user made for a conversation that has run no turn', async () => {
      const chosen = modelFixture({ name: 'Claude Sonnet' });
      const server = mcpFixture({ name: 'Jira Cloud' });
      const catalogs = TestBed.inject(ModelCatalogStore).ensureLoaded();
      TestBed.inject(McpCatalogStore).ensureLoaded();
      backend.expectOne(MODELS_URL).flush([modelFixture({ isDefault: true }), chosen]);
      backend.expectOne(MCPS_URL).flush([server]);
      await catalogs;

      const settings = TestBed.inject(TurnSettingsStore);
      settings.selectModel(chosen.id);
      settings.toggleMcpServer(server.id);

      // A conversation whose first turn has not finished reports both empty; seeding from
      // that would undo the picks the running turn was sent with.
      await openWith({ modelId: null, mcpServerIds: [] });

      expect(settings.selectedModel()?.id).toBe(chosen.id);
      expect(settings.effectiveMcpServerIds()).toEqual([server.id]);
    });

    it('falls back to the default model and says so when the last one is gone', async () => {
      const fallback = modelFixture({ name: 'GPT-5', isDefault: true });
      const catalog = TestBed.inject(ModelCatalogStore).ensureLoaded();
      backend.expectOne(MODELS_URL).flush([fallback]);
      await catalog;

      await openWith({ modelId: '00000000-dead-4bee-8ccc-dddddddddddd', mcpServerIds: [] });

      const settings = TestBed.inject(TurnSettingsStore);
      expect(settings.selectedModel()?.id).toBe(fallback.id);
      expect(
        getByText(element(), /^The model this conversation last used is unavailable/).getAttribute(
          'role',
        ),
      ).toBe('status');
    });

    it('says nothing while the catalog is still loading', async () => {
      // An empty catalog means not loaded yet, never that the model was deactivated.
      await openWith({ modelId: '00000000-dead-4bee-8ccc-dddddddddddd', mcpServerIds: [] });

      expect(TestBed.inject(TurnSettingsStore).restoredModelUnavailable()).toBe(false);
      expect(queryByText(element(), /last used is unavailable/)).toBeNull();
    });

    it('replays the stored messages into the transcript', async () => {
      await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
      backend
        .expectOne(detailUrl(conversation.id))
        .flush(conversationDetailFixture({ ...conversation }));

      // The skeleton, not a flash of the landing screen, covers the round trip.
      await harness.fixture.whenStable();
      expect(element().querySelector('app-transcript app-skeleton')).not.toBeNull();
      expect(element().querySelector('app-chat-empty-state')).toBeNull();
      // The skeleton is aria-hidden; aria-busy is how assistive tech perceives the wait.
      expect(element().querySelector('app-transcript')?.getAttribute('aria-busy')).toBe('true');

      backend.expectOne(`${detailUrl(conversation.id)}/messages`).flush({
        id: conversation.id,
        name: conversation.name,
        messages: [
          messageFixture({ text: 'What is the weather?', role: CHAT_ROLE.user }),
          messageFixture({ text: 'It is sunny.', role: CHAT_ROLE.assistant }),
        ],
      });
      await harness.fixture.whenStable();

      expect(element().querySelector('app-transcript app-skeleton')).toBeNull();
      expect(element().querySelector('.transcript__bubble')?.textContent).toBe(
        'What is the weather?',
      );
      expect(element().querySelector('.assistant-turn__md')?.textContent?.trim()).toBe(
        'It is sunny.',
      );
    });

    it('keeps the composer usable when the stored messages cannot be read', async () => {
      await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
      backend
        .expectOne(detailUrl(conversation.id))
        .flush(conversationDetailFixture({ ...conversation }));
      backend
        .expectOne(`${detailUrl(conversation.id)}/messages`)
        .flush({ title: 'Boom' }, { status: 500, statusText: 'Internal Server Error' });
      await harness.fixture.whenStable();

      expect(element().querySelector('app-error-panel')?.textContent).toContain(
        "Earlier messages couldn't load",
      );
      expect(prompt()).not.toBeNull();
    });
  });

  it('keeps the sidebar row in step with the name the server reports', async () => {
    const list = TestBed.inject(ConversationListStore);
    list.ensureLoaded();
    TestBed.tick();
    backend
      .expectOne((request) => request.url === SEARCH_URL)
      .flush(conversationPage([conversation]));
    TestBed.tick();

    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(detailUrl(conversation.id))
      .flush(conversationDetailFixture({ ...conversation, name: 'Named by the server' }));
    await harness.fixture.whenStable();

    expect(list.entities()[0]?.name).toBe('Named by the server');
  });
});
