import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideLocationMocks } from '@angular/common/testing';
import {
  RouterOutlet,
  UrlSegment,
  provideRouter,
  withComponentInputBinding,
} from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import {
  conversationDetailFixture,
  conversationFixture,
  conversationPage,
} from '@testing/conversations';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { Chat } from './chat';
import { CONVERSATION_ID_PARAM, chatMatcher } from './chat-route';

const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;

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

  const conversation = conversationFixture({ name: 'Helios 2.4 release status' });

  beforeEach(async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter(
          [
            { matcher: chatMatcher, component: Chat },
            { path: '**', component: TestHost },
          ],
          withComponentInputBinding(),
        ),
        provideLocationMocks(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => {
    backend.verify();
  });

  function detailUrl(id: string): string {
    return `${TEST_API_BASE_URL}/api/conversations/${id}`;
  }

  function element(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

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
    // The property US-401 depends on: it creates the conversation mid-turn and updates
    // the URL with Location.replaceState while an answer is already streaming.
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
