import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom, timeout } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { textFile } from '@testing/drag';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { FakeUploadXhrQueue, provideFakeUploadXhr } from '@testing/upload-xhr';
import { JobStatusDto } from '@domain/api/document';
import { TokenService } from '@core/auth/token-service';
import { SupportedExtensionsStore } from '@core/documents/supported-extensions-store';
import { ToastStore } from '@core/notifications/toast-store';
import { UploadStore } from './upload-store';

const CONVERSATION_ID = '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b';
const OTHER_ID = '11111111-2222-4333-8444-555555555555';
const JOB_ID = 'b0f2c1a4-1f3e-4b7a-9a2e-5c6d7e8f9a0b';
const DOC_ID = '9c8b7a65-4321-4fed-8cba-0987654321fe';
const EXTENSIONS_URL = `${TEST_API_BASE_URL}/api/documents/file-extensions`;
const STATUS_URL = `${TEST_API_BASE_URL}/api/documents/upload-status/${JOB_ID}`;
const SUPPORTED = ['.doc', '.docx', '.md', '.pdf', '.pptx', '.txt'];

/** A file whose `size` is the 212 MB frame `4l` quotes back to the user. */
function oversizeFile(name: string): File {
  const file = textFile(name);
  Object.defineProperty(file, 'size', { value: 222_298_112 });

  return file;
}

/** One poll of the status route, with the server's own defaults. */
function jobStatus(overrides: Partial<JobStatusDto> = {}): JobStatusDto {
  return {
    id: JOB_ID,
    state: 'Processing',
    status: 'Embedding',
    progress: 50,
    message: null,
    completedUnits: null,
    totalUnits: null,
    documentId: null,
    errorMessage: null,
    updatedAt: '2026-08-14T09:14:23.117+00:00',
    ...overrides,
  };
}

