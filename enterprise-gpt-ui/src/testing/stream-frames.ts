import { vi } from 'vitest';
import {
  AssistantUiEvent,
  AssistantUiEventKind,
} from '../app/domain/stream/andes/assistant-ui.contract';

/** A representative wire timestamp; the contract forbids ordering by it. */
export const EVENT_TIMESTAMP = '2026-08-12T09:00:00.0000000+00:00';

/**
 * One wire-accurate canned event per kind: camelCase keys, string enums, and
 * only the fields the serializer would emit for that kind (`nulls omitted`).
 */
const CANNED_EVENTS = {
  Status: {
    kind: 'Status',
    message: 'Reasoning…',
    depth: 0,
    toolKind: 'Unknown',
    timestamp: EVENT_TIMESTAMP,
  },
  ActivityStarted: {
    kind: 'ActivityStarted',
    // Since Andes 0.7.0 an MCP activity's `displayName` is the tool the model called, which for a
    // leased tool is `{sanitizedServer}_{tool}` — so this carries the prefix the client strips.
    // The server name stays in `source`.
    scopeId: 'scope-1',
    displayName: 'Weather_get_forecast',
    source: 'Weather',
    depth: 1,
    toolKind: 'McpTool',
    timestamp: EVENT_TIMESTAMP,
  },
  ActivityProgress: {
    kind: 'ActivityProgress',
    scopeId: 'scope-1',
    message: 'Fetching forecast…',
    progress: 1,
    progressTotal: 3,
    depth: 2,
    toolKind: 'McpTool',
    timestamp: EVENT_TIMESTAMP,
  },
  ActivityCompleted: {
    kind: 'ActivityCompleted',
    scopeId: 'scope-1',
    durationSeconds: 1.4,
    depth: 1,
    toolKind: 'McpTool',
    timestamp: EVENT_TIMESTAMP,
  },
  ActivityFailed: {
    kind: 'ActivityFailed',
    scopeId: 'scope-1',
    durationSeconds: 0.6,
    depth: 1,
    toolKind: 'McpTool',
    timestamp: EVENT_TIMESTAMP,
  },
  TextDelta: {
    kind: 'TextDelta',
    text: 'Hello',
    depth: 0,
    toolKind: 'Unknown',
    timestamp: EVENT_TIMESTAMP,
  },
  ReasoningDelta: {
    kind: 'ReasoningDelta',
    text: 'The user asked about the weather. ',
    depth: 0,
    toolKind: 'Unknown',
    timestamp: EVENT_TIMESTAMP,
  },
  Finished: {
    kind: 'Finished',
    durationSeconds: 4.2,
    usage: { inputTokens: 1842, outputTokens: 512, totalTokens: 2354 },
    depth: 0,
    toolKind: 'Unknown',
    timestamp: EVENT_TIMESTAMP,
  },
} as const satisfies Record<AssistantUiEventKind, AssistantUiEvent>;

/** An {@link AssistantUiEvent} of the given kind, wire-shaped, with overrides. */
export function assistantEvent(
  kind: AssistantUiEventKind,
  overrides: Partial<AssistantUiEvent> = {},
): AssistantUiEvent {
  return { ...CANNED_EVENTS[kind], ...overrides };
}

/**
 * A recorded turn covering all eight kinds, with a nested child activity and a
 * failed sibling alongside a completed answer (the design's frame `1f` shape).
 * The shared input for the fold spec, the codec spec, and the transport spec.
 */
