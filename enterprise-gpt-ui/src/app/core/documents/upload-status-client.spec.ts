import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Subscription } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { JobStatusDto } from '@domain/api/document';
import { AppError } from '@core/errors/app-error';
import { JobPoll, UploadStatusClient } from './upload-status-client';

const JOB_ID = 'b0f2c1a4-1f3e-4b7a-9a2e-5c6d7e8f9a0b';
const STATUS_URL = `${TEST_API_BASE_URL}/api/documents/upload-status/${JOB_ID}`;

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

describe('UploadStatusClient', () => {
  let client: UploadStatusClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    client = TestBed.inject(UploadStatusClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
    vi.useRealTimers();
  });

  /** Subscribes and records everything the follow emits and how it ended. */
  function follow(): {
    polls: JobPoll[];
    error: () => AppError | null;
    completed: () => boolean;
    subscription: Subscription;
  } {
    const polls: JobPoll[] = [];
    let error: AppError | null = null;
    let completed = false;

    const subscription = client.follow(JOB_ID).subscribe({
      next: (poll) => polls.push(poll),
      error: (thrown: AppError) => (error = thrown),
      complete: () => (completed = true),
    });

    return { polls, error: () => error, completed: () => completed, subscription };
  }

  it('waits the staged delay before each read, and never polls on subscribe', async () => {
    const run = follow();

    backend.expectNone(STATUS_URL);

    await vi.advanceTimersByTimeAsync(499);
    backend.expectNone(STATUS_URL);
    await vi.advanceTimersByTimeAsync(1);
    backend.expectOne(STATUS_URL).flush(jobStatus());

    // The second wait doubles rather than repeating.
    await vi.advanceTimersByTimeAsync(999);
    backend.expectNone(STATUS_URL);
    await vi.advanceTimersByTimeAsync(1);
    backend.expectOne(STATUS_URL).flush(jobStatus({ state: 'Succeeded' }));

    expect(run.polls).toHaveLength(2);
    expect(run.completed()).toBe(true);
  });

  it('completes on the terminal poll and reads nothing after it', async () => {
    const run = follow();

    await vi.advanceTimersByTimeAsync(500);
    backend.expectOne(STATUS_URL).flush(jobStatus({ state: 'Failed', errorMessage: 'Corrupt.' }));

    expect(run.completed()).toBe(true);

    await vi.advanceTimersByTimeAsync(60_000);
    backend.expectNone(STATUS_URL);
  });

  it('reads a 404 as expired rather than as a failure', async () => {
    const run = follow();

    await vi.advanceTimersByTimeAsync(500);
    backend
      .expectOne(STATUS_URL)
      .flush(PROBLEM_FIXTURES.resourceNotFound, { status: 404, statusText: 'Not Found' });

    expect(run.polls).toEqual([{ kind: 'expired' }]);
    expect(run.error()).toBeNull();
    expect(run.completed()).toBe(true);
  });

  it('surfaces any other failure as an error', async () => {
    const run = follow();

    await vi.advanceTimersByTimeAsync(500);
    backend.expectOne(STATUS_URL).flush(null, { status: 500, statusText: 'Server Error' });

    expect(run.error()?.status).toBe(500);
    expect(run.completed()).toBe(false);
  });

  it('issues nothing further once the caller unsubscribes mid-schedule', async () => {
    const run = follow();

    await vi.advanceTimersByTimeAsync(500);
    backend.expectOne(STATUS_URL).flush(jobStatus());

    run.subscription.unsubscribe();

    // The store's `cancel(id)` depends on this: a chip removed mid-ingest must stop
    // costing requests, and the pending timer has to go with it.
    await vi.advanceTimersByTimeAsync(60_000);
    backend.expectNone(STATUS_URL);
  });
});
