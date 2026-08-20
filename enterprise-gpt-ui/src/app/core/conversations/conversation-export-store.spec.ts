import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { PROBLEM_FIXTURES, TRACE_ID } from '@testing/problem-fixtures';
import { ToastStore } from '@core/notifications/toast-store';
import { ConversationExportStore } from './conversation-export-store';

const CONVERSATION_ID = '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b';
const EXPORT_URL = `${TEST_API_BASE_URL}/api/conversations/${CONVERSATION_ID}/export`;

/** How the API answers: bytes, an attachment name, and no-store. */
function okResponse(fileName = 'Planning-notes.md'): {
  body: Blob;
  headers: Record<string, string>;
} {
  return {
    body: new Blob(['# Planning notes'], { type: 'text/markdown' }),
    headers: {
      'Content-Type': 'text/markdown',
      'Content-Disposition': `attachment; filename="${fileName}"; filename*=UTF-8''${encodeURIComponent(fileName)}`,
      'Cache-Control': 'no-store',
    },
  };
}

describe('ConversationExportStore', () => {
  let store: InstanceType<typeof ConversationExportStore>;
  let toasts: InstanceType<typeof ToastStore>;
  let backend: HttpTestingController;
  let clicks: HTMLAnchorElement[];
  let created: string[];
  let revoked: string[];

  beforeEach(() => {
    // File-wide, so the store's 10-second revoke timer never escapes a test as a real one.
    // `vi.clearAllTimers()` is implemented as a no-op unless fake timers are installed, so
    // without this the teardown below would claim a cleanup it was not performing.
    vi.useFakeTimers();
    clicks = [];
    created = [];
    revoked = [];
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(ConversationExportStore);
    toasts = TestBed.inject(ToastStore);

    // jsdom implements neither; the object URL is also the thing this store must let go of.
    vi.stubGlobal('URL', {
      ...URL,
      createObjectURL: vi.fn((_: Blob) => {
        const url = `blob:test/${created.length}`;
        created.push(url);

        return url;
      }),
      revokeObjectURL: vi.fn((url: string) => revoked.push(url)),
    });

    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      clicks.push(this);
    });
  });

  afterEach(() => {
    backend.verify();
    // `revokeObjectURL` resolves off the global at call time, so a revoke timer left
    // pending from one test would write into whichever stub the next test installs.
    vi.clearAllTimers();
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  /** Flushes the promise chain the failure path reads the error blob through. */
  async function flushMicrotasks(): Promise<void> {
    await vi.advanceTimersByTimeAsync(0);
  }

  it('issues nothing until a format is chosen', () => {
    backend.expectNone(() => true);
  });

  it('requests the chosen format and hands the bytes to the browser', () => {
    store.download(CONVERSATION_ID, 'md');

    const request = backend.expectOne((candidate) => candidate.url === EXPORT_URL);
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('format')).toBe('md');
    expect(request.request.responseType).toBe('blob');
    expect(store.pending()).toEqual({ conversationId: CONVERSATION_ID, format: 'md' });

    const { body, headers } = okResponse();
    request.flush(body, { headers });

    expect(clicks).toHaveLength(1);
    expect(clicks[0]?.download).toBe('Planning-notes.md');
    expect(store.pending()).toBeNull();
    expect(store.outcome()).toMatchObject({
      conversationId: CONVERSATION_ID,
      format: 'md',
      ok: true,
    });
  });

  /**
   * A blob URL holds the whole rendered document in memory until it is revoked, and a
   * transcript is the most sensitive thing this app renders. The delay is the other half:
   * revoking before the browser has read the URL cancels the download it was just handed.
   */
  it('revokes the object URL, but only after the browser has had time to read it', async () => {
    store.download(CONVERSATION_ID, 'pdf');
    const { body, headers } = okResponse('Planning-notes.pdf');
    backend.expectOne((candidate) => candidate.url === EXPORT_URL).flush(body, { headers });

    expect(created).toHaveLength(1);
    // Not synchronously, and not on the next task either.
    expect(revoked).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(0);
    expect(revoked).toHaveLength(0);

    await vi.advanceTimersByTimeAsync(10_000);
    expect(revoked).toEqual(created);
  });

  /**
   * The name is what lands on the reader's disk, and a conversation named in any script
   * travels only in the RFC 5987 form.
   */
  it('prefers the encoded filename* over the plain filename', () => {
    store.download(CONVERSATION_ID, 'docx');

    backend
      .expectOne((candidate) => candidate.url === EXPORT_URL)
      .flush(new Blob(['x']), {
        headers: {
          'Content-Disposition':
            'attachment; filename="?????.docx"; filename*=UTF-8\'\'%D0%9F%D0%BB%D0%B0%D0%BD.docx',
        },
      });

    expect(clicks[0]?.download).toBe('План.docx');
  });

  it('falls back to a built name when the server sent no disposition', () => {
    store.download(CONVERSATION_ID, 'md');

    backend.expectOne((candidate) => candidate.url === EXPORT_URL).flush(new Blob(['x']));

    expect(clicks[0]?.download).toBe(`conversation-${CONVERSATION_ID}.md`);
  });

  /**
   * The defect this store's `responseType: 'blob'` would otherwise cause: `HttpClient`
   * leaves an error body as a `Blob`, `parseProblemDetails` rejects it, and the arm
   * degrades to plain `http` — losing the one type that says "not available here".
   */
  it('reads the problem body back out of the error blob', async () => {
    store.download(CONVERSATION_ID, 'pdf');

    backend
      .expectOne((candidate) => candidate.url === EXPORT_URL)
      .flush(new Blob([JSON.stringify(PROBLEM_FIXTURES.exportRendererNotConfigured)]), {
        status: 503,
        statusText: 'Service Unavailable',
        headers: { 'Content-Type': 'application/problem+json' },
      });

    await flushMicrotasks();
    expect(toasts.toasts()).toHaveLength(1);

    const toast = toasts.toasts()[0];
    expect(toast?.title).toBe('PDF download isn’t available in this environment.');
    expect(toast?.traceLine).toContain(TRACE_ID);
  });

  /** Criterion 4: the menu returns to idle, and the outcome says it failed. */
  it('settles as a failure without clearing the outcome sequence', async () => {
    store.download(CONVERSATION_ID, 'pdf');
    backend
      .expectOne((candidate) => candidate.url === EXPORT_URL)
      .flush(new Blob(['nope']), { status: 500, statusText: 'Server Error' });

    expect(store.pending()).toBeNull();
    expect(store.outcome()).toMatchObject({ ok: false, format: 'pdf' });
    expect(clicks).toHaveLength(0);

    await flushMicrotasks();
    expect(toasts.toasts()).toHaveLength(1);
  });

  /** Each settle gets its own sequence, so two mounted menus each react exactly once. */
  it('increments the outcome sequence per settle', () => {
    store.download(CONVERSATION_ID, 'md');
    backend.expectOne((candidate) => candidate.url === EXPORT_URL).flush(new Blob(['a']));
    const first = store.outcome()?.seq ?? 0;

    store.download(CONVERSATION_ID, 'docx');
    backend.expectOne((candidate) => candidate.url === EXPORT_URL).flush(new Blob(['b']));

    expect(store.outcome()?.seq).toBe(first + 1);
  });

  /** A second press while one is out is a double-click, not a second file. */
  it('ignores a second request while one is in flight', () => {
    store.download(CONVERSATION_ID, 'md');
    store.download(CONVERSATION_ID, 'pdf');

    const requests = backend.match((candidate) => candidate.url === EXPORT_URL);
    expect(requests).toHaveLength(1);
    expect(requests[0]?.request.params.get('format')).toBe('md');

    requests[0]?.flush(new Blob(['x']));
  });

  /**
   * The request opts out of `retryInterceptor`: its error body is a `Blob` the interceptor
   * cannot classify, so an application-typed 503 would be replayed three times.
   */
  it('is not retried on a 503', () => {
    store.download(CONVERSATION_ID, 'pdf');

    backend
      .expectOne((candidate) => candidate.url === EXPORT_URL)
      .flush(new Blob(['x']), { status: 503, statusText: 'Service Unavailable' });

    backend.expectNone((candidate) => candidate.url === EXPORT_URL);
  });

  /** A response with no body is a server contract break, not a file. */
  it('reports a bodyless 200 rather than saving nothing', () => {
    store.download(CONVERSATION_ID, 'md');

    backend.expectOne((candidate) => candidate.url === EXPORT_URL).flush(null);

    expect(clicks).toHaveLength(0);
    expect(store.outcome()).toMatchObject({ ok: false });
    expect(toasts.toasts()[0]?.title).toContain('Markdown');
  });

  it('normalizes a transport failure without a blob body', async () => {
    store.download(CONVERSATION_ID, 'md');

    backend
      .expectOne((candidate) => candidate.url === EXPORT_URL)
      .error(new ProgressEvent('error') as unknown as HttpErrorResponse['error'], { status: 0 });

    expect(store.outcome()).toMatchObject({ ok: false });
    await flushMicrotasks();
    expect(toasts.toasts()).toHaveLength(1);
  });
});