export function fullTurnEvents(): AssistantUiEvent[] {
  return [
    assistantEvent('Status'),
    assistantEvent('ActivityStarted', { scopeId: 'mcp-1' }),
    assistantEvent('ActivityProgress', { scopeId: 'mcp-1' }),
    assistantEvent('ActivityStarted', {
      scopeId: 'agent-1',
      parentScopeId: 'mcp-1',
      displayName: 'Forecast Agent',
      source: 'agent:forecast',
      toolKind: 'Agent',
      depth: 2,
    }),
    assistantEvent('ActivityCompleted', { scopeId: 'agent-1', toolKind: 'Agent', depth: 2 }),
    assistantEvent('ActivityStarted', {
      scopeId: 'fn-1',
      displayName: 'Search Documents',
      source: undefined,
      toolKind: 'Function',
    }),
    assistantEvent('ActivityFailed', { scopeId: 'fn-1', toolKind: 'Function' }),
    assistantEvent('ActivityCompleted', { scopeId: 'mcp-1' }),
    assistantEvent('ReasoningDelta'),
    assistantEvent('TextDelta', { text: 'Hello, ' }),
    assistantEvent('TextDelta', { text: 'world.' }),
    assistantEvent('Finished'),
  ];
}

/** Serializes one event exactly as the server frames it. */
export function frame(event: AssistantUiEvent): string {
  return `data: ${JSON.stringify(event)}\n\n`;
}

export function encode(text: string): Uint8Array {
  return new TextEncoder().encode(text);
}

/** Splits a byte array at an arbitrary index, for chunk-boundary fixtures. */
export function splitBytes(bytes: Uint8Array, at: number): [Uint8Array, Uint8Array] {
  return [bytes.slice(0, at), bytes.slice(at)];
}

/** Drives a synthetic streaming response chunk by chunk. */
export interface StreamingResponseHandle {
  /** Structural, cast once — realm-safe under jsdom, per `ResponseLike`'s rationale. */
  readonly response: Response;
  enqueue(text: string): void;
  enqueueBytes(bytes: Uint8Array): void;
  /** Ends the body cleanly (the contract's only terminator). */
  close(): void;
  /** Faults the body mid-stream, as a dropped connection would. */
  fail(reason: unknown): void;
  /**
   * Emulates real `fetch` cancellation: when the request's signal aborts, the
   * pending and all future reads reject with an `AbortError` `DOMException`.
   * Call from the fake fetch with the `RequestInit.signal` it received.
   */
  abortOn(signal: AbortSignal | null | undefined): void;
}

/**
 * A 200 `text/event-stream` response over a synthetic `ReadableStream`
 * (US-405's "no browser and no backend" criterion). Pass `body: null` for the
 * degenerate headers-only response.
 */
export function streamingResponse(
  options: { body?: 'stream' | null } = {},
): StreamingResponseHandle {
  let controller!: ReadableStreamDefaultController<Uint8Array>;
  const stream = new ReadableStream<Uint8Array>({
    start(c) {
      controller = c;
    },
  });
  let ended = false;

  const response = {
    ok: true,
    status: 200,
    url: '',
    bodyUsed: false,
    headers: {
      get: (name: string) => (name.toLowerCase() === 'content-type' ? 'text/event-stream' : null),
    },
    body: options.body === null ? null : stream,
    text: () => Promise.resolve(''),
  } as unknown as Response;

  return {
    response,
    enqueue: (text) => controller.enqueue(encode(text)),
    enqueueBytes: (bytes) => controller.enqueue(bytes),
    close: () => {
      ended = true;
      controller.close();
    },
    fail: (reason) => {
      ended = true;
      controller.error(reason);
    },
    abortOn: (signal) => {
      signal?.addEventListener('abort', () => {
        if (!ended) {
          ended = true;
          controller.error(new DOMException('The operation was aborted.', 'AbortError'));
        }
      });
    },
  };
}

/**
 * A pre-stream failure response: `application/problem+json`, with a spying
 * `body.getReader` so specs can assert the transport never touched the body
 * before parsing the problem.
 */
export function problemResponse(
  body: object,
  status: number,
): { response: Response; getReader: ReturnType<typeof vi.fn> } {
  const getReader = vi.fn();
  const response = {
    ok: status >= 200 && status < 300,
    status,
    url: '',
    bodyUsed: false,
    headers: {
      get: (name: string) =>
        name.toLowerCase() === 'content-type' ? 'application/problem+json' : null,
    },
    body: { getReader },
    text: () => Promise.resolve(JSON.stringify(body)),
  } as unknown as Response;

  return { response, getReader };
}
