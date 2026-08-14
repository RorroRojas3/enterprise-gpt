import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { DocumentDownloadDto, UploadTarget } from '@domain/api/document';
import { ToastStore } from '@core/notifications/toast-store';
import { DocumentDownloadStore } from './document-download-store';

const CONVERSATION: UploadTarget = {
  kind: 'conversation',
  id: '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b',
};
const DOC_ID = '9c8b7a65-4321-4fed-8cba-0987654321fe';
const DOWNLOAD_URL = `${TEST_API_BASE_URL}/api/documents/conversations/${CONVERSATION.id}/${DOC_ID}`;

const SIGNED: DocumentDownloadDto = {
  downloadUrl: 'https://acct.blob.core.windows.net/documents/8f3/q3-report.pdf?sig=secret',
  fileName: 'q3-report.pdf',
  expiresAt: '2026-08-14T14:35:00+00:00',
};

describe('DocumentDownloadStore', () => {
  let store: InstanceType<typeof DocumentDownloadStore>;
  let toasts: InstanceType<typeof ToastStore>;
  let backend: HttpTestingController;
  let clicks: HTMLAnchorElement[];

  beforeEach(() => {
    clicks = [];
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(DocumentDownloadStore);
    toasts = TestBed.inject(ToastStore);

    // jsdom navigates on an anchor click and logs "Not implemented"; recording the
    // element is also the only way to assert what the browser was handed.
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      clicks.push(this);
    });
  });

  afterEach(() => {
    backend.verify();
    vi.restoreAllMocks();
  });

  it('issues nothing until a click', () => {
    // A list rendering forty documents must not put forty live credentials in flight.
    backend.expectNone(DOWNLOAD_URL);
  });

  it('fetches the signed link on click and hands it straight to the browser', () => {
    store.download(CONVERSATION, DOC_ID, 'q3-report.pdf');

    const request = backend.expectOne(DOWNLOAD_URL);
    expect(request.request.method).toBe('GET');
    expect(store.isRowPending(DOC_ID)).toBe(true);

    request.flush(SIGNED);

    expect(clicks).toHaveLength(1);
    expect(clicks[0]?.href).toBe(SIGNED.downloadUrl);
    expect(clicks[0]?.download).toBe('q3-report.pdf');
    expect(store.isRowPending(DOC_ID)).toBe(false);
  });

  it('leaves the credential nowhere it could outlive its five minutes', () => {
    store.download(CONVERSATION, DOC_ID, 'q3-report.pdf');
    backend.expectOne(DOWNLOAD_URL).flush(SIGNED);

    const secret = SIGNED.downloadUrl;
    expect(JSON.stringify(localStorage)).not.toContain(secret);
    expect(JSON.stringify(sessionStorage)).not.toContain(secret);
    // Not in state either: the anchor was detached again, so nothing on the page
    // still holds it.
    expect(JSON.stringify(store.pendingIds())).not.toContain(secret);
    expect(document.body.innerHTML).not.toContain(secret);
    expect(window.location.href).not.toContain(secret);
  });

  it('reports a missing document as unavailable, never as deleted', () => {
    store.download(CONVERSATION, DOC_ID, 'q3-report.pdf');
    backend
      .expectOne(DOWNLOAD_URL)
      .flush(PROBLEM_FIXTURES.resourceNotFound, { status: 404, statusText: 'Not Found' });

    const toast = toasts.toasts()[0];
    expect(toast?.title).toBe('q3-report.pdf isn’t available');
    expect(toast?.detail).toBe('This document can no longer be retrieved.');
    expect(JSON.stringify(toast)).not.toMatch(/deleted/i);
    expect(store.isRowPending(DOC_ID)).toBe(false);
  });

  it('explains a deployment with no storage rather than failing generically', () => {
    store.download(CONVERSATION, DOC_ID, 'q3-report.pdf');
    backend.expectOne(DOWNLOAD_URL).flush(PROBLEM_FIXTURES.storageNotConfigured, {
      status: 503,
      statusText: 'Service Unavailable',
    });

    expect(toasts.toasts()[0]?.title).toBe('Downloads aren’t available in this deployment');
    // An app-typed 503 is a deterministic deployment state, so `retryInterceptor`
    // leaves it alone and exactly one request was made.
    backend.expectNone(DOWNLOAD_URL);
  });

  it('runs two downloads side by side rather than cancelling the first', () => {
    const second = '00000000-1111-4222-8333-444444444444';

    store.download(CONVERSATION, DOC_ID, 'a.pdf');
    store.download(CONVERSATION, second, 'b.pdf');

    expect(store.isRowPending(DOC_ID)).toBe(true);
    expect(store.isRowPending(second)).toBe(true);

    backend.expectOne(DOWNLOAD_URL).flush(SIGNED);
    backend
      .expectOne(`${TEST_API_BASE_URL}/api/documents/conversations/${CONVERSATION.id}/${second}`)
      .flush({ ...SIGNED, fileName: 'b.pdf' });

    expect(clicks.map((anchor) => anchor.download)).toEqual(['q3-report.pdf', 'b.pdf']);
  });
});