describe('UploadStore', () => {
  let store: InstanceType<typeof UploadStore>;
  let toasts: InstanceType<typeof ToastStore>;
  let backend: HttpTestingController;
  let xhr: FakeUploadXhrQueue;

  beforeEach(async () => {
    // The poll schedule is the thing under test in half of these, and a real 500ms
    // wait per step would make the file take seconds to run.
    vi.useFakeTimers();
    xhr = new FakeUploadXhrQueue();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: TokenService, useValue: { getToken: vi.fn().mockResolvedValue('token-1') } },
        provideFakeUploadXhr(xhr),
        UploadStore,
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(UploadStore);
    toasts = TestBed.inject(ToastStore);

    const extensions = TestBed.inject(SupportedExtensionsStore).ensureLoaded();
    backend.expectOne(EXTENSIONS_URL).flush(SUPPORTED);
    await extensions;
  });

  afterEach(() => {
    backend.verify();
    vi.useRealTimers();
  });

  /** Lets the transport's token promise resolve so the request is issued. */
  const settle = () => Promise.resolve().then(() => Promise.resolve());

  const state = (index = 0) => store.attachments()[index]?.state;

  it('holds an attachment before a conversation exists, and uploads it on binding', async () => {
    store.attach([textFile('notes.txt')]);
    await settle();

    // Deferred: no conversation to post it to yet — Send is what creates one.
    expect(xhr.requests).toHaveLength(0);
    expect(state()).toEqual({ kind: 'uploading', percent: 0 });
    expect(store.blocksSend()).toBe(false);

    store.bindConversation(CONVERSATION_ID);
    await settle();

    expect(xhr.opened).toEqual([
      ['POST', `${TEST_API_BASE_URL}/api/documents/conversations/${CONVERSATION_ID}`],
    ]);
    expect(store.blocksSend()).toBe(true);
  });

  it('binds the same conversation twice without re-posting anything', async () => {
    store.bindConversation(CONVERSATION_ID);
    store.attach([textFile('notes.txt')]);
    await settle();

    // Both the create path and the route call this; the second must be a no-op.
    store.bindConversation(CONVERSATION_ID);
    await settle();

    expect(xhr.requests).toHaveLength(1);
    expect(store.attachments()).toHaveLength(1);
  });

  it('drops the chips when a different conversation is opened', async () => {
    store.bindConversation(CONVERSATION_ID);
    store.attach([textFile('notes.txt')]);
    await settle();

    store.bindConversation(OTHER_ID);

    // The files described a draft on a conversation the user has left.
    expect(store.attachments()).toEqual([]);
    expect(xhr.last.aborted).toBe(true);
  });

  it('releases a waiting send gate when the chips are dropped', async () => {
    store.bindConversation(CONVERSATION_ID);
    store.attach([textFile('notes.txt')]);
    await settle();

    const settled = firstValueFrom(store.settled$().pipe(timeout(50)));
    store.bindConversation(OTHER_ID);

    // An emptied list is a settled one. Leaving this unsaid strands `TurnStore`'s
    // gate inside `exhaustMap`, which then silently swallows every later send.
    await expect(settled).resolves.toBeUndefined();
  });

  it('transfers at most three files at once', async () => {
    store.bindConversation(CONVERSATION_ID);
    store.attach([
      textFile('a.txt'),
      textFile('b.txt'),
      textFile('c.txt'),
      textFile('d.txt'),
      textFile('e.txt'),
    ]);
    await settle();

    // The origin allows six connections in total, shared with the SSE stream and
    // the status polls; starting all five would queue the polls behind them.
    expect(xhr.requests).toHaveLength(3);

    // A 202 does **not** free the slot: the budget spans transfer and ingest
    // together, or fifty poll loops would simply accumulate behind three transfers.
    xhr.requests[0]!.respond(202, { id: JOB_ID });
    await settle();
    expect(xhr.requests).toHaveLength(3);

    await vi.advanceTimersByTimeAsync(5_000);
    backend.expectOne(STATUS_URL).flush(jobStatus({ state: 'Succeeded', documentId: DOC_ID }));
    await settle();

    expect(xhr.requests).toHaveLength(4);
  });

  it('moves a chip from bytes to ingest as the transport reports them', async () => {
    store.bindConversation(CONVERSATION_ID);
    store.attach([textFile('notes.txt')]);
    await settle();

    xhr.last.progress(21, 50);
    expect(state()).toEqual({ kind: 'uploading', percent: 42 });

    xhr.last.respond(202, { id: 'job-1' });
    await settle();
    expect(state()).toEqual({ kind: 'processing', subStatus: 'Waiting to start' });
  });

  it('refuses an unsupported extension before any request, naming the supported set', async () => {
    store.bindConversation(CONVERSATION_ID);
    store.attach([textFile('demo-capture.mov')]);
    await settle();

    expect(xhr.requests).toHaveLength(0);
    expect(state()).toEqual({
      kind: 'unsupported',
      reason: '.mov isn’t supported — DOC, DOCX, MD, PDF, PPTX, TXT',
    });
    // Nothing is outstanding: a refused file will not change again, and holding
    // Send for it would leave the user unable to send at all.
    expect(store.blocksSend()).toBe(false);
  });

  it('reports a rejected upload on the chip and in a persistent toast (US-805)', async () => {
    store.bindConversation(CONVERSATION_ID);
    store.attach([oversizeFile('big-scan.pdf')]);
    await settle();

    xhr.last.respond(400, PROBLEM_FIXTURES.uploadTooLarge);
    await settle();

    expect(state()).toEqual({
      kind: 'failed',
      reason: 'Files can be up to 50 MB — this one is 212 MB.',
    });

    const toast = toasts.toasts()[0];
    expect(toast?.title).toBe('big-scan.pdf couldn’t be uploaded');
    expect(toast?.detail).toBe('Files can be up to 50 MB — this one is 212 MB.');
    expect(toast?.traceLine).toContain('4bf92f3577b34da6a3ce929d0e0e4736');
    expect(toast?.retryLabel).toBe('Retry');
    // Zero means it stays until dismissed: an error that disappears before it is
    // read is a defect (frame 4l).
    expect(toast?.dismissAfterMs).toBe(0);
    expect(store.blocksSend()).toBe(false);
  });

  it('re-posts the file when the toast’s Retry is used (US-805)', async () => {
    store.bindConversation(CONVERSATION_ID);
    store.attach([oversizeFile('big-scan.pdf')]);
    await settle();
    xhr.last.respond(400, PROBLEM_FIXTURES.uploadTooLarge);
    await settle();

    toasts.retry(toasts.toasts()[0]!.id);
    await settle();

    expect(xhr.requests).toHaveLength(2);
    expect(state()).toEqual({ kind: 'uploading', percent: 0 });
    expect(toasts.toasts()).toHaveLength(0);
  });

  it('offers no retry for a missing grant, which retrying cannot supply (US-805)', async () => {
    store.bindConversation(CONVERSATION_ID);
    store.attach([textFile('notes.txt')]);
    await settle();

    xhr.last.respond(403, PROBLEM_FIXTURES.permissionRequired);
    await settle();

    const toast = toasts.toasts()[0];
    expect(toast?.title).toBe('You can’t upload files');
    expect(toast?.detail).toContain('“Upload File”');
    expect(toast?.retryLabel).toBeNull();
  });

  it('says nothing when the user cancels their own upload', async () => {
    store.bindConversation(CONVERSATION_ID);
    store.attach([textFile('notes.txt')]);
    await settle();

    store.remove(store.attachments()[0]!.id);
    await settle();

    expect(xhr.last.aborted).toBe(true);
    expect(store.attachments()).toEqual([]);
    expect(toasts.toasts()).toEqual([]);
  });

  it('replaces records rather than mutating them, so chips re-render', async () => {
    store.bindConversation(CONVERSATION_ID);
    store.attach([textFile('notes.txt')]);
    await settle();
    const before = store.attachments()[0];

    xhr.last.progress(25, 50);

    expect(store.attachments()[0]).not.toBe(before);
    expect(before?.state).toEqual({ kind: 'uploading', percent: 0 });
  });

  it('releases the send gate only once nothing is outstanding', async () => {
    store.bindConversation(CONVERSATION_ID);
    store.attach([textFile('a.txt'), textFile('b.txt')]);
    await settle();

    const settled = firstValueFrom(store.settled$().pipe(timeout(50)));

    xhr.requests[0]!.respond(400, PROBLEM_FIXTURES.uploadTooLarge);
    await settle();
    expect(store.blocksSend()).toBe(true);

    xhr.requests[1]!.respond(400, PROBLEM_FIXTURES.uploadTooLarge);
    await settle();

    await expect(settled).resolves.toBeUndefined();
    expect(store.blocksSend()).toBe(false);
  });

  it('completes the send gate immediately when there is nothing to wait for', async () => {
    store.bindConversation(CONVERSATION_ID);

    // The common case, and the one that must cost a turn with no attachments nothing.
    await expect(firstValueFrom(store.settled$().pipe(timeout(5)))).resolves.toBeUndefined();
  });

  it('re-posts a failed file under a new job on retry', async () => {
    store.bindConversation(CONVERSATION_ID);
    store.attach([textFile('notes.txt')]);
    await settle();
    xhr.last.respond(500, null);
    await settle();

    store.retry(store.attachments()[0]!.id);
    await settle();

    expect(xhr.requests).toHaveLength(2);
    expect(state()).toEqual({ kind: 'uploading', percent: 0 });
  });

  it('never lets the transfer bar retreat', async () => {
    store.bindConversation(CONVERSATION_ID);
    store.attach([textFile('notes.txt')]);
    await settle();

    xhr.last.progress(40, 50);
    xhr.last.progress(10, 50);

    expect(state()).toEqual({ kind: 'uploading', percent: 80 });
  });

  describe('watching a job (US-802)', () => {
    async function queue(): Promise<string> {
      store.bindConversation(CONVERSATION_ID);
      store.attach([textFile('notes.txt')]);
      await settle();
      xhr.last.respond(202, { id: JOB_ID });
      await settle();

      return store.attachments()[0]!.id;
    }

    /** Advances past the next scheduled delay and answers the poll it issues. */
    async function poll(body: JobStatusDto): Promise<void> {
      await vi.advanceTimersByTimeAsync(5_000);
      backend.expectOne(STATUS_URL).flush(body);
      await settle();
    }

    it('polls on the staged schedule rather than a fixed interval', async () => {
      await queue();

      // Nothing before the first delay elapses.
      await vi.advanceTimersByTimeAsync(499);
      backend.expectNone(STATUS_URL);

      await vi.advanceTimersByTimeAsync(1);
      backend.expectOne(STATUS_URL).flush(jobStatus({ state: 'Processing' }));
      await settle();

      // The second wait is twice the first, not the same again.
      await vi.advanceTimersByTimeAsync(999);
      backend.expectNone(STATUS_URL);
      await vi.advanceTimersByTimeAsync(1);
      backend.expectOne(STATUS_URL).flush(jobStatus({ state: 'Succeeded', documentId: DOC_ID }));
      await settle();

      expect(state()).toEqual({ kind: 'ready' });
    });

    it('renders the server’s own words rather than composing them twice', async () => {
      await queue();
      await poll(
        jobStatus({
          state: 'Processing',
          message: 'Embedded 12 of 24 chunk(s)',
          completedUnits: 12,
          totalUnits: 24,
        }),
      );

      expect(state()).toEqual({
        kind: 'processing',
        subStatus: 'Embedded 12 of 24 chunk(s)',
      });
    });

    it('does not latch counts a later report cleared', async () => {
      await queue();
      await poll(
        jobStatus({ state: 'Processing', message: null, completedUnits: 3, totalUnits: 12 }),
      );
      expect(state()).toEqual({ kind: 'processing', subStatus: '3 of 12' });

      // Any report that omits them nulls them server-side, so a client that held on
      // to the last pair would show a stale count through a stage counting nothing.
      await poll(
        jobStatus({ state: 'Processing', message: null, completedUnits: null, totalUnits: null }),
      );
      expect(state()).toEqual({ kind: 'processing', subStatus: 'Processing…' });
    });

    it('keeps the document id so the file can be fetched back without a lookup', async () => {
      const id = await queue();
      await poll(jobStatus({ state: 'Succeeded', documentId: DOC_ID }));

      expect(state()).toEqual({ kind: 'ready' });
      expect(store.documentIdOf(id)).toBe(DOC_ID);
      // The document is the conversation's now, and no API detaches it, so the
      // control can only hide the chip — it must not claim to remove anything.
      expect(store.attachments()[0]?.removeAction).toBe('dismiss');
      expect(store.attachments()[0]?.downloadable).toBe(true);
    });

    it('stops polling once the job is terminal', async () => {
      await queue();
      await poll(jobStatus({ state: 'Succeeded', documentId: DOC_ID }));

      await vi.advanceTimersByTimeAsync(30_000);
      backend.expectNone(STATUS_URL);
    });

    it('carries a failed job’s own explanation, with a retry', async () => {
      const id = await queue();
      await poll(
        jobStatus({
          state: 'Failed',
          errorMessage: 'No readable text was found in the document, so there is nothing to index.',
        }),
      );

      expect(state()).toEqual({
        kind: 'failed',
        reason: 'No readable text was found in the document, so there is nothing to index.',
      });

      store.retry(id);
      await settle();
      expect(xhr.requests).toHaveLength(2);
    });
  });

  describe('an expired job (US-803)', () => {
    it('reads as unknown, not failed, and stops polling', async () => {
      store.bindConversation(CONVERSATION_ID);
      store.attach([textFile('notes.txt')]);
      await settle();
      xhr.last.respond(202, { id: JOB_ID });
      await settle();

      await vi.advanceTimersByTimeAsync(5_000);
      backend
        .expectOne(STATUS_URL)
        .flush(PROBLEM_FIXTURES.resourceNotFound, { status: 404, statusText: 'Not Found' });
      await settle();

      // Nothing failed and there is nothing to retry — the status simply is no longer
      // kept. The same 404 covers another user's job, and must read identically.
      expect(state()).toEqual({ kind: 'unknown' });

      await vi.advanceTimersByTimeAsync(30_000);
      backend.expectNone(STATUS_URL);
    });
  });
});
