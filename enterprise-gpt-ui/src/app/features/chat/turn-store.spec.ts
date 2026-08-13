import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture } from '@testing/conversations';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import {
  StreamingResponseHandle,
  assistantEvent,
  frame,
  fullTurnEvents,
  problemResponse,
  streamingResponse,
} from '@testing/stream-frames';
import { TurnSelection, TurnSettingsStore } from '@core/chat/turn-settings-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { sessionEvents } from '@core/events/session-events';
import { TokenService } from '@core/auth/token-service';
import { STREAM_BATCH_WINDOW_MS } from '@core/stream/conversation-stream-client';
import { STREAM_FETCH } from '@core/stream/stream-fetch.token';
import { TranscriptEntry, TurnStore } from './turn-store';

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
  let selection: TurnSelection | null;

  beforeEach(() => {
    vi.useFakeTimers();
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

    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: STREAM_FETCH, useValue: fetchMock },
        { provide: TokenService, useValue: { getToken: vi.fn().mockResolvedValue('token-1') } },
        { provide: Router, useValue: { navigateByUrl } },
        {
          provide: TurnSettingsStore,
          useValue: {
            streamSelection: () => selection,
            applyConversationSettings,
          },
        },
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

  /** Binds an existing conversation and opens a live stream for it. */
  async function startBoundTurn(handle = streamingResponse()): Promise<StreamingResponseHandle> {
    store.bindRoute(CONVERSATION_ID);
    respondWithStream(handle);
    store.send('What is the weather?');
    await settle();
    return handle;
  }

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

      store.bindRoute(conversationFixture().id);

      expect(store.stoppedTurn()).toBeNull();
      expect(store.entries()).toEqual([]);
    });
  });

  describe('pre-stream failures', () => {
    async function failWith(problem: object, status: number): Promise<void> {
      store.bindRoute(CONVERSATION_ID);
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

    it('surfaces a 409 as the conversation-busy notice', async () => {
      setup();
      await failWith(PROBLEM_FIXTURES.conversationBusy, 409);

      expect(store.turnError()?.error.kind).toBe('conversation-busy');
      expect(store.entries()).toEqual([]);
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
