import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { sessionEvents } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { UploadCancelClient } from './upload-cancel-client';

const JOB_ID = 'b0f2c1a4-1f3e-4b7a-9a2e-5c6d7e8f9a0b';
const CANCEL_URL = `${TEST_API_BASE_URL}/api/documents/upload-status/${JOB_ID}`;

describe('UploadCancelClient', () => {
  let client: UploadCancelClient;
  let backend: HttpTestingController;
  let toasts: InstanceType<typeof ToastStore>;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    backend = TestBed.inject(HttpTestingController);
    client = TestBed.inject(UploadCancelClient);
    toasts = TestBed.inject(ToastStore);
  });

  afterEach(() => backend.verify());

  it('asks the server to call the job off', () => {
    client.cancel(JOB_ID);

    const request = backend.expectOne(CANCEL_URL);
    expect(request.request.method).toBe('DELETE');
    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('escapes the job id into the path', () => {
    client.cancel('a/b');

    backend
      .expectOne(`${TEST_API_BASE_URL}/api/documents/upload-status/a%2Fb`)
      .flush(null, { status: 204, statusText: 'No Content' });
  });

  it('says nothing when the job is already gone', () => {
    client.cancel(JOB_ID);

    backend
      .expectOne(CANCEL_URL)
      .flush({ title: 'Not found' }, { status: 404, statusText: 'Not Found' });

    // The chip is off screen either way; a toast here would report a cleanup nobody is
    // waiting on and offer no control to act on it.
    expect(toasts.toasts()).toHaveLength(0);
  });

  it('says nothing when the cancel itself fails', () => {
    client.cancel(JOB_ID);

    backend
      .expectOne(CANCEL_URL)
      .flush({ title: 'Server error' }, { status: 500, statusText: 'Server Error' });

    expect(toasts.toasts()).toHaveLength(0);
  });

  it('abandons the request when the session ends', () => {
    client.cancel(JOB_ID);
    backend.expectOne(CANCEL_URL);

    // Cancels the in-flight request, which is what `backend.verify()` then requires.
    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
  });
});
