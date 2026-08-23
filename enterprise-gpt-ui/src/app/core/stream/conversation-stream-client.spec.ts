import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { FRAMEWORK_PROBLEM_FIXTURES, PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import {
  StreamingResponseHandle,
  assistantEvent,
  frame,
  fullTurnEvents,
  problemResponse,
  streamingResponse,
} from '@testing/stream-frames';
import { AppError } from '@core/errors/app-error';
import { AppConfig } from '@core/config/app-config';
import { sessionEvents } from '@core/events/session-events';
import { TokenService } from '@core/auth/token-service';
import { AssistantUiEvent } from '@domain/stream/andes/assistant-ui.contract';
import {
  ConversationStreamClient,
  STREAM_BATCH_WINDOW_MS,
  StreamTurnRequest,
} from './conversation-stream-client';
import { STREAM_FETCH } from './stream-fetch.token';

const CONVERSATION_ID = '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b';
const STREAM_URL = `${TEST_API_BASE_URL}/api/conversations/${CONVERSATION_ID}/stream`;

function turnRequest(overrides: Partial<StreamTurnRequest> = {}): StreamTurnRequest {
  return {
    conversationId: CONVERSATION_ID,
    prompt: 'What is the weather?',
    modelId: 'model-1',
    mcpServerIds: [],
    ...overrides,
  };
}

/** What one subscription observed, for asserting endings without callbacks. */
interface Observed {
  readonly batches: (readonly AssistantUiEvent[])[];
  error: AppError | null;
  completed: boolean;
}

describe('ConversationStreamClient', () => {
  let fetchMock: ReturnType<typeof vi.fn>;
  let getToken: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function setup(config: Partial<AppConfig> = {}): ConversationStreamClient {
    fetchMock = vi.fn();
    getToken = vi.fn().mockResolvedValue('token-1');

    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(config),
        { provide: STREAM_FETCH, useValue: fetchMock },
        { provide: TokenService, useValue: { getToken } },
      ],
    });

    return TestBed.inject(ConversationStreamClient);
  }

  function respondWithStream(handle: StreamingResponseHandle): void {
    fetchMock.mockImplementation((_input: unknown, init?: RequestInit) => {
      handle.abortOn(init?.signal);
      return Promise.resolve(handle.response);
    });
  }

  function observe(client: ConversationStreamClient, request = turnRequest()): Observed {
    const observed: Observed = { batches: [], error: null, completed: false };
    client.stream(request).subscribe({
      next: (batch) => observed.batches.push(batch),
      error: (error: AppError) => (observed.error = error),
      complete: () => (observed.completed = true),
    });
    return observed;
  }

  /** Advances fake time, draining the microtasks the async read loop awaits on. */
  async function settle(ms = 0): Promise<void> {
    await vi.advanceTimersByTimeAsync(ms);
  }

  it('POSTs the documented request shape, with mcpServers always present', async () => {
    const client = setup();
    const handle = streamingResponse();
    respondWithStream(handle);

    observe(client, turnRequest({ mcpServerIds: ['mcp-a', 'mcp-b'] }));
    await settle();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe(STREAM_URL);
    expect(init.method).toBe('POST');
    expect(init.headers).toEqual({
      'Content-Type': 'application/json',
      Accept: 'text/event-stream',
      Authorization: 'Bearer token-1',
    });
    expect(JSON.parse(init.body as string)).toEqual({
      prompt: 'What is the weather?',
      modelId: 'model-1',
      mcpServers: [{ id: 'mcp-a' }, { id: 'mcp-b' }],
    });
  });

  it('encodes the conversation id as a single path segment', async () => {
    const client = setup();
    respondWithStream(streamingResponse());

    observe(client, turnRequest({ conversationId: 'a/../b' }));
    await settle();

    const [url] = fetchMock.mock.calls[0] as [string];
    expect(url).toBe(
      `${TEST_API_BASE_URL}/api/conversations/${encodeURIComponent('a/../b')}/stream`,
    );
  });

  it('streams a full turn over a synthetic body and completes after the residual flush', async () => {
    const client = setup();
    const handle = streamingResponse();
    respondWithStream(handle);
    const events = fullTurnEvents();

    const observed = observe(client);
    await settle();

    handle.enqueue(events.slice(0, 5).map(frame).join(''));
    await settle(STREAM_BATCH_WINDOW_MS);
    handle.enqueue(events.slice(5).map(frame).join(''));
    handle.close();
    await settle();

    expect(observed.batches.flat()).toEqual(events);
    expect(observed.batches.every((batch) => batch.length > 0)).toBe(true);
    expect(observed.completed).toBe(true);
    expect(observed.error).toBeNull();
  });

  it('delivers at most one batch per buffer window at any enqueue rate', async () => {
    const client = setup();
    const handle = streamingResponse();
    respondWithStream(handle);

    const observed = observe(client);
    await settle();

    for (let index = 0; index < 5; index++) {
      handle.enqueue(frame(assistantEvent('TextDelta', { text: `chunk ${index}` })));
    }
    await settle(STREAM_BATCH_WINDOW_MS);
    expect(observed.batches).toHaveLength(1);
    expect(observed.batches[0]).toHaveLength(5);

    handle.enqueue(frame(assistantEvent('TextDelta')));
    handle.enqueue(frame(assistantEvent('Finished')));
    await settle(STREAM_BATCH_WINDOW_MS);
    expect(observed.batches).toHaveLength(2);
    expect(observed.batches[1]).toHaveLength(2);
  });

  it('parses each pre-stream problem before reading a single body byte', async () => {
    const client = setup();
    const cases = [
      { fixture: PROBLEM_FIXTURES.validationError, kind: 'validation-error' },
      { fixture: PROBLEM_FIXTURES.forbidden, kind: 'forbidden' },
      { fixture: PROBLEM_FIXTURES.resourceNotFound, kind: 'resource-not-found' },
      { fixture: PROBLEM_FIXTURES.conversationBusy, kind: 'conversation-busy' },
      { fixture: PROBLEM_FIXTURES.mcpServerUnavailable, kind: 'mcp-server-unavailable' },
      { fixture: PROBLEM_FIXTURES.providerNotConfigured, kind: 'provider-not-configured' },
    ] as const;

    for (const { fixture, kind } of cases) {
      const { response, getReader } = problemResponse(fixture, fixture.status);
      fetchMock.mockResolvedValueOnce(response);

      const observed = observe(client);
      await settle();

      expect(observed.error?.kind).toBe(kind);
      expect(observed.error?.url).toBe(STREAM_URL);
      expect(observed.batches).toEqual([]);
      expect(getReader).not.toHaveBeenCalled();
    }
  });

  it('never refreshes the token for a 403', async () => {
    const client = setup();
    const { response, getReader } = problemResponse(PROBLEM_FIXTURES.forbidden, 403);
    fetchMock.mockResolvedValue(response);

    const observed = observe(client);
    await settle();

    expect(observed.error?.kind).toBe('forbidden');
    expect(getToken).toHaveBeenCalledTimes(1);
    expect(getToken).not.toHaveBeenCalledWith({ forceRefresh: true });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(getReader).not.toHaveBeenCalled();
  });

  it('replays a bare 401 exactly once with a force-refreshed token', async () => {
    const client = setup();
    getToken.mockResolvedValueOnce('token-1').mockResolvedValueOnce('token-2');
    const handle = streamingResponse();
    fetchMock
      .mockResolvedValueOnce(problemResponse(FRAMEWORK_PROBLEM_FIXTURES.unauthorized, 401).response)
      .mockImplementation((_input: unknown, init?: RequestInit) => {
        handle.abortOn(init?.signal);
        return Promise.resolve(handle.response);
      });

    const observed = observe(client);
    await settle();

    handle.enqueue(frame(assistantEvent('TextDelta')));
    handle.close();
    await settle();

    expect(getToken).toHaveBeenNthCalledWith(2, { forceRefresh: true });
    const [, retryInit] = fetchMock.mock.calls[1] as [string, RequestInit];
    expect((retryInit.headers as Record<string, string>)['Authorization']).toBe('Bearer token-2');
    expect(observed.completed).toBe(true);
    expect(observed.batches.flat()).toHaveLength(1);
  });

  it('gives up after a second 401 without looping', async () => {
    const client = setup();
    fetchMock
      .mockResolvedValueOnce(problemResponse(FRAMEWORK_PROBLEM_FIXTURES.unauthorized, 401).response)
      .mockResolvedValueOnce(
        problemResponse(FRAMEWORK_PROBLEM_FIXTURES.unauthorized, 401).response,
      );

    const observed = observe(client);
    await settle();

    expect(observed.error?.kind).toBe('http');
    expect(observed.error?.status).toBe(401);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('errors with the client arm when no token can be produced, without fetching', async () => {
    const client = setup();
    getToken.mockRejectedValue(
      Object.assign(new Error('No signed-in account.'), { name: 'InteractiveSignInRequiredError' }),
    );

    const observed = observe(client);
    await settle();

    expect(observed.error?.kind).toBe('client');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("errors with the aborted arm on the caller's Stop, keeping delivered batches", async () => {
    const client = setup();
    const handle = streamingResponse();
    respondWithStream(handle);
    const stop = new AbortController();

    const observed = observe(client, turnRequest({ signal: stop.signal }));
    await settle();

    handle.enqueue(frame(assistantEvent('TextDelta')));
    await settle(STREAM_BATCH_WINDOW_MS);
    expect(observed.batches).toHaveLength(1);

    stop.abort();
    await settle();

    expect(observed.error?.kind).toBe('aborted');
    expect(observed.completed).toBe(false);
    expect(observed.batches).toHaveLength(1);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('aborts the stream on sign-out', async () => {
    const client = setup();
    const handle = streamingResponse();
    respondWithStream(handle);

    const observed = observe(client);
    await settle();

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    await settle();

    expect(observed.error?.kind).toBe('aborted');
  });

  it('aborts the underlying request when the subscriber unsubscribes', async () => {
    const client = setup();
    const handle = streamingResponse();
    respondWithStream(handle);

    const subscription = client.stream(turnRequest()).subscribe();
    await settle();

    subscription.unsubscribe();

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(init.signal?.aborted).toBe(true);
  });

  it('completes gracefully when the body faults after the first frame', async () => {
    const client = setup();
    const handle = streamingResponse();
    respondWithStream(handle);

    const observed = observe(client);
    await settle();

    handle.enqueue(frame(assistantEvent('TextDelta')));
    await settle(STREAM_BATCH_WINDOW_MS);
    handle.fail(new TypeError('Failed to fetch'));
    await settle();

    expect(observed.completed).toBe(true);
    expect(observed.error).toBeNull();
    expect(observed.batches).toHaveLength(1);
  });

  it('salvages the buffered tail when the body faults instead of closing', async () => {
    // The contract's two "body just stops" endings — clean close and faulting
    // read — must deliver the same text: the final frame often loses its
    // terminator to the truncation.
    const client = setup();
    const handle = streamingResponse();
    respondWithStream(handle);
    const lastWords = assistantEvent('TextDelta', { text: 'the last words' });

    const observed = observe(client);
    await settle();

    handle.enqueue(frame(lastWords).slice(0, -2));
    await settle(STREAM_BATCH_WINDOW_MS);
    expect(observed.batches).toEqual([]);

    handle.fail(new TypeError('connection reset'));
    await settle();

    expect(observed.completed).toBe(true);
    expect(observed.error).toBeNull();
    expect(observed.batches.flat()).toEqual([lastWords]);
  });

  it('errors with the network arm when the fetch itself rejects', async () => {
    const client = setup();
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'));

    const observed = observe(client);
    await settle();

    expect(observed.error?.kind).toBe('network');
  });

  it('completes with zero emissions for an empty stream and for a null body', async () => {
    const client = setup();
    const handle = streamingResponse();
    respondWithStream(handle);

    const emptyStream = observe(client);
    await settle();
    handle.close();
    await settle();

    expect(emptyStream.completed).toBe(true);
    expect(emptyStream.batches).toEqual([]);

    fetchMock.mockResolvedValue(streamingResponse({ body: null }).response);
    const nullBody = observe(client);
    await settle();

    expect(nullBody.completed).toBe(true);
    expect(nullBody.batches).toEqual([]);
  });

  it('decodes unframed text through the raw codec when the config flag selects it', async () => {
    const client = setup({ features: { diagrams: false, math: false, rawStreamCodec: true } });
    const handle = streamingResponse();
    respondWithStream(handle);

    const observed = observe(client);
    await settle();

    handle.enqueue('Plain answer text');
    handle.close();
    await settle();

    const events = observed.batches.flat();
    expect(events).toHaveLength(1);
    expect(events[0]).toMatchObject({ kind: 'TextDelta', text: 'Plain answer text' });
    expect(observed.completed).toBe(true);
  });
});
