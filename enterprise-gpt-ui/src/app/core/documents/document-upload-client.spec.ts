import { TestBed } from '@angular/core/testing';
import { Subscription } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { textFile } from '@testing/drag';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { FakeUploadXhrQueue, provideFakeUploadXhr } from '@testing/upload-xhr';
import { UploadTarget } from '@domain/api/document';
import { TokenService } from '@core/auth/token-service';
import { AppError } from '@core/errors/app-error';
import { DocumentUploadClient, UploadEvent } from './document-upload-client';

const CONVERSATION: UploadTarget = {
  kind: 'conversation',
  id: '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b',
};
const UPLOAD_URL = `${TEST_API_BASE_URL}/api/documents/conversations/${CONVERSATION.id}`;

describe('DocumentUploadClient', () => {
  let xhr: FakeUploadXhrQueue;
  let getToken: ReturnType<typeof vi.fn>;
  let client: DocumentUploadClient;

  beforeEach(() => {
    xhr = new FakeUploadXhrQueue();
    getToken = vi.fn().mockResolvedValue('token-1');

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        { provide: TokenService, useValue: { getToken } },
        provideFakeUploadXhr(xhr),
      ],
    });

    client = TestBed.inject(DocumentUploadClient);
  });

  /** Collects everything one upload emits, plus how it ended. */
  function observe(file = textFile('notes.txt')): {
    events: UploadEvent[];
    error: () => AppError | null;
    completed: () => boolean;
    subscription: Subscription;
  } {
    const events: UploadEvent[] = [];
    let error: AppError | null = null;
    let completed = false;

    const subscription = client.upload(CONVERSATION, file).subscribe({
      next: (event) => events.push(event),
      error: (thrown: AppError) => (error = thrown),
      complete: () => (completed = true),
    });

    return { events, error: () => error, completed: () => completed, subscription };
  }

  /** Lets the token promise resolve so the request is actually issued. */
  const settle = () => Promise.resolve().then(() => Promise.resolve());

  it('posts the file under the field name the minimal API binds', async () => {
    observe(textFile('q3-report.pdf'));
    await settle();

    const request = xhr.last;
    expect(request.method).toBe('POST');
    expect(request.url).toBe(UPLOAD_URL);
    expect(request.headers.get('Authorization')).toBe('Bearer token-1');
    // Never set by hand: the browser has to append the multipart boundary itself.
    expect(request.headers.has('Content-Type')).toBe(false);
    expect(request.file()?.name).toBe('q3-report.pdf');
  });

  it('reports byte progress, then the queued job', async () => {
    const upload = observe();
    await settle();

    xhr.last.progress(21, 50);
    xhr.last.progress(50, 50);
    xhr.last.respond(202, { id: 'job-1' });
    await settle();

    expect(upload.events).toEqual([
      { kind: 'progress', percent: 42 },
      { kind: 'progress', percent: 100 },
      { kind: 'accepted', jobId: 'job-1' },
    ]);
    expect(upload.completed()).toBe(true);
  });

  it('surfaces a rejected upload as its typed problem, extensions and all', async () => {
    const upload = observe();
    await settle();

    xhr.last.respond(400, PROBLEM_FIXTURES.uploadTooLarge);
    await settle();

    const error = upload.error();
    expect(error?.kind).toBe('upload-too-large');
    expect(error?.kind === 'upload-too-large' && error.maxBytes).toBe(52_428_800);
    expect(getToken).toHaveBeenCalledTimes(1);
  });

  it('replays a 401 once with a forced refresh, and only a 401', async () => {
    const upload = observe();
    await settle();

    xhr.last.respond(401, null);
    await settle();

    expect(getToken).toHaveBeenNthCalledWith(2, { forceRefresh: true });
    expect(xhr.requests).toHaveLength(2);
    expect(xhr.last.headers.get('Authorization')).toBe('Bearer token-1');

    xhr.last.respond(202, { id: 'job-2' });
    await settle();
    expect(upload.events).toContainEqual({ kind: 'accepted', jobId: 'job-2' });
  });

  it('does not refresh for a missing grant, which no token can supply', async () => {
    const upload = observe();
    await settle();

    xhr.last.respond(403, PROBLEM_FIXTURES.permissionRequired);
    await settle();

    expect(xhr.requests).toHaveLength(1);
    expect(getToken).toHaveBeenCalledTimes(1);
    const error = upload.error();
    expect(error?.kind).toBe('permission-required');
    expect(error?.kind === 'permission-required' && error.permissions).toEqual(['Upload File']);
  });

  it('classifies a dropped connection as the network arm, like the stream does', async () => {
    const upload = observe();
    await settle();

    xhr.last.fail();
    await settle();

    expect(upload.error()?.kind).toBe('network');
  });

  it('aborts the request when the caller unsubscribes', async () => {
    const upload = observe();
    await settle();

    upload.subscription.unsubscribe();

    expect(xhr.last.aborted).toBe(true);
  });

  it('refuses a 202 with no job id rather than queueing something unobservable', async () => {
    const upload = observe();
    await settle();

    xhr.last.respond(202, {});
    await settle();

    expect(upload.error()?.kind).toBe('client');
  });
});
