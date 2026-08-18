import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { Dispatcher, Events } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture } from '@testing/conversations';
import { CHAT_ROLE, ConversationMessageDto } from '@domain/api/conversation';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import {
  StreamingResponseHandle,
  assistantEvent,
  frame,
  fullTurnEvents,
  problemResponse,
  streamingResponse,
} from '@testing/stream-frames';
import { FakeUploadXhrQueue, provideFakeUploadXhr } from '@testing/upload-xhr';
import { TurnSelection, TurnSettingsStore } from '@core/chat/turn-settings-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { sessionEvents } from '@core/events/session-events';
import { TurnCompleted, turnEvents } from '@core/events/turn-events';
import { TokenService } from '@core/auth/token-service';
import { STREAM_BATCH_WINDOW_MS } from '@core/stream/conversation-stream-client';
import { STREAM_FETCH } from '@core/stream/stream-fetch.token';
import { ConversationStore } from './conversation-store';
import { TranscriptEntry, TurnStore } from './turn-store';
import { UploadStore } from '@core/documents/upload-store';

const CONVERSATION_ID = '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b';
const CREATE_URL = `${TEST_API_BASE_URL}/api/conversations`;
const SELECTION: TurnSelection = { modelId: 'model-1', mcpServerIds: ['mcp-a'] };

