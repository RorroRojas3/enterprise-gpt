import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideLocationMocks } from '@angular/common/testing';
import {
  Router,
  RouterOutlet,
  UrlSegment,
  provideRouter,
  withComponentInputBinding,
} from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ShellChrome } from '@core/ui/shell-chrome';
import { NARROW_VIEWPORT, resetMediaQueries, setMediaQuery } from '@testing/media-query';
import { Dispatcher } from '@ngrx/signals/events';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { TurnSettingsStore } from '@core/chat/turn-settings-store';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { conversationEvents } from '@core/events/conversation-events';
import { turnEvents } from '@core/events/turn-events';
import { TokenService } from '@core/auth/token-service';
import { STREAM_FETCH } from '@core/stream/stream-fetch.token';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpFixture, modelFixture } from '@testing/catalog';
import {
  conversationDetailFixture,
  conversationFixture,
  conversationPage,
  messageFixture,
} from '@testing/conversations';
import { CHAT_ROLE, ConversationDetailDto } from '@domain/api/conversation';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { streamingResponse } from '@testing/stream-frames';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { UploadStore } from '@core/documents/upload-store';
import { FakeUploadXhrQueue, provideFakeUploadXhr } from '@testing/upload-xhr';
import { Chat } from './chat';
import { CONVERSATION_ID_PARAM, chatMatcher } from './chat-route';

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
    // Otherwise `/chat/garbage` matches and the store issues GET conversations/garbage.
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
    // Pending unless a test swaps it: these specs exercise the screen, and an
    // unresolved fetch keeps a sent turn in its thinking state.
    streamFetch = vi.fn(() => new Promise<Response>(() => {}));
    uploadXhr = new FakeUploadXhrQueue();
    // Or a width set by one test decides the branch every later one renders.
    resetMediaQueries();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        // Pinned off rather than inherited from `public/config.json`: this screen
        // mounts the transcript, and a deployment that switches diagrams or math
        // on would otherwise have these specs reach for a library jsdom cannot run.
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
        // The seam US-1403 added. `Chat` injects it `{ optional: true }`, so every
        // other spec in this file runs without one; providing it here is what makes
        // the navbar slot assertable at all.
        ShellChrome,
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => {
    // Opening a conversation also reads its stored transcript (US-410). These
    // specs are about the header, the kebab, and the detail request; `match`
    // takes the replay reads off the pending queue without answering them,
    // rather than repeating a flush at twenty navigation sites. The replay
    // itself is covered in `turn-store.spec.ts`.
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

  it('retires a finished attachment chip once its turn is done', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(detailUrl(conversation.id))
      .flush(conversationDetailFixture({ ...conversation }));
    await harness.fixture.whenStable();

    const uploads = harness.routeDebugElement!.injector.get(UploadStore);
    uploads.attach([new File(['x'], 'notes.txt', { type: 'text/plain' })]);
    // The composer holds its files until a turn is sent, which is what posts them.
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

    // The document is the conversation's now and retrieval reaches it; a chip left on
    // screen would make every later prompt look like it carries an attachment.
    expect(uploads.attachments()).toHaveLength(0);
    // And retiring it must not retract it — that path belongs to a cancel.
    backend.expectNone((candidate) => candidate.method === 'DELETE');
  });

  it('renders the empty state with no header bar on the bare chat route', async () => {
    await harness.navigateByUrl('/chat', Chat);

    // Frame 1a: the header is absent, not rendered empty.
    expect(element().querySelector('.chat__header')).toBeNull();
    expect(element().querySelector('h1')?.textContent).toContain('How can I help you today?');
    expect(element().querySelector('app-brand-logo')).not.toBeNull();
  });

  it('creates nothing when the empty state opens', async () => {
    await harness.navigateByUrl('/chat', Chat);

    // US-303: no POST until the first prompt.
    backend.verify();
  });

  it('shows the conversation name in a header once one is open', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);

    backend
      .expectOne(detailUrl(conversation.id))
      .flush(conversationDetailFixture({ ...conversation }));
    await harness.fixture.whenStable();

    expect(element().querySelector('.chat__title')?.textContent).toContain(
      'Helios 2.4 release status',
    );
    // Only one h1 on the screen: the empty state steps down while a conversation is open.
    expect(element().querySelectorAll('h1')).toHaveLength(1);
  });

  describe('the mobile navbar slot (US-1403)', () => {
    async function openConversation(): Promise<void> {
      await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
      backend
        .expectOne(detailUrl(conversation.id))
        .flush(conversationDetailFixture({ ...conversation }));
      await harness.fixture.whenStable();
    }

    it('renders the star and kebab in the header, and publishes them for the navbar', async () => {
      await openConversation();

      // `.chat__menu` and not a bare `app-menu`: the composer renders two of its own,
      // for the model and tool pickers. Exactly one instantiation of *this* one — a
      // `TemplateRef` rendered in both places would put two of this menu, and two
      // project pickers each with its own open state, in the document at once.
      expect(element().querySelectorAll('.chat__menu')).toHaveLength(1);
      expect(element().querySelector('.chat__header .chat__menu')).not.toBeNull();
      expect(TestBed.inject(ShellChrome).state().actions).not.toBeNull();
    });

    it('gives the header up below 768px and renders the controls only in the navbar', async () => {
      setMediaQuery(NARROW_VIEWPORT, true);
      await openConversation();

      // The bar is gone from the header, so nothing here renders the template — the
      // shell's navbar does, through the reference published above.
      expect(element().querySelector('.chat__header .chat__menu')).toBeNull();
      expect(element().querySelectorAll('.chat__menu')).toHaveLength(0);
      // The star is the other half of the same template; asserting only the kebab would
      // pass for a template that had lost one of them.
      expect(element().querySelectorAll('.chat__star')).toHaveLength(0);
      expect(TestBed.inject(ShellChrome).state().actions).not.toBeNull();
    });

    it('keeps the level-one heading at every width', async () => {
      setMediaQuery(NARROW_VIEWPORT, true);
      await openConversation();

      // Frame 1d shows the brand rather than the conversation name, and the stylesheet
      // clips this rather than hiding it: `display: none` would take the document's only
      // <h1> out of the accessibility tree with it. jsdom applies no stylesheet, so what
      // is assertable here is that the element and its text are still in the DOM.
      const heading = element().querySelector('h1.chat__title');
      expect(heading).not.toBeNull();
      expect(heading?.textContent).toContain('Helios 2.4 release status');
      expect(element().querySelectorAll('h1')).toHaveLength(1);
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

    expect(element().querySelector('.chat__title')?.textContent).toContain(
      'Helios 2.4 release status',
    );

    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();
  });

  it('keeps the component instance across opening and closing a conversation', async () => {
    // The property US-401 depends on: it creates the conversation mid-turn and
    // replaces the URL (navigateByUrl with replaceUrl) while an answer is
    // already streaming — a parameter change, never a remount.
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
    expect(element().querySelector('.chat__header')).toBeNull();
    expect(element().querySelector('h1')?.textContent).toContain('How can I help you today?');
  });

  it('explains a conversation that does not exist, and offers no Retry', async () => {
    // A 404 means the conversation is gone *or* belongs to someone else — the API
    // deliberately does not distinguish the two, and neither becomes false on a second
    // attempt, so a Retry there would be a button that cannot work.
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
    expect(element().querySelector('.chat__title')?.textContent).toContain('Helios 2.4');
  });

  it('re-renders the header when a rename is announced for the open conversation', async () => {
    // US-304: a rename made from the sidebar row must reach the header even when the
    // list never held the row — the event carries the server-confirmed DTO.
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    TestBed.inject(Dispatcher).dispatch(
      conversationEvents.updated({ ...conversation, name: 'Renamed elsewhere' }),
    );
    await harness.fixture.whenStable();

    expect(element().querySelector('.chat__title')?.textContent).toContain('Renamed elsewhere');
  });

  it('ignores an update announced for a different conversation', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    TestBed.inject(Dispatcher).dispatch(
      conversationEvents.updated(conversationFixture({ name: 'Someone else’s rename' })),
    );
    await harness.fixture.whenStable();

    expect(element().querySelector('.chat__title')?.textContent).toContain(
      'Helios 2.4 release status',
    );
  });

  it('navigates to the empty chat only after a delete of the open conversation completes', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    const actions = TestBed.inject(ConversationActionsStore);
    actions.beginDelete(conversation);
    actions.confirmDelete();
    await harness.fixture.whenStable();

    // Removal is optimistic; navigation is not — a failed delete restores the row
    // and the user must still be standing on the conversation it belongs to.
    expect(TestBed.inject(Router).url).toBe(`/chat/${conversation.id}`);

    backend
      .expectOne({ method: 'DELETE', url: detailUrl(conversation.id) })
      .flush(null, { status: 204, statusText: 'No Content' });
    await harness.fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/chat');
    expect(element().querySelector('.chat__header')).toBeNull();
    expect(element().querySelector('h1')?.textContent).toContain('How can I help you today?');
  });

  it('moves fallen-through focus to the main landmark after a header-initiated delete', async () => {
    // The navigation's render unmounts the header kebab that held focus; the fixup
    // must wait for that render (afterNextRender) or it finds the trigger still
    // connected and skips — leaving focus on <body>.
    const main = document.createElement('main');
    main.id = 'main-content';
    main.tabIndex = -1;
    document.body.appendChild(main);

    try {
      await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
      backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
      await harness.fixture.whenStable();

      const trigger = element().querySelector<HTMLButtonElement>('.chat__menu .menu__trigger');
      trigger?.focus();

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
    expect(element().querySelector('.chat__title')?.textContent).toContain(
      'Helios 2.4 release status',
    );
  });

  it('offers rename and delete from the header kebab, routed through the shared store', async () => {
    // US-308: the detail DTO is what the kebab hands over — its projectId is the
    // fresh one a rename must echo. A distinctive projectId proves detail preference.
    const projectId = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(detailUrl(conversation.id))
      .flush(conversationDetailFixture({ ...conversation, projectId }));
    await harness.fixture.whenStable();

    element().querySelector<HTMLButtonElement>('.chat__menu .menu__trigger')?.click();
    await harness.fixture.whenStable();

    const items = [...element().querySelectorAll<HTMLButtonElement>('[appMenuItem]')];
    const rename = items.find((item) => item.textContent?.includes('Rename'));
    const danger = items.filter((item) => item.classList.contains('menu__item--danger'));
    expect(danger).toHaveLength(1);
    expect(danger[0]?.textContent).toContain('Delete');

    rename?.click();
    await harness.fixture.whenStable();

    expect(TestBed.inject(ConversationActionsStore).renameTarget()?.projectId).toBe(projectId);
  });

  it('offers the project moves from the header kebab, and removes in one request (US-307)', async () => {
    const projectId = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(detailUrl(conversation.id))
      .flush(conversationDetailFixture({ ...conversation, projectId }));
    await harness.fixture.whenStable();

    element().querySelector<HTMLButtonElement>('.chat__menu .menu__trigger')?.click();
    await harness.fixture.whenStable();

    const items = [...element().querySelectorAll<HTMLButtonElement>('[appMenuItem]')];
    expect(items.map((item) => item.textContent?.trim())).toEqual([
      'Rename',
      'Move to project',
      'Remove from project',
      'Delete',
    ]);
    // The board's submenu affordance, announced as what it actually opens.
    const move = items.find((item) => item.textContent?.includes('Move to project'));
    expect(move?.getAttribute('aria-haspopup')).toBe('dialog');

    items.find((item) => item.textContent?.includes('Remove from project'))?.click();
    await harness.fixture.whenStable();

    // The detail DTO's projectId is what the body echoes away from, and the removal is
    // an explicit null rather than an omission.
    const request = backend.expectOne(`${TEST_API_BASE_URL}/api/conversations`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      id: conversation.id,
      name: conversation.name,
      projectId: null,
    });
    request.flush({ ...conversation, projectId: null });
  });

  it('withholds Remove from project on a standalone conversation (US-307)', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(detailUrl(conversation.id))
      .flush(conversationDetailFixture({ ...conversation, projectId: null }));
    await harness.fixture.whenStable();

    element().querySelector<HTMLButtonElement>('.chat__menu .menu__trigger')?.click();
    await harness.fixture.whenStable();

    const labels = [...element().querySelectorAll<HTMLButtonElement>('[appMenuItem]')].map((item) =>
      item.textContent?.trim(),
    );
    expect(labels).not.toContain('Remove from project');
    expect(labels).toContain('Move to project');
  });

  it('disables the header kebab items while the conversation’s own action is in flight', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    TestBed.inject(ConversationListStore).setRowPending(conversation.id, true);
    element().querySelector<HTMLButtonElement>('.chat__menu .menu__trigger')?.click();
    await harness.fixture.whenStable();

    // aria-disabled, not the native attribute: the items stay focusable so the
    // menu's roving focus and Escape keep working; the store guards make the
    // clicks no-ops.
    const items = [...element().querySelectorAll<HTMLButtonElement>('[appMenuItem]')];
    expect(items.length).toBeGreaterThan(0);
    expect(items.every((item) => item.getAttribute('aria-disabled') === 'true')).toBe(true);
    expect(items.every((item) => !item.disabled)).toBe(true);

    items[0]?.click();
    await harness.fixture.whenStable();
    expect(TestBed.inject(ConversationActionsStore).renameTarget()).toBeNull();
  });

  it('withholds the kebab for a deep link until the detail supplies a full DTO', async () => {
    // Acting needs more than a name: rename echoes projectId, delete names the
    // conversation — so the kebab waits a round trip rather than acting on a guess.
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);

    expect(element().querySelector('.chat__menu')).toBeNull();

    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    expect(element().querySelector('.chat__menu')).not.toBeNull();
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

    const star = element().querySelector<HTMLButtonElement>('.chat__star');
    expect(star?.classList.contains('chat__star--on')).toBe(true);
    // The label says which way the control goes, which is why it carries no
    // aria-pressed on top.
    expect(star?.getAttribute('aria-label')).toBe('Unfavourite Helios 2.4 release status');
  });

  it('flips the header star and the sidebar row together, before the server answers', async () => {
    // US-308's deferred criterion: the star performs US-305's toggle and both surfaces
    // reflect it — the header reads the list's optimistically patched copy.
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

    element().querySelector<HTMLButtonElement>('.chat__star')?.click();
    await harness.fixture.whenStable();

    expect(element().querySelector('.chat__star')?.classList.contains('chat__star--on')).toBe(true);
    expect(list.entities()[0]?.isFavorite).toBe(true);

    const request = backend.expectOne({
      method: 'PUT',
      url: `${detailUrl(conversation.id)}/favorite`,
    });
    expect(request.request.body).toEqual({ isFavorite: true });
    request.flush(null, { status: 204, statusText: 'No Content' });
    await harness.fixture.whenStable();

    expect(element().querySelector('.chat__star')?.classList.contains('chat__star--on')).toBe(true);
  });

  it('disables the header star while the conversation’s own action is in flight', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    TestBed.inject(ConversationListStore).setRowPending(conversation.id, true);
    await harness.fixture.whenStable();

    // Native disabled, unlike the kebab's items: a standalone button is not part of a
    // roving-focus panel it could be stranded out of.
    expect(element().querySelector<HTMLButtonElement>('.chat__star')?.disabled).toBe(true);
  });

  it('fills the star from the detail DTO for a deep link the list never held', async () => {
    // GET api/conversations/{id} reports isFavorite truthfully — unlike
    // GET {id}/messages — so the deep-link header is honest, one round trip late.
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);

    expect(element().querySelector('.chat__star')).toBeNull();

    backend
      .expectOne(detailUrl(conversation.id))
      .flush(conversationDetailFixture({ ...conversation, isFavorite: true }));
    await harness.fixture.whenStable();

    expect(element().querySelector('.chat__star')?.classList.contains('chat__star--on')).toBe(true);
  });

  it('follows a favourite announced for the open conversation', async () => {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    TestBed.inject(Dispatcher).dispatch(
      conversationEvents.updated({ ...conversation, isFavorite: true }),
    );
    await harness.fixture.whenStable();

    expect(element().querySelector('.chat__star')?.classList.contains('chat__star--on')).toBe(true);
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
    // Sending has no target when the conversation could not be opened; the
    // error panel is the screen's only content.
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(detailUrl(conversation.id))
      .flush(PROBLEM_FIXTURES.resourceNotFound, { status: 404, statusText: 'Not Found' });
    await harness.fixture.whenStable();

    expect(element().querySelector('app-composer')).toBeNull();
  });

  it('offers the four suggested prompts on the landing screen only', async () => {
    await harness.navigateByUrl('/chat', Chat);

    const labels = [...element().querySelectorAll('.chat-empty__chip')].map(
      (chip) => chip.textContent?.trim() ?? '',
    );
    expect(labels).toEqual([
      'Summarize a document',
      'Draft a status update',
      'Draft an email',
      'Generate a document',
    ]);

    // An open conversation's body is not the landing screen (frame 1a).
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend.expectOne(detailUrl(conversation.id)).flush(conversationDetailFixture(conversation));
    await harness.fixture.whenStable();

    expect(element().querySelector('.chat-empty__chip')).toBeNull();
  });

  it('seeds the composer when a suggested prompt is chosen, creating nothing', async () => {
    await harness.navigateByUrl('/chat', Chat);

    element().querySelector<HTMLButtonElement>('.chat-empty__chip')?.click();
    await harness.fixture.whenStable();

    const prompt = element().querySelector<HTMLTextAreaElement>('.composer__prompt');
    expect(prompt?.value).toBe('Summarize a document');
    expect(document.activeElement).toBe(prompt);
    // A chip only seeds text; sending is still the user's move (US-303).
    backend.expectNone(CREATE_URL);
  });

  it('mounts the turn status region outside the transcript, empty, from first render (US-1402)', async () => {
    await harness.navigateByUrl('/chat', Chat);

    // Present before any turn and silent: a live region created together with
    // its content is not reliably announced. The composer's own two regions —
    // the attachment summary and the note — are empty here for the same reason.
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

    const prompt = element().querySelector<HTMLTextAreaElement>('.composer__prompt');
    prompt!.value = 'First prompt';
    prompt!.dispatchEvent(new Event('input', { bubbles: true }));
    await harness.fixture.whenStable();
    element().querySelector<HTMLButtonElement>('.composer__send')?.click();
    await harness.fixture.whenStable();
    const created = conversationFixture();
    backend.expectOne(CREATE_URL).flush(created);
    await harness.fixture.whenStable();
    // The replaceUrl navigation opens the conversation, exactly as US-401's own
    // test does; leaving it unanswered would fail `backend.verify()`.
    backend.expectOne(detailUrl(created.id)).flush(conversationDetailFixture(created));
    await harness.fixture.whenStable();

    // Now the transcript is mounted with its own two status regions, so the
    // separation is a real assertion rather than one that passes on absence.
    const transcript = element().querySelector('app-transcript');
    expect(transcript).not.toBeNull();
    expect(transcript?.getAttribute('aria-busy')).toBe('true');

    const speaking = [...element().querySelectorAll('[role="status"]')].filter(
      (node) => (node.textContent?.trim() ?? '') !== '',
    );
    expect(speaking).toHaveLength(1);

    // The one region carrying the announcement is outside the `aria-busy`
    // container, where a live region would be deferred along with the answer,
    // and outside the scroll container, so nothing about it moves the reader.
    // What this element renders is `turnStatus`, so the store spec's remaining
    // strings — including the `Answer ready` backstop — reach here too. Pinning
    // those here as well would mean streaming real frames through the 16 ms
    // buffer window on real timers, which this file has never done and which
    // buys a second assertion of a link already proven.
    const live = speaking[0]!;
    expect(live.textContent?.trim()).toBe('Waiting for a response');
    expect(transcript?.contains(live)).toBe(false);
    expect(element().querySelector('.chat__body')?.contains(live)).toBe(false);
  });

  it('runs the first send end to end: create, prepend, replace the URL, stream (US-401)', async () => {
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

    const prompt = element().querySelector<HTMLTextAreaElement>('.composer__prompt');
    prompt!.value = 'First prompt';
    prompt!.dispatchEvent(new Event('input', { bubbles: true }));
    await harness.fixture.whenStable();
    element().querySelector<HTMLButtonElement>('.composer__send')?.click();
    await harness.fixture.whenStable();

    const created = conversationFixture({ name: 'New conversation' });
    const create = backend.expectOne(CREATE_URL);
    expect(create.request.body).toEqual({ projectId: null });
    create.flush(created);
    await harness.fixture.whenStable();

    // The URL was replaced through the router, so the route input caught up —
    // and the equality guard means the live turn survived it.
    expect(TestBed.inject(Router).url).toBe(`/chat/${created.id}`);
    expect(TestBed.inject(ConversationListStore).entities()[0]?.id).toBe(created.id);
    expect(streamFetch).toHaveBeenCalledTimes(1);

    // The transcript replaced the empty state: optimistic bubble, thinking
    // ridgeline, aria-busy — the turn is in flight against the new id.
    expect(element().querySelector('.transcript__bubble')?.textContent).toBe('First prompt');
    expect(element().querySelector('app-ridgeline')).not.toBeNull();
    expect(element().querySelector('app-transcript')?.getAttribute('aria-busy')).toBe('true');
    expect(element().querySelector('.chat-empty__chip')).toBeNull();

    // The navigation opened the conversation detail, exactly as a sidebar click would.
    backend.expectOne(detailUrl(created.id)).flush(conversationDetailFixture(created));
    await harness.fixture.whenStable();
    expect(element().querySelector('.chat__title')?.textContent).toContain('New conversation');
  });

  describe('picking up the name the server generates (US-409)', () => {
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

      expect(element().querySelector('.chat__title')?.textContent).toContain(
        'Helios 2.4 release status',
      );
      // The sidebar row moves with it — no manual refresh.
      expect(TestBed.inject(ConversationListStore).entityMap()[conversation.id]?.name).toBe(
        'Helios 2.4 release status',
      );
    });

    it('leaves the header showing the old name while the refetch is in flight', async () => {
      await openAndComplete(true);

      // The silent refresh must not blank the header on its way: `open` would
      // clear the detail and flash the sidebar's copy back.
      expect(element().querySelector('.chat__title')?.textContent).toContain('New conversation');
      backend
        .expectOne(detailUrl(conversation.id))
        .flush(conversationDetailFixture({ ...conversation }));
    });

    it('issues nothing after a later turn, whose name cannot have changed', async () => {
      await openAndComplete(false);

      backend.expectNone(detailUrl(conversation.id));
    });
  });

  describe('the header download control (US-1502)', () => {
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

    /**
     * Two controls, and deliberately: frame `2f` offers the same menu "from the composer
     * and the conversation header". They are one component and one store, which is what
     * the criterion's "one shared menu" means — and why the store's outcome is a replaced
     * record rather than a one-shot either instance could swallow from the other.
     *
     * The header's is declared once, inside `#navbarActions`, so it follows the star and
     * the kebab into frame `1d`'s navbar below 768px rather than needing a second copy.
     */
    it('renders the control in the header and in the composer, from one declaration each', async () => {
      await openWithTranscript();

      expect(element().querySelectorAll('app-conversation-download-menu')).toHaveLength(2);
      expect(
        element().querySelectorAll('.chat__header app-conversation-download-menu'),
      ).toHaveLength(1);
      expect(
        element().querySelectorAll('.chat__composer app-conversation-download-menu'),
      ).toHaveLength(1);
    });

    it('is absent for a conversation with nothing in it', async () => {
      await openWithTranscript([]);

      expect(element().querySelector('app-conversation-download-menu')).toBeNull();
      // The star and the kebab stay: they act on the row, which exists from creation.
      expect(element().querySelector('.chat__star')).not.toBeNull();
    });
  });

  describe('resuming a conversation (US-410)', () => {
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
      expect(element().querySelector('.model-menu__pill')?.textContent).toContain('Claude Sonnet');
      expect(element().querySelector('.tools-menu__pill')?.textContent).toContain('1 Tool');
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

      // The server writes `modelId` and the MCP set from the newest usage row,
      // so a conversation whose first turn has not finished reports both empty
      // — which is what US-401's own `replaceUrl` re-opens this store against.
      // Seeding from it would undo the picks the running turn was sent with.
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
      // Silently substituting a different model would misreport what the next
      // turn will cost and what it can do.
      expect(element().querySelector('.composer__note')?.textContent).toContain(
        'The model this conversation last used is unavailable',
      );
    });

    it('says nothing while the catalog is still loading', async () => {
      // The detail request and the catalog load race on every deep link; an
      // empty catalog is "not loaded yet", never "your model was deactivated".
      await openWith({ modelId: '00000000-dead-4bee-8ccc-dddddddddddd', mcpServerIds: [] });

      expect(TestBed.inject(TurnSettingsStore).restoredModelUnavailable()).toBe(false);
      expect(element().querySelector('.composer__note')?.textContent?.trim()).toBe('');
    });

    it('replays the stored messages into the transcript', async () => {
      await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
      backend
        .expectOne(detailUrl(conversation.id))
        .flush(conversationDetailFixture({ ...conversation }));

      // The skeleton stands in for the messages rather than the landing screen,
      // which would otherwise flash for the length of the round trip.
      await harness.fixture.whenStable();
      expect(element().querySelector('.transcript__history-skeleton')).not.toBeNull();
      expect(element().querySelector('app-chat-empty-state')).toBeNull();
      // The skeleton is aria-hidden, so the wait is only perceivable to
      // assistive tech through the region's own busy state.
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

      expect(element().querySelector('.transcript__history-skeleton')).toBeNull();
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
      // Not being able to read the past is not a reason to block the future.
      expect(element().querySelector('.composer__prompt')).not.toBeNull();
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
