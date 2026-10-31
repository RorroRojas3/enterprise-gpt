import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, RouterOutlet, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { getByRole } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AccessTokenClient } from '@core/auth/access-token-client';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { ConversationActivityStore } from '@core/conversations/conversation-activity-store';
import { conversationEvents } from '@core/events/conversation-events';
import { sessionEvents } from '@core/events/session-events';
import { chatMatcher } from '@core/navigation/chat-matcher';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { modelFixture } from '@testing/catalog';
import { conversationDetailFixture, conversationFixture } from '@testing/conversations';
import {
  StreamingResponseHandle,
  assistantEvent,
  frame,
  streamingResponse,
} from '@testing/stream-frames';
import { FakeUploadXhrQueue, provideFakeUploadXhr } from '@testing/upload-xhr';
import { Chat } from './chat';
import { ChatSessions } from './chat-sessions';
import { STREAM_BATCH_WINDOW_MS } from './conversation-stream-client';
import { STREAM_FETCH } from './stream-fetch-token';
import { TurnStore } from './turn-store';

const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;
const CREATE_URL = `${TEST_API_BASE_URL}/api/conversations`;

@Component({
  selector: 'app-test-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
class TestHost {}

/** One opened stream, with the signal its fetch was given. */
interface OpenedStream {
  readonly url: string;
  readonly handle: StreamingResponseHandle;
  readonly signal: AbortSignal;
}

describe('ChatSessions', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;
  let streams: OpenedStream[];
  let uploadXhr: FakeUploadXhrQueue;

  const first = conversationFixture({ name: 'Helios release' });
  const second = conversationFixture({ name: 'Quarterly numbers' });

  beforeEach(async () => {
    streams = [];
    uploadXhr = new FakeUploadXhrQueue();
    const streamFetch = vi.fn((url: string, init: RequestInit) => {
      const handle = streamingResponse();
      handle.abortOn(init.signal);
      streams.push({ url, handle, signal: init.signal! });
      return Promise.resolve(handle.response);
    });

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig({ features: { diagrams: false, math: false, rawStreamCodec: false } }),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: STREAM_FETCH, useValue: streamFetch },
        provideFakeUploadXhr(uploadXhr),
        {
          provide: AccessTokenClient,
          useValue: { getToken: vi.fn().mockResolvedValue('token-1') },
        },
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

    const models = TestBed.inject(ModelCatalogStore).ensureLoaded();
    backend.expectOne(MODELS_URL).flush([modelFixture({ isDefault: true })]);
    await models;
  });

  afterEach(() => {
    backend.match((request) => request.url.includes('file-extensions'));
    backend.verify();
  });

  function sessions(): ChatSessions {
    return TestBed.inject(ChatSessions);
  }

  function messagesUrl(id: string): string {
    return `${TEST_API_BASE_URL}/api/conversations/${id}/messages`;
  }

  function streamUrl(id: string): string {
    return `${TEST_API_BASE_URL}/api/conversations/${id}/stream`;
  }

  function element(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function prompt(): HTMLTextAreaElement {
    return getByRole<HTMLTextAreaElement>(element(), 'textbox', { name: 'Message Enterprise GPT' });
  }

  /** Opens a conversation; `history` is false when its session is expected to be kept. */
  async function open(
    conversation: typeof first,
    { history = true }: { history?: boolean } = {},
  ): Promise<void> {
    await harness.navigateByUrl(`/chat/${conversation.id}`, Chat);
    backend
      .expectOne(`${CREATE_URL}/${conversation.id}`)
      .flush(conversationDetailFixture({ ...conversation }));
    if (history) {
      backend
        .expectOne(messagesUrl(conversation.id))
        .flush({ id: conversation.id, name: conversation.name, messages: [] });
    } else {
      backend.expectNone(messagesUrl(conversation.id));
    }
    await harness.fixture.whenStable();
  }

  async function type(text: string): Promise<void> {
    prompt().value = text;
    prompt().dispatchEvent(new Event('input', { bubbles: true }));
    await harness.fixture.whenStable();
  }

  async function send(text: string): Promise<void> {
    await type(text);
    getByRole(element(), 'button', { name: 'Send' }).click();
    await harness.fixture.whenStable();
  }

  /** Real timers: the stream client holds decoded events for one batch window. */
  async function flushBatches(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, STREAM_BATCH_WINDOW_MS * 2));
    await harness.fixture.whenStable();
  }

  function streamFor(id: string): OpenedStream {
    const opened = streams.find((stream) => stream.url === streamUrl(id));
    expect(opened).toBeDefined();
    return opened!;
  }

  it('keeps a turn streaming while the user reads another conversation', async () => {
    await open(first);
    await send('Summarise the release');
    const running = streamFor(first.id);

    await open(second);

    expect(running.signal.aborted).toBe(false);
    expect([...sessions().running()]).toEqual([first.id]);

    running.handle.enqueue(frame(assistantEvent('TextDelta', { text: 'The release ships' })));
    await flushBatches();

    // Back again: the same session, so nothing is read back and the live text is on screen.
    await open(first, { history: false });
    expect(sessions().active()?.turn.snapshot().text).toBe('The release ships');
    expect(element().querySelector('.transcript__bubble')?.textContent).toBe(
      'Summarise the release',
    );
  });

  it('lets a second conversation stream beside the first', async () => {
    await open(first);
    await send('One');
    await open(second);
    await send('Two');

    expect(streams.map((stream) => stream.url)).toEqual([
      streamUrl(first.id),
      streamUrl(second.id),
    ]);
    expect(streams.every((stream) => !stream.signal.aborted)).toBe(true);
    expect(sessions().running()).toEqual(new Set([first.id, second.id]));
  });

  it('keeps each conversation’s unsent text to itself', async () => {
    await open(first);
    await type('Half a thought about Helios');
    await open(second);

    expect(prompt().value).toBe('');
    await type('Something else');

    await open(first, { history: false });
    expect(prompt().value).toBe('Half a thought about Helios');
    await open(second, { history: false });
    expect(prompt().value).toBe('Something else');
  });

  it('keeps each conversation’s attachment chips to itself', async () => {
    await open(first);
    sessions()
      .active()!
      .uploads.attach([new File(['x'], 'notes.txt', { type: 'text/plain' })]);
    await harness.fixture.whenStable();
    await open(second);

    expect(sessions().active()!.uploads.attachments()).toHaveLength(0);

    await open(first, { history: false });
    expect(
      sessions()
        .active()!
        .uploads.attachments()
        .map((row) => row.fileName),
    ).toEqual(['notes.txt']);
  });

  it('drops a session left idle with nothing unsent, and reads it back on return', async () => {
    await open(first);
    await open(second);

    await open(first, { history: true });
  });

  it('holds a conversation’s upload gate shut when the user moves on mid-upload', async () => {
    await open(first);
    sessions()
      .active()!
      .uploads.attach([new File(['x'], 'notes.txt', { type: 'text/plain' })]);
    await harness.fixture.whenStable();
    await send('What does this say?');
    const upload = uploadXhr.last;

    await open(second);

    // Another conversation's binding must neither cancel the upload nor open the stream early.
    expect(upload.aborted).toBe(false);
    expect(streams).toHaveLength(0);
    expect(sessions().running()).toEqual(new Set([first.id]));
  });

  it('keeps a turn that settled out of sight until it has been seen', async () => {
    await open(first);
    await send('Summarise the release');
    await open(second);

    const running = streamFor(first.id);
    running.handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Half an ans' })));
    running.handle.close();
    await flushBatches();

    await open(first, { history: false });
    expect(
      sessions()
        .active()
        ?.turn.entries()
        .map((entry) => entry.kind),
    ).toEqual(['user', 'cutOff']);
  });

  it('stops a conversation from outside it, leaving the stopped card for when it is opened', async () => {
    await open(first);
    await send('Summarise the release');
    const running = streamFor(first.id);
    running.handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Partial' })));
    await flushBatches();
    await open(second);

    sessions().stop(first.id);
    await flushBatches();

    expect(running.signal.aborted).toBe(true);
    expect(sessions().running().size).toBe(0);
    await open(first, { history: false });
    expect(sessions().active()?.turn.stoppedTurn()?.text).toBe('Partial');
  });

  it('ends the stream of a conversation deleted out of sight', async () => {
    await open(first);
    await send('Summarise the release');
    await open(second);

    TestBed.inject(Dispatcher).dispatch(conversationEvents.deleted(first.id));

    expect(streamFor(first.id).signal.aborted).toBe(true);
    expect(sessions().running().size).toBe(0);
  });

  it('ends every stream on sign-out', async () => {
    await open(first);
    await send('One');
    await open(second);
    await send('Two');

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    await harness.fixture.whenStable();

    expect(streams.every((stream) => stream.signal.aborted)).toBe(true);
    expect(sessions().runningCount()).toBe(0);
  });

  it('finishes a first prompt’s create in the background and leaves the user where they went', async () => {
    await harness.navigateByUrl('/chat', Chat);
    await send('A brand new question');
    const create = backend.expectOne(CREATE_URL);
    expect(sessions().runningCount()).toBe(1);

    await open(second);
    const created = conversationFixture({ name: 'New conversation' });
    create.flush(created);
    await harness.fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe(`/chat/${second.id}`);
    expect(streamFor(created.id).signal.aborted).toBe(false);
    expect(sessions().running()).toEqual(new Set([created.id]));

    // A fresh blank conversation, not the one still streaming.
    await harness.navigateByUrl('/chat', Chat);
    await harness.fixture.whenStable();
    expect(prompt().value).toBe('');
    expect(sessions().active()?.turn.boundConversationId()).toBeNull();
  });

  it('keeps a turn streaming after the user leaves the chat screen, and drops an idle one', async () => {
    await open(first);
    await send('Summarise the release');
    await open(second);
    await harness.navigateByUrl('/projects');
    await harness.fixture.whenStable();

    expect(sessions().active()).toBeNull();
    expect(streamFor(first.id).signal.aborted).toBe(false);

    // The idle one was dropped on the way out, so it is read back; the running one is not.
    await open(second, { history: true });
    await open(first, { history: false });
  });

  it('drops the session of a conversation deleted on screen once it is left, draft and all', async () => {
    await open(first);
    await type('A draft nobody will send');

    TestBed.inject(Dispatcher).dispatch(conversationEvents.deleted(first.id));
    await open(second);

    await open(first, { history: true });
    expect(prompt().value).toBe('');
  });

  it('returns to the blank conversation the user was drafting on `/chat`', async () => {
    await harness.navigateByUrl('/chat', Chat);
    await type('Not sent yet');
    await open(first);

    await harness.navigateByUrl('/chat', Chat);
    await harness.fixture.whenStable();

    expect(prompt().value).toBe('Not sent yet');
  });

  it('marks an answer that finished out of sight, and not the one on screen', async () => {
    const activity = TestBed.inject(ConversationActivityStore);
    await open(first);
    await send('One');
    await open(second);
    await send('Two');

    for (const id of [first.id, second.id]) {
      const { handle } = streamFor(id);
      handle.enqueue(frame(assistantEvent('Finished', {})));
      handle.close();
    }
    await flushBatches();
    // First turns: each generated name is read back.
    for (const conversation of [first, second]) {
      backend
        .expectOne(`${CREATE_URL}/${conversation.id}`)
        .flush(conversationDetailFixture({ ...conversation }));
    }

    expect(activity.activityOf(first.id)).toBe('answer');
    expect(activity.activityOf(second.id)).toBeNull();

    await open(first, { history: false });
    expect(activity.activityOf(first.id)).toBeNull();
  });

  it('ends every session with the application, as the root does not own them', async () => {
    await open(first);
    await open(second);
    const held = sessions().active()!;

    TestBed.resetTestingModule();

    // A live injector would still resolve; a destroyed one refuses (NG0205).
    expect(() => held.injector.get(TurnStore)).toThrow(/destroyed/);
  });
});