describe('TurnStore', () => {
  let store: InstanceType<typeof TurnStore>;
  let list: InstanceType<typeof ConversationListStore>;
  let backend: HttpTestingController;
  let fetchMock: ReturnType<typeof vi.fn>;
  let navigateByUrl: ReturnType<typeof vi.fn>;
  let applyConversationSettings: ReturnType<typeof vi.fn>;
  let getToken: ReturnType<typeof vi.fn>;
  let selection: TurnSelection | null;
  let uploadXhr: FakeUploadXhrQueue;

  beforeEach(() => {
    vi.useFakeTimers();
    uploadXhr = new FakeUploadXhrQueue();
  });

  afterEach(() => {
    backend.verify();
    vi.useRealTimers();
  });

  function setup(options: { selection?: TurnSelection | null } = {}): void {
    selection = options.selection === undefined ? SELECTION : options.selection;
    fetchMock = vi.fn();
    navigateByUrl = vi.fn().mockResolvedValue(true);
    applyConversationSettings = vi.fn();
    getToken = vi.fn().mockResolvedValue('token-1');

    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: STREAM_FETCH, useValue: fetchMock },
        { provide: TokenService, useValue: { getToken } },
        { provide: Router, useValue: { navigateByUrl } },
        {
          provide: TurnSettingsStore,
          useValue: {
            streamSelection: () => selection,
            applyConversationSettings,
          },
        },
        // Provided rather than faked: TurnStore gates the stream on its settle
        // signal (EP-8), and a fake store would let that gate pass without proving
        // it. With nothing attached it settles synchronously, which is the path
        // every test outside "the attachment gate" takes.
        provideFakeUploadXhr(uploadXhr),
        UploadStore,
        ConversationStore,
        TurnStore,
      ],
    });

    store = TestBed.inject(TurnStore);
    list = TestBed.inject(ConversationListStore);
    backend = TestBed.inject(HttpTestingController);
  }

  function respondWithStream(handle: StreamingResponseHandle): void {
    fetchMock.mockImplementation((_input: unknown, init?: RequestInit) => {
      handle.abortOn(init?.signal);
      return Promise.resolve(handle.response);
    });
  }

  /** Advances fake time, draining the microtasks the read loop and buffer window await on. */
  async function settle(ms = 0): Promise<void> {
    await vi.advanceTimersByTimeAsync(ms);
  }

  function messagesUrl(id: string): string {
    return `${TEST_API_BASE_URL}/api/conversations/${id}/messages`;
  }

  /**
   * Answers the stored-transcript read that opening a conversation issues
   * (US-410). Every test that binds a conversation has to, because the read is
   * part of opening one — an empty body is the "nothing said yet" case.
   */
  function flushHistory(id: string, messages: readonly ConversationMessageDto[] = []): void {
    backend.expectOne(messagesUrl(id)).flush({ id, name: 'Conversation', messages });
  }

  /** Binds an existing conversation and opens a live stream for it. */
  async function startBoundTurn(handle = streamingResponse()): Promise<StreamingResponseHandle> {
    store.bindRoute(CONVERSATION_ID);
    flushHistory(CONVERSATION_ID);
    respondWithStream(handle);
    store.send('What is the weather?');
    await settle();
    return handle;
  }

  describe('the attachment gate (US-801)', () => {
    /** A file whose upload never settles, so the gate stays shut. */
    function attachPending(): void {
      const uploads = TestBed.inject(UploadStore);
      uploads.bindConversation(CONVERSATION_ID);
      uploads.attach([new File(['x'], 'notes.txt', { type: 'text/plain' })]);
    }

    it('holds the stream until every attachment has settled', async () => {
      setup();
      store.bindRoute(CONVERSATION_ID);
      flushHistory(CONVERSATION_ID);
      attachPending();
      respondWithStream(streamingResponse());

      store.send('What does this say?');
      await settle();

      // A document still being embedded is not in what the retrieval tool can see,
      // so opening the stream now would answer the question without the file.
      expect(fetchMock).not.toHaveBeenCalled();
      expect(store.inFlight()).toBe(true);

      TestBed.inject(UploadStore).remove(TestBed.inject(UploadStore).attachments()[0]!.id);
      await settle();

      expect(fetchMock).toHaveBeenCalledTimes(1);
    });

    it('costs a turn with no attachments nothing', async () => {
      setup();
      store.bindRoute(CONVERSATION_ID);
      flushHistory(CONVERSATION_ID);
      respondWithStream(streamingResponse());

      store.send('Plain prompt');
      await settle();

      expect(fetchMock).toHaveBeenCalledTimes(1);
    });

    it('lets Stop out of the wait rather than stranding the turn in it', async () => {
      setup();
      store.bindRoute(CONVERSATION_ID);
      flushHistory(CONVERSATION_ID);
      attachPending();
      respondWithStream(streamingResponse());

      store.send('What does this say?');
      await settle();
      expect(fetchMock).not.toHaveBeenCalled();

      store.stop();
      await settle();

      // Nothing streamed, so nothing is opened and the prompt goes back to the
      // composer — the same settle the other pre-stream window takes.
      expect(fetchMock).not.toHaveBeenCalled();
      expect(store.inFlight()).toBe(false);
      expect(store.phase()).toBe('idle');
      expect(store.composerSeed()?.text).toBe('What does this say?');

      // And the slot is genuinely free: without this the Critical would still be
      // here, with `exhaustMap` swallowing every later send in silence.
      TestBed.inject(UploadStore).remove(TestBed.inject(UploadStore).attachments()[0]!.id);
      store.send('A fresh prompt');
      await settle();

      expect(fetchMock).toHaveBeenCalledTimes(1);
    });

    it('does not swallow the next send after a route change abandons the wait', async () => {
      setup();
      store.bindRoute(CONVERSATION_ID);
      flushHistory(CONVERSATION_ID);
      attachPending();
      respondWithStream(streamingResponse());

      store.send('What does this say?');
      await settle();
      expect(fetchMock).not.toHaveBeenCalled();

      // Moving away must tear the gate down with the turn. Left alive, it holds
      // `exhaustMap`'s slot and every later send is dropped with no error anywhere.
      // Both bindings move together, as `Chat` drives them from the same input.
      const other = '2b3c4d5e-6f70-4812-9a3b-4c5d6e7f8091';
      store.bindRoute(other);
      TestBed.inject(UploadStore).bindConversation(other);
      flushHistory(other);
      await settle();

      store.send('A fresh prompt');
      await settle();

      expect(fetchMock).toHaveBeenCalledTimes(1);
      const [url] = fetchMock.mock.calls[0] as [string, RequestInit];
      expect(url).toBe(`${TEST_API_BASE_URL}/api/conversations/${other}/stream`);
    });
  });

  describe('send on the empty chat screen (US-401)', () => {
    it('creates the conversation, prepends it, replaces the URL, and streams against the new id', async () => {
      setup();
      const created = conversationFixture();
      respondWithStream(streamingResponse());

      store.send('First prompt');
      expect(store.phase()).toBe('creating');
      expect(store.pendingUserText()).toBe('First prompt');

      const create = backend.expectOne(CREATE_URL);
      expect(create.request.method).toBe('POST');
      expect(create.request.body).toEqual({ projectId: null });
      create.flush(created);
      await settle();

      expect(list.entities()[0]?.id).toBe(created.id);
      expect(navigateByUrl).toHaveBeenCalledExactlyOnceWith(`/chat/${created.id}`, {
        replaceUrl: true,
      });
      expect(store.boundConversationId()).toBe(created.id);

      expect(fetchMock).toHaveBeenCalledTimes(1);
      const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
      expect(url).toBe(`${TEST_API_BASE_URL}/api/conversations/${created.id}/stream`);
      expect(JSON.parse(init.body as string)).toEqual({
        prompt: 'First prompt',
        modelId: 'model-1',
        mcpServers: [{ id: 'mcp-a' }],
      });
    });

    it('does not reset the live turn when the route input catches up with the created id', async () => {
      setup();
      const created = conversationFixture();
      const handle = streamingResponse();
      respondWithStream(handle);

      store.send('First prompt');
      backend.expectOne(CREATE_URL).flush(created);
      await settle();
      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Hello' })));
      await settle(STREAM_BATCH_WINDOW_MS);

      // withComponentInputBinding delivers the id the replaceUrl navigation set.
      store.bindRoute(created.id);

      expect(store.phase()).toBe('streaming');
      expect(store.snapshot().text).toBe('Hello');
    });

    it('keeps the prompt when creation fails, as a composer seed beside the notice', async () => {
      setup();
      respondWithStream(streamingResponse());

      store.send('First prompt');
      backend
        .expectOne(CREATE_URL)
        .flush({ title: 'Boom' }, { status: 500, statusText: 'Internal Server Error' });
      await settle();

      expect(store.phase()).toBe('idle');
      expect(store.pendingUserText()).toBeNull();
      expect(store.composerSeed()?.text).toBe('First prompt');
      expect(store.turnError()?.retry).toBeNull();
      expect(fetchMock).not.toHaveBeenCalled();

      // The failure is contained: the next send still works.
      store.send('Second try');
      backend.expectOne(CREATE_URL).flush(conversationFixture());
      await settle();
      expect(fetchMock).toHaveBeenCalledTimes(1);
    });

    it('cancels the create when the route moves before the 201 lands', async () => {
      setup();
      const other = conversationFixture();
      store.send('First prompt');
      const create = backend.expectOne(CREATE_URL);

      // The user opened another conversation from the sidebar mid-create.
      store.bindRoute(other.id);
      flushHistory(other.id);
      await settle();

      expect(create.cancelled).toBe(true);
      expect(store.phase()).toBe('idle');
      expect(store.boundConversationId()).toBe(other.id);
      expect(navigateByUrl).not.toHaveBeenCalled();
      expect(fetchMock).not.toHaveBeenCalled();

      // The next send targets the newly opened conversation directly.
      respondWithStream(streamingResponse());
      store.send('Again');
      await settle();
      backend.expectNone(CREATE_URL);
      const [url] = fetchMock.mock.calls[0] as [string];
      expect(url).toBe(`${TEST_API_BASE_URL}/api/conversations/${other.id}/stream`);
    });

    it('settles like Stop when a same-URL route event lands mid-create', async () => {
      setup();
      store.send('First prompt');
      const create = backend.expectOne(CREATE_URL);

      // "New conversation" delivering the bare route again while the create flies.
      store.bindRoute(undefined);
      await settle();

      expect(create.cancelled).toBe(true);
      expect(store.phase()).toBe('idle');
      expect(store.stoppedTurn()).toBeNull();
      expect(store.composerSeed()?.text).toBe('First prompt');
      expect(fetchMock).not.toHaveBeenCalled();
    });

    it('cancels the create and resets when the user signs out mid-create', async () => {
      setup();
      store.send('First prompt');

      const create = backend.expectOne(CREATE_URL);
      TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
      await settle();

      expect(create.cancelled).toBe(true);
      expect(store.phase()).toBe('idle');
      expect(store.pendingUserText()).toBeNull();
      expect(fetchMock).not.toHaveBeenCalled();
    });
  });

  describe('send on an open conversation', () => {
    it('skips the create and streams against the bound id', async () => {
      setup();
      await startBoundTurn();

      backend.expectNone(CREATE_URL);
      expect(fetchMock).toHaveBeenCalledTimes(1);
      const [url] = fetchMock.mock.calls[0] as [string];
      expect(url).toBe(`${TEST_API_BASE_URL}/api/conversations/${CONVERSATION_ID}/stream`);
    });

    it('ignores a second send while a turn is in flight', async () => {
      setup();
      await startBoundTurn();

      store.send('Another prompt');
      await settle();

      backend.expectNone(CREATE_URL);
      expect(fetchMock).toHaveBeenCalledTimes(1);
    });

    it('refuses to send with no resolvable model or an empty prompt', async () => {
      setup({ selection: null });
      store.bindRoute(CONVERSATION_ID);
      flushHistory(CONVERSATION_ID);

      store.send('A prompt');
      await settle();
      expect(fetchMock).not.toHaveBeenCalled();

      selection = SELECTION;
      store.send('   ');
      await settle();
      expect(fetchMock).not.toHaveBeenCalled();
      expect(store.phase()).toBe('idle');
    });
  });

  describe('watching the answer arrive (US-406)', () => {
    it('accumulates streamed text on the live snapshot without touching the transcript', async () => {
      setup();
      const handle = await startBoundTurn();

      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Hello, ' })));
      await settle(STREAM_BATCH_WINDOW_MS);
      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'world.' })));
      await settle(STREAM_BATCH_WINDOW_MS);

      expect(store.phase()).toBe('streaming');
      expect(store.snapshot().text).toBe('Hello, world.');
      expect(store.entries()).toEqual([]);
      expect(store.pendingUserText()).toBe('What is the weather?');
    });

    it('appends the user and assistant entries once on Finished and clears the buffer', async () => {
      setup();
      const handle = await startBoundTurn();

      handle.enqueue(fullTurnEvents().map(frame).join(''));
      handle.close();
      await settle(STREAM_BATCH_WINDOW_MS);

      const entries = store.entries();
      expect(entries.map((entry) => entry.kind)).toEqual(['user', 'assistant']);
      expect(entries[0]).toMatchObject({ kind: 'user', text: 'What is the weather?' });
      const answer = entries[1] as Extract<TranscriptEntry, { kind: 'assistant' }>;
      expect(answer.snapshot.text).toBe('Hello, world.');
      expect(answer.snapshot.phase).toBe('Completed');
      expect(answer.timeline.nodes.length).toBeGreaterThan(0);

      expect(store.phase()).toBe('idle');
      expect(store.pendingUserText()).toBeNull();
      expect(store.snapshot().text).toBeUndefined();
      expect(store.timeline().nodes).toEqual([]);
    });

    it('settles a body that ended without Finished as a cut-off entry carrying its retry', async () => {
      setup();
      const handle = await startBoundTurn();

      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Partial ans' })));
      handle.close();
      await settle(STREAM_BATCH_WINDOW_MS);

      const entries = store.entries();
      expect(entries.map((entry) => entry.kind)).toEqual(['user', 'cutOff']);
      const cutOff = entries[1] as Extract<TranscriptEntry, { kind: 'cutOff' }>;
      expect(cutOff.snapshot.text).toBe('Partial ans');
      expect(cutOff.snapshot.phase).toBe('Running');
      expect(cutOff.retry).toEqual({ prompt: 'What is the weather?', selection: SELECTION });
      expect(store.phase()).toBe('idle');
    });

    it('keeps the thinking indicator through a Status-only batch and clears it on content', async () => {
      setup();
      const handle = await startBoundTurn();
      expect(store.showThinking()).toBe(true);

      handle.enqueue(frame(assistantEvent('Status')));
      await settle(STREAM_BATCH_WINDOW_MS);
      expect(store.showThinking()).toBe(true);
      expect(store.phase()).toBe('awaitingFirst');

      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'H' })));
      await settle(STREAM_BATCH_WINDOW_MS);
      expect(store.showThinking()).toBe(false);
      expect(store.phase()).toBe('streaming');
    });

    it('re-sends a cut-off turn with its own prompt and selection', async () => {
      setup();
      const handle = await startBoundTurn();
      handle.close();
      await settle();
      const cutOff = store.entries()[1] as Extract<TranscriptEntry, { kind: 'cutOff' }>;

      respondWithStream(streamingResponse());
      store.retryTurn(cutOff.retry);
      await settle();

      expect(fetchMock).toHaveBeenCalledTimes(2);
      const [, init] = fetchMock.mock.calls[1] as [string, RequestInit];
      expect(JSON.parse(init.body as string)).toMatchObject({ prompt: 'What is the weather?' });
    });
  });

  describe('stopping a turn (US-407)', () => {
    it('aborts the fetch, keeps the flushed partial text on the detached card, and drops the bubble', async () => {
      setup();
      const handle = await startBoundTurn();

      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Partial ans' })));
      await settle(STREAM_BATCH_WINDOW_MS);

      store.stop();
      // The send control returns on this synchronous patch — no network wait.
      expect(store.inFlight()).toBe(true);
      await settle(STREAM_BATCH_WINDOW_MS);

      expect(store.phase()).toBe('idle');
      expect(store.stoppedTurn()).toEqual({
        text: 'Partial ans',
        prompt: 'What is the weather?',
        selection: SELECTION,
      });
      expect(store.pendingUserText()).toBeNull();
      expect(store.entries()).toEqual([]);
    });

    it('restores the prompt and the turn settings from the stopped card, without sending', async () => {
      setup();
      const handle = await startBoundTurn();
      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Partial' })));
      await settle(STREAM_BATCH_WINDOW_MS);
      store.stop();
      await settle(STREAM_BATCH_WINDOW_MS);

      store.restoreStoppedToComposer();

      expect(applyConversationSettings).toHaveBeenCalledExactlyOnceWith({
        modelId: SELECTION.modelId,
        mcpServerIds: SELECTION.mcpServerIds,
      });
      expect(store.composerSeed()?.text).toBe('What is the weather?');
      expect(store.stoppedTurn()).toBeNull();
      expect(fetchMock).toHaveBeenCalledTimes(1);
    });

    it('honours a Finished that raced the stop: the answer is saved, so it is transcribed', async () => {
      setup();
      const handle = await startBoundTurn();

      handle.enqueue(fullTurnEvents().map(frame).join(''));
      handle.close();
      store.stop();
      await settle(STREAM_BATCH_WINDOW_MS);

      expect(store.stoppedTurn()).toBeNull();
      expect(store.entries().map((entry) => entry.kind)).toEqual(['user', 'assistant']);
    });

    it('cancels the create when stopped before it returns, reseeding the prompt with no card', async () => {
      setup();
      store.send('First prompt');
      const create = backend.expectOne(CREATE_URL);

      store.stop();
      await settle();

      expect(create.cancelled).toBe(true);
      expect(store.phase()).toBe('idle');
      expect(store.stoppedTurn()).toBeNull();
      expect(store.composerSeed()?.text).toBe('First prompt');
      expect(fetchMock).not.toHaveBeenCalled();
    });

    it('discards the abort that follows a sign-out instead of resurrecting a stopped card', async () => {
      setup();
      const handle = await startBoundTurn();
      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Partial' })));
      await settle(STREAM_BATCH_WINDOW_MS);

      TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
      await settle(STREAM_BATCH_WINDOW_MS);

      expect(store.phase()).toBe('idle');
      expect(store.stoppedTurn()).toBeNull();
      expect(store.entries()).toEqual([]);
      expect(store.snapshot().text).toBeUndefined();
    });

    it('clears the stopped card when the route moves to another conversation', async () => {
      setup();
      const handle = await startBoundTurn();
      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Partial' })));
      await settle(STREAM_BATCH_WINDOW_MS);
      store.stop();
      await settle(STREAM_BATCH_WINDOW_MS);
      expect(store.stoppedTurn()).not.toBeNull();

      const other = conversationFixture();
      store.bindRoute(other.id);
      flushHistory(other.id);

      expect(store.stoppedTurn()).toBeNull();
      expect(store.entries()).toEqual([]);
    });
  });

  describe('pre-stream failures', () => {
    async function failWith(problem: object, status: number): Promise<void> {
      store.bindRoute(CONVERSATION_ID);
      flushHistory(CONVERSATION_ID);
      fetchMock.mockResolvedValue(problemResponse(problem, status).response);
      store.send('What is the weather?');
      await settle();
    }

    it('surfaces a 502 as the tool-server-unavailable notice naming the server', async () => {
      setup();
      await failWith(PROBLEM_FIXTURES.mcpServerUnavailable, 502);

      const notice = store.turnError();
      expect(notice?.error.kind).toBe('mcp-server-unavailable');
      expect(notice?.error.kind === 'mcp-server-unavailable' && notice.error.serverName).toBe(
        'Weather',
      );
      expect(notice?.retry).toEqual({ prompt: 'What is the weather?', selection: SELECTION });
      expect(store.pendingUserText()).toBeNull();
      expect(store.phase()).toBe('idle');
    });

    it('surfaces a 409 as the conversation-busy notice (US-408)', async () => {
      setup();
      await failWith(PROBLEM_FIXTURES.conversationBusy, 409);

      expect(store.turnError()?.error.kind).toBe('conversation-busy');
      expect(store.entries()).toEqual([]);
      // The notice offers Retry, so the retry has to be the user's: the other
      // tab holds the lock for an unknown time and a schedule would either
      // fire uselessly or send a turn the user has moved on from.
      expect(store.turnError()?.retry).not.toBeNull();
    });

    it('schedules no automatic retry of a 409 at any interval (US-408)', async () => {
      setup();
      await failWith(PROBLEM_FIXTURES.conversationBusy, 409);

      await settle(60_000);

      expect(fetchMock).toHaveBeenCalledTimes(1);
      expect(store.phase()).toBe('idle');
    });

    it('surfaces a 403 consent requirement without a token refresh (US-412)', async () => {
      setup();
      await failWith(PROBLEM_FIXTURES.mcpAuthorizationRequired, 403);

      const notice = store.turnError();
      expect(notice?.error.kind).toBe('mcp-authorization-required');
      expect(notice?.error.kind === 'mcp-authorization-required' && notice.error.serverName).toBe(
        'Weather',
      );
      // One acquisition — the request's own. A 403 consent problem must never
      // take the replay path a bare 401 takes: no refresh can satisfy a consent
      // requirement, so a loop is all it would produce.
      expect(getToken).toHaveBeenCalledExactlyOnceWith(undefined);
      expect(fetchMock).toHaveBeenCalledTimes(1);
    });

    it('clears the notice when the user retries and the turn starts normally', async () => {
      setup();
      await failWith(PROBLEM_FIXTURES.conversationBusy, 409);
      const retry = store.turnError()?.retry;
      expect(retry).not.toBeNull();

      respondWithStream(streamingResponse());
      store.retryTurn(retry!);
      await settle();

      expect(store.turnError()).toBeNull();
      expect(store.phase()).toBe('awaitingFirst');
      expect(fetchMock).toHaveBeenCalledTimes(2);
    });
  });

  describe('announcing a completed turn (US-409)', () => {
    function completions(): TurnCompleted[] {
      const seen: TurnCompleted[] = [];
      TestBed.inject(Events)
        .on(turnEvents.completed)
        .subscribe(({ payload }) => seen.push(payload));
      return seen;
    }

    async function finishTurn(handle: StreamingResponseHandle): Promise<void> {
      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Sunny.' })));
      handle.enqueue(frame(assistantEvent('Finished', {})));
      handle.close();
      await settle(STREAM_BATCH_WINDOW_MS);
    }

    it('reports the first completed turn of a conversation as the first', async () => {
      setup();
      const seen = completions();

      await finishTurn(await startBoundTurn());

      expect(seen).toEqual([{ conversationId: CONVERSATION_ID, wasFirstTurn: true }]);
    });

    it('reports a later turn as not the first, so nothing refetches a settled name', async () => {
      setup();
      await finishTurn(await startBoundTurn());
      const seen = completions();

      const second = streamingResponse();
      respondWithStream(second);
      store.send('And tomorrow?');
      await settle();
      await finishTurn(second);

      expect(seen).toEqual([{ conversationId: CONVERSATION_ID, wasFirstTurn: false }]);
    });

    it('reports a conversation that already holds stored messages as not the first', async () => {
      setup();
      store.bindRoute(CONVERSATION_ID);
      flushHistory(CONVERSATION_ID, [
        { text: 'What is the weather?', role: CHAT_ROLE.user },
        { text: 'It was sunny.', role: CHAT_ROLE.assistant },
      ]);
      const seen = completions();

      const handle = streamingResponse();
      respondWithStream(handle);
      store.send('And tomorrow?');
      await settle();
      await finishTurn(handle);

      expect(seen[0]?.wasFirstTurn).toBe(false);
    });

    it('claims nothing about being first when the stored transcript could not be read', async () => {
      setup();
      store.bindRoute(CONVERSATION_ID);
      backend
        .expectOne(messagesUrl(CONVERSATION_ID))
        .flush({ title: 'Boom' }, { status: 500, statusText: 'Internal Server Error' });
      const seen = completions();

      const handle = streamingResponse();
      respondWithStream(handle);
      store.send('And tomorrow?');
      await settle();
      await finishTurn(handle);

      // An empty transcript here means unread, not new — a conversation named
      // long ago would otherwise be refetched on every turn.
      expect(seen[0]?.wasFirstTurn).toBe(false);
    });

    it('stays silent for a turn that was stopped or cut off', async () => {
      setup();
      const seen = completions();

      const cutOff = await startBoundTurn();
      cutOff.enqueue(frame(assistantEvent('TextDelta', { text: 'Half an ans' })));
      cutOff.close();
      await settle(STREAM_BATCH_WINDOW_MS);
      expect(store.entries().at(-1)?.kind).toBe('cutOff');

      const stopped = streamingResponse();
      respondWithStream(stopped);
      store.send('Again');
      await settle();
      store.stop();
      await settle(STREAM_BATCH_WINDOW_MS);

      // Neither turn left the server anything to name the conversation after.
      expect(seen).toEqual([]);
    });

    it('announces the retry that completes, not the attempt that was abandoned', async () => {
      setup();
      const seen = completions();

      const cutOff = await startBoundTurn();
      cutOff.close();
      await settle(STREAM_BATCH_WINDOW_MS);
      expect(seen).toEqual([]);

      const retried = streamingResponse();
      respondWithStream(retried);
      store.retryTurn({ prompt: 'What is the weather?', selection: SELECTION });
      await settle();
      await finishTurn(retried);

      // The abandoned attempt left a `cutOff` entry, never an `assistant` one,
      // so the turn that actually completed is still the conversation's first.
      expect(seen).toEqual([{ conversationId: CONVERSATION_ID, wasFirstTurn: true }]);
    });
  });

  describe('replaying the stored transcript (US-410)', () => {
    const HISTORY: readonly ConversationMessageDto[] = [
      { text: 'What is the weather?', role: CHAT_ROLE.user },
      { text: 'It is sunny.', role: CHAT_ROLE.assistant },
    ];

    it('renders the stored messages as transcript entries when a conversation is opened', () => {
      setup();
      store.bindRoute(CONVERSATION_ID);
      expect(store.historyPending()).toBe(true);
      expect(store.hasContent()).toBe(true);

      flushHistory(CONVERSATION_ID, HISTORY);

      expect(store.historyPending()).toBe(false);
      const entries = store.entries();
      expect(entries).toHaveLength(2);
      expect(entries[0]).toMatchObject({ kind: 'user', text: 'What is the weather?' });
      expect(entries[1]?.kind).toBe('assistant');
      // Replayed through the same fold a live turn uses, so the answer text is
      // where the transcript already looks for it.
      expect(entries[1]?.kind === 'assistant' && entries[1].snapshot.text).toBe('It is sunny.');
    });

    it('skips roles the transcript does not render', () => {
      setup();
      store.bindRoute(CONVERSATION_ID);
      flushHistory(CONVERSATION_ID, [
        { text: 'You are a helpful assistant.', role: CHAT_ROLE.system },
        { text: 'Ask the weather tool.', role: CHAT_ROLE.tool },
        { text: 'It is sunny.', role: CHAT_ROLE.assistant },
      ]);

      expect(store.entries()).toHaveLength(1);
      expect(store.entries()[0]?.kind).toBe('assistant');
    });

    it('replaces a longer local history with a shorter refetch rather than merging', () => {
      setup();
      store.bindRoute(CONVERSATION_ID);
      flushHistory(CONVERSATION_ID, HISTORY);
      expect(store.entries()).toHaveLength(2);

      // What an aborted turn leaves behind: the server transcribed neither
      // half of it, so the read back is shorter than what is on screen.
      store.retryHistory();
      flushHistory(CONVERSATION_ID, HISTORY.slice(0, 1));

      expect(store.entries()).toHaveLength(1);
      expect(store.entries()[0]).toMatchObject({ kind: 'user' });
    });

    it('keeps a turn taken during the read, and orders it after the replayed history', async () => {
      setup();
      store.bindRoute(CONVERSATION_ID);
      const pending = backend.expectOne(messagesUrl(CONVERSATION_ID));

      const handle = streamingResponse();
      respondWithStream(handle);
      store.send('And tomorrow?');
      await settle();
      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Rain.' })));
      handle.enqueue(frame(assistantEvent('Finished', {})));
      handle.close();
      await settle(STREAM_BATCH_WINDOW_MS);
      expect(store.entries()).toHaveLength(2);

      pending.flush({ id: CONVERSATION_ID, name: 'Conversation', messages: HISTORY });

      const kinds = store.entries().map((entry) => entry.kind);
      expect(kinds).toEqual(['user', 'assistant', 'user', 'assistant']);
      expect(store.entries()[2]).toMatchObject({ text: 'And tomorrow?' });
      // Two id ranges, so the replayed block can be swapped without ever
      // colliding with a live turn's `@for` key.
      expect(
        store.entries().every((entry, index) => (index < 2 ? entry.id < 0 : entry.id > 0)),
      ).toBe(true);
    });

    it('cancels the read for a conversation the user leaves before it answers', () => {
      setup();
      const other = conversationFixture();
      store.bindRoute(CONVERSATION_ID);
      const stale = backend.expectOne(messagesUrl(CONVERSATION_ID));

      store.bindRoute(other.id);

      // Cancelled, not merely ignored on arrival: the entries it carries
      // belong to a screen the user has left, and nothing may install them.
      expect(stale.cancelled).toBe(true);
      flushHistory(other.id, HISTORY.slice(0, 1));
      expect(store.entries()).toHaveLength(1);
    });

    it('surfaces a failed read without disabling the turn surface', async () => {
      setup();
      store.bindRoute(CONVERSATION_ID);
      backend
        .expectOne(messagesUrl(CONVERSATION_ID))
        .flush({ title: 'Boom' }, { status: 500, statusText: 'Internal Server Error' });

      expect(store.historyPending()).toBe(false);
      expect(store.historyError()?.status).toBe(500);

      respondWithStream(streamingResponse());
      store.send('And tomorrow?');
      await settle();
      expect(fetchMock).toHaveBeenCalledTimes(1);

      store.retryHistory();
      flushHistory(CONVERSATION_ID, HISTORY);
      expect(store.historyError()).toBeNull();
    });

    it('does not render a turn twice when a retry brings back the turn itself', async () => {
      setup();
      store.bindRoute(CONVERSATION_ID);
      backend
        .expectOne(messagesUrl(CONVERSATION_ID))
        .flush({ title: 'Boom' }, { status: 500, statusText: 'Internal Server Error' });

      // The panel keeps the composer live, so a turn can be taken and settle
      // while the stored transcript is unreadable.
      const handle = streamingResponse();
      respondWithStream(handle);
      store.send('And tomorrow?');
      await settle();
      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Rain.' })));
      handle.enqueue(frame(assistantEvent('Finished', {})));
      handle.close();
      await settle(STREAM_BATCH_WINDOW_MS);
      expect(store.entries()).toHaveLength(2);

      // The server now holds that exchange too, so a retry that kept the live
      // entries would show the prompt and the answer twice.
      store.retryHistory();
      flushHistory(CONVERSATION_ID, [
        ...HISTORY,
        { text: 'And tomorrow?', role: CHAT_ROLE.user },
        { text: 'Rain.', role: CHAT_ROLE.assistant },
      ]);

      expect(store.entries()).toHaveLength(4);
      expect(store.entries().map((entry) => entry.id > 0)).toEqual([false, false, false, false]);
    });

    it('reads nothing back on the empty chat route', () => {
      setup();
      store.bindRoute(undefined);

      backend.expectNone(messagesUrl(CONVERSATION_ID));
      expect(store.historyPending()).toBe(false);
      expect(store.hasContent()).toBe(false);
    });
  });

  describe('what a screen reader is told (US-1402)', () => {
    it('reports the folded Finished before the body has closed', async () => {
      setup();
      const handle = await startBoundTurn();

      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Hello.' })));
      await settle(STREAM_BATCH_WINDOW_MS);
      expect(store.answerComplete()).toBe(false);

      // The frame, and only the frame: the body stays open. `inFlight` is still
      // true here, which is the whole point — the transcript's `aria-busy` names
      // the frame the criterion names, not the settle a step later.
      handle.enqueue(frame(assistantEvent('Finished')));
      await settle(STREAM_BATCH_WINDOW_MS);

      expect(store.answerComplete()).toBe(true);
      expect(store.inFlight()).toBe(true);
      expect(store.phase()).toBe('streaming');

      handle.close();
      await settle(STREAM_BATCH_WINDOW_MS);
      expect(store.inFlight()).toBe(false);
    });

    it('says nothing at rest, and announces the answer as a backstop when it lands', async () => {
      setup();
      expect(store.turnStatus()).toBe('');

      const handle = await startBoundTurn();
      expect(store.turnStatus()).toBe('Waiting for a response');

      handle.enqueue(frame(assistantEvent('TextDelta', { text: 'Clear skies.' })));
      await settle(STREAM_BATCH_WINDOW_MS);
      expect(store.turnStatus()).toBe('Writing the answer');

      handle.enqueue(frame(assistantEvent('Finished')));
      await settle(STREAM_BATCH_WINDOW_MS);

      // Releasing the answer's own region is an `aria-busy` change with no
      // content mutation behind it, so it rests on assistive tech replaying what
      // it buffered. This is the channel that works either way.
      expect(store.turnStatus()).toBe('Answer ready');

      handle.close();
      await settle(STREAM_BATCH_WINDOW_MS);
      expect(store.turnStatus()).toBe('');
    });

    it('names the create round trip on a first prompt', async () => {
      setup();
      respondWithStream(streamingResponse());

      store.send('What is the weather?');

      // The one window with no stream to describe, and the one a first-time user
      // spends the longest in.
      expect(store.phase()).toBe('creating');
      expect(store.turnStatus()).toBe('Starting the conversation');

      backend.expectOne(CREATE_URL).flush(conversationFixture());
      await settle();

      expect(store.turnStatus()).toBe('Waiting for a response');
    });

    it('changes on a status change and holds still across a run of text deltas', async () => {
      setup();
      const handle = await startBoundTurn();
      const spoken: string[] = [store.turnStatus()];

      function record(): void {
        const next = store.turnStatus();
        if (next !== spoken[spoken.length - 1]) {
          spoken.push(next);
        }
      }

      handle.enqueue(frame(assistantEvent('ActivityStarted', { scopeId: 'mcp-1' })));
      await settle(STREAM_BATCH_WINDOW_MS);
      record();

      handle.enqueue(frame(assistantEvent('ActivityCompleted', { scopeId: 'mcp-1' })));
      await settle(STREAM_BATCH_WINDOW_MS);
      record();

      for (const chunk of ['The ', 'forecast ', 'is ', 'clear.']) {
        handle.enqueue(frame(assistantEvent('TextDelta', { text: chunk })));
        await settle(STREAM_BATCH_WINDOW_MS);
        record();
      }

      expect(spoken).toEqual([
        'Waiting for a response',
        'Get forecast is running',
        'Get forecast completed',
        'Writing the answer',
      ]);
    });
  });

  describe('composer seeding', () => {
    it('re-seeds the same text as a fresh object after the composer consumes it', () => {
      setup();

      store.seedComposer('Summarize a document');
      const first = store.composerSeed();
      store.consumeComposerSeed();
      expect(store.composerSeed()).toBeNull();

      store.seedComposer('Summarize a document');
      const second = store.composerSeed();

      expect(second).toEqual({ text: 'Summarize a document' });
      expect(second).not.toBe(first);
    });
  });
});
