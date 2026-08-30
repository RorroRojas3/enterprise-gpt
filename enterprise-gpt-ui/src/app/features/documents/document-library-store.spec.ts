import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { UserDocumentDto } from '@domain/api/document';
import { conversationEvents } from '@core/events/conversation-events';
import { sessionEvents } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture } from '@testing/conversations';
import { documentPage, userDocumentFixture } from '@testing/documents';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { DOCUMENT_PAGE_SIZE, DocumentLibraryStore } from './document-library-store';

const DOCUMENTS_URL = `${TEST_API_BASE_URL}/api/documents`;

describe('DocumentLibraryStore (US-1002/US-1003)', () => {
  let backend: HttpTestingController;
  let store: InstanceType<typeof DocumentLibraryStore>;
  let term: ReturnType<typeof signal<string>>;
  let generatedOnly: ReturnType<typeof signal<boolean>>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        DocumentLibraryStore,
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(DocumentLibraryStore);
    term = signal('');
    generatedOnly = signal(false);
  });

  afterEach(() => {
    backend.verify();
  });

  /** Wires the query the way the screen's constructor does — one computation, both filters. */
  function bind(): void {
    TestBed.runInInjectionContext(() =>
      store.bindQuery(() => ({ name: term(), generatedOnly: generatedOnly() })),
    );
    TestBed.tick();
  }

  function expectRequest(): TestRequest {
    return backend.expectOne((request) => request.url === DOCUMENTS_URL);
  }

  function expectGeneratedRequest(): TestRequest {
    return backend.expectOne(
      (request) => request.url === DOCUMENTS_URL && request.params.get('type') === 'generated',
    );
  }

  /** A full page, so more is left behind and `hasMore()` is true. */
  function fullPage(count = DOCUMENT_PAGE_SIZE): UserDocumentDto[] {
    return Array.from({ length: count }, () => userDocumentFixture());
  }

  /** A first page that leaves more behind, answered the way the server would. */
  function loadFirstPage(totalCount = 60): void {
    bind();
    expectRequest().flush(documentPage(fullPage(), { totalCount, pageSize: DOCUMENT_PAGE_SIZE }));
    TestBed.tick();
  }

  it('starts empty and idle, with zero seeded records', () => {
    // Asserted explicitly: the library is built from the server's answer alone, so a
    // brand-new user sees the empty state and never a placeholder row.
    expect(store.entities()).toEqual([]);
    expect(store.isIdle()).toBe(true);
    expect(store.totalCount()).toBe(0);
    backend.expectNone((request) => request.url === DOCUMENTS_URL);
  });

  describe('paging', () => {
    it('fetches one page and leaves the rest behind the control', () => {
      loadFirstPage(600);

      const request = backend.match(() => true);
      expect(request).toHaveLength(0);
      expect(store.entities().length).toBe(DOCUMENT_PAGE_SIZE);
      expect(store.hasMore()).toBe(true);
      expect(store.isFulfilled()).toBe(true);
    });

    it('asks for the page size the screen chose', () => {
      bind();

      const request = expectRequest();
      expect(request.request.params.get('take')).toBe(String(DOCUMENT_PAGE_SIZE));
      expect(request.request.params.get('skip')).toBe('0');
      // Omitted, never sent empty: a blank `name=` would still take the server down its
      // LIKE branch.
      expect(request.request.params.has('name')).toBe(false);

      request.flush(documentPage([userDocumentFixture()], { pageSize: DOCUMENT_PAGE_SIZE }));
      TestBed.tick();
    });

    it('appends the next page and moves the offset by what came back', () => {
      loadFirstPage();

      store.loadMore();
      TestBed.tick();

      const next = expectRequest();
      expect(next.request.params.get('skip')).toBe(String(DOCUMENT_PAGE_SIZE));
      next.flush(
        documentPage([userDocumentFixture()], {
          totalCount: 60,
          pageSize: DOCUMENT_PAGE_SIZE,
          skip: DOCUMENT_PAGE_SIZE,
        }),
      );
      TestBed.tick();

      expect(store.entities().length).toBe(DOCUMENT_PAGE_SIZE + 1);
      expect(store.skip()).toBe(DOCUMENT_PAGE_SIZE + 1);
      expect(store.countSummary()).toBe('Showing 26 of 60 documents');
    });

    it('ignores a second press while a page is on the wire', () => {
      loadFirstPage();

      store.loadMore();
      TestBed.tick();
      const inFlight = expectRequest();
      expect(store.loadingMore()).toBe(true);

      store.loadMore();
      TestBed.tick();
      // `skip` has not moved yet, so a second request would fetch the same window twice
      // and `addEntities` would silently no-op on every id.
      backend.expectNone(() => true);

      inFlight.flush(documentPage([], { totalCount: 60, pageSize: DOCUMENT_PAGE_SIZE, skip: 25 }));
      TestBed.tick();
      expect(store.loadingMore()).toBe(false);
    });

    it('refuses a press once everything is loaded', () => {
      loadFirstPage(DOCUMENT_PAGE_SIZE);

      expect(store.hasMore()).toBe(false);

      store.loadMore();
      TestBed.tick();

      backend.verify();
    });

    it('keeps the loaded rows when a page fails, and toasts instead', async () => {
      loadFirstPage();
      const toasts = TestBed.inject(ToastStore);

      store.loadMore();
      TestBed.tick();
      expectRequest().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
        status: 500,
        statusText: 'Internal Server Error',
      });
      TestBed.tick();
      await Promise.resolve();

      // The rows already on screen belong to a query that succeeded, so swapping the list
      // for an error panel would throw away a usable result set.
      expect(store.entities().length).toBe(DOCUMENT_PAGE_SIZE);
      expect(store.error()).toBeNull();
      expect(store.loadingMore()).toBe(false);
      expect(toasts.toasts()).toHaveLength(1);
    });

    it('drops a page whose result set a newer query replaced', () => {
      loadFirstPage(600);

      store.loadMore();
      TestBed.tick();
      const page = expectRequest();

      term.set('quarterly');
      TestBed.tick();

      // Without `_querySuperseded$` this page would land after the new query's
      // `setAllEntities` and append a window from a list that no longer exists.
      expect(page.cancelled).toBe(true);
      expect(store.loadingMore()).toBe(false);

      const narrowed = expectRequest();
      expect(narrowed.request.params.get('name')).toBe('quarterly');
      expect(narrowed.request.params.get('skip')).toBe('0');
      narrowed.flush(documentPage([userDocumentFixture()], { pageSize: DOCUMENT_PAGE_SIZE }));
      TestBed.tick();

      expect(store.entities().length).toBe(1);
    });

    it('drops a page when a conversation delete moves the offset under it', () => {
      loadFirstPage(600);
      const doomed = store.entities()[0];

      store.loadMore();
      TestBed.tick();
      const page = expectRequest();

      TestBed.inject(Dispatcher).dispatch(conversationEvents.deleted(doomed?.conversationId ?? ''));
      TestBed.tick();

      // The page's URL was built against the pre-removal offset, so letting it land would
      // skip the rows that slid into the gap.
      expect(page.cancelled).toBe(true);
      expect(store.loadingMore()).toBe(false);
    });

    it('replaces the listing with an error on a failed first page and keeps Retry meaningful', () => {
      bind();
      expectRequest().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
        status: 500,
        statusText: 'Server Error',
      });
      TestBed.tick();

      expect(store.error()).not.toBeNull();
      expect(store.entities().length).toBe(0);

      store.retry();
      TestBed.tick();

      const retried = expectRequest();
      expect(retried.request.params.get('skip')).toBe('0');
      retried.flush(documentPage([userDocumentFixture()], { pageSize: DOCUMENT_PAGE_SIZE }));
      TestBed.tick();

      expect(store.error()).toBeNull();
      expect(store.isFulfilled()).toBe(true);
      expect(store.entities().length).toBe(1);
    });
  });

  describe('the served name filter', () => {
    it('sends the term as `name=` when the URL carries one', () => {
      term.set('quarterly');
      bind();

      const request = expectRequest();
      expect(request.request.params.get('name')).toBe('quarterly');

      request.flush(documentPage([userDocumentFixture()], { pageSize: DOCUMENT_PAGE_SIZE }));
      TestBed.tick();
    });

    it('carries the term onto an appended page, so a search reaches every document', () => {
      term.set('quarterly');
      loadFirstPage(600);

      store.loadMore();
      TestBed.tick();

      // The reachability guarantee: without this the second page searches nothing and a
      // match ranked past the first page never comes back.
      const next = expectRequest();
      expect(next.request.params.get('name')).toBe('quarterly');
      expect(next.request.params.get('skip')).toBe(String(DOCUMENT_PAGE_SIZE));

      next.flush(documentPage([], { totalCount: 600, pageSize: DOCUMENT_PAGE_SIZE, skip: 25 }));
      TestBed.tick();
    });

    it('replaces the rows when the term changes rather than narrowing what is held', () => {
      loadFirstPage(600);

      term.set('quarterly');
      TestBed.tick();

      const narrowed = expectRequest();
      expect(narrowed.request.params.get('name')).toBe('quarterly');
      narrowed.flush(
        documentPage([userDocumentFixture({ name: 'quarterly.pdf' })], {
          pageSize: DOCUMENT_PAGE_SIZE,
        }),
      );
      TestBed.tick();

      expect(store.entities().length).toBe(1);
      expect(store.hasQuery()).toBe(true);
    });

    it('tells "nothing uploaded" apart from "no matches"', () => {
      bind();
      expectRequest().flush(documentPage([], { pageSize: DOCUMENT_PAGE_SIZE }));
      TestBed.tick();

      // Zero documents and no term: "Nothing uploaded yet" is the truth.
      expect(store.isEmpty()).toBe(true);
      expect(store.hasNoMatches()).toBe(false);
      expect(store.countSummary()).toBe('No documents');

      term.set('quarterly');
      TestBed.tick();
      expectRequest().flush(documentPage([], { pageSize: DOCUMENT_PAGE_SIZE }));
      TestBed.tick();

      // A term with nothing behind it is the other state, and the one with a way out.
      expect(store.hasNoMatches()).toBe(true);
    });
  });

  describe('grouping', () => {
    it('groups loaded rows in first-seen order', () => {
      const alphaNewest = userDocumentFixture({
        name: 'alpha-plan.pdf',
        conversationName: 'Alpha',
        conversationDocumentCount: 2,
      });
      const beta = userDocumentFixture({ name: 'beta-notes.txt', conversationName: 'Beta' });
      const alphaOlder = userDocumentFixture({
        name: 'alpha-summary.md',
        conversationId: alphaNewest.conversationId,
        conversationName: 'Alpha',
        conversationDocumentCount: 2,
      });
      bind();
      expectRequest().flush(
        documentPage([alphaNewest, beta, alphaOlder], { pageSize: DOCUMENT_PAGE_SIZE }),
      );
      TestBed.tick();

      // First-seen over a newest-first listing: groups order by their newest document.
      const groups = store.groups();
      expect(groups.map((group) => group.conversationId)).toEqual([
        alphaNewest.conversationId,
        beta.conversationId,
      ]);
      expect(groups[0]?.documents.map((document) => document.id)).toEqual([
        alphaNewest.id,
        alphaOlder.id,
      ]);
      expect(groups[0]?.countLabel).toBe('2 files');
      expect(groups[1]?.countLabel).toBe('1 file');
    });

    it('says how many of a conversation’s files a partially loaded group holds', () => {
      const first = userDocumentFixture({
        conversationName: 'Alpha',
        conversationDocumentCount: 8,
      });
      const second = userDocumentFixture({
        conversationId: first.conversationId,
        conversationName: 'Alpha',
        conversationDocumentCount: 8,
      });
      const third = userDocumentFixture({
        conversationId: first.conversationId,
        conversationName: 'Alpha',
        conversationDocumentCount: 8,
      });
      bind();
      expectRequest().flush(
        documentPage([first, second, third], { totalCount: 60, pageSize: DOCUMENT_PAGE_SIZE }),
      );
      TestBed.tick();

      expect(store.groups()[0]?.countLabel).toBe('3 of 8 files');
    });

    it('drops the qualifier once a group holds everything the server counted', () => {
      const document = userDocumentFixture({ conversationDocumentCount: 1 });
      bind();
      expectRequest().flush(documentPage([document], { pageSize: DOCUMENT_PAGE_SIZE }));
      TestBed.tick();

      // Never "1 of 1 files": the group is whole, and the qualifier would be noise.
      expect(store.groups()[0]?.countLabel).toBe('1 file');
    });

    it('updates a group’s count as Load more appends rows to it', () => {
      const first = userDocumentFixture({
        conversationName: 'Alpha',
        conversationDocumentCount: 8,
      });
      bind();
      expectRequest().flush(
        documentPage([first], { totalCount: 60, pageSize: DOCUMENT_PAGE_SIZE }),
      );
      TestBed.tick();
      expect(store.groups()[0]?.countLabel).toBe('1 of 8 files');

      store.loadMore();
      TestBed.tick();
      expectRequest().flush(
        documentPage(
          [
            userDocumentFixture({
              conversationId: first.conversationId,
              conversationName: 'Alpha',
              conversationDocumentCount: 8,
            }),
          ],
          { totalCount: 60, pageSize: DOCUMENT_PAGE_SIZE, skip: 1 },
        ),
      );
      TestBed.tick();

      expect(store.groups()[0]?.countLabel).toBe('2 of 8 files');
    });
  });

  describe('the served origin filter', () => {
    it('asks for every origin until the filter is on', () => {
      bind();

      expect(expectRequest().request.params.has('type')).toBe(false);
    });

    it('narrows the request when the assistant-created filter is on', () => {
      generatedOnly.set(true);
      bind();

      expectGeneratedRequest().flush(
        documentPage([userDocumentFixture({ type: 'Generated' })], {
          pageSize: DOCUMENT_PAGE_SIZE,
        }),
      );
      TestBed.tick();

      expect(store.generatedOnly()).toBe(true);
      expect(store.entities()).toHaveLength(1);
    });

    it('carries the origin onto an appended page', () => {
      generatedOnly.set(true);
      bind();
      expectGeneratedRequest().flush(
        documentPage(fullPage(), { totalCount: 60, pageSize: DOCUMENT_PAGE_SIZE }),
      );
      TestBed.tick();

      store.loadMore();
      TestBed.tick();

      expectGeneratedRequest().flush(
        documentPage([], { totalCount: 60, pageSize: DOCUMENT_PAGE_SIZE, skip: 25 }),
      );
      TestBed.tick();
    });

    it('drops the previous rows the moment the origin changes', () => {
      bind();
      expectRequest().flush(
        documentPage([userDocumentFixture()], { pageSize: DOCUMENT_PAGE_SIZE }),
      );
      TestBed.tick();
      expect(store.totalCount()).toBe(1);

      generatedOnly.set(true);
      TestBed.tick();

      // Held rows under the new filter would be counted and captioned as that filter's set
      // for a whole round trip; the skeleton is the honest thing to show instead.
      expect(store.isFirstLoad()).toBe(false);
      expectGeneratedRequest().flush(documentPage([], { pageSize: DOCUMENT_PAGE_SIZE }));
      TestBed.tick();

      expect(store.entities()).toEqual([]);
      expect(store.totalCount()).toBe(0);
    });

    it('names the narrowed set in the count', () => {
      generatedOnly.set(true);
      bind();
      expectGeneratedRequest().flush(
        documentPage(fullPage(), { totalCount: 600, pageSize: DOCUMENT_PAGE_SIZE }),
      );
      TestBed.tick();

      // It would otherwise describe the whole library, which is the dishonesty serving the
      // filter exists to avoid.
      expect(store.countSummary()).toBe('Showing 25 of 600 generated documents');
    });

    it('retries the query the filter last ran with', () => {
      generatedOnly.set(true);
      bind();
      expectGeneratedRequest().error(new ProgressEvent('error'));
      TestBed.tick();

      store.retry();
      TestBed.tick();

      expectGeneratedRequest().flush(documentPage([], { pageSize: DOCUMENT_PAGE_SIZE }));
      TestBed.tick();
      expect(store.isFulfilled()).toBe(true);
    });
  });

  describe('events', () => {
    it('renames the link column when a conversation is renamed, and evicts nothing', () => {
      const kept = userDocumentFixture();
      const renamedA = userDocumentFixture();
      const renamedB = userDocumentFixture({ conversationId: renamedA.conversationId });
      bind();
      expectRequest().flush(
        documentPage([kept, renamedA, renamedB], { pageSize: DOCUMENT_PAGE_SIZE }),
      );
      TestBed.tick();

      TestBed.inject(Dispatcher).dispatch(
        conversationEvents.updated(
          conversationFixture({ id: renamedA.conversationId, name: 'Renamed' }),
        ),
      );
      TestBed.tick();

      expect(store.entityMap()[renamedA.id]?.conversationName).toBe('Renamed');
      expect(store.entityMap()[renamedB.id]?.conversationName).toBe('Renamed');
      expect(store.entityMap()[kept.id]?.conversationName).toBe(kept.conversationName);
      // The filter is on document names only, so a rename never removes a row — or moves
      // a counter.
      expect(store.entities().length).toBe(3);
      expect(store.totalCount()).toBe(3);
    });

    it('drops a deleted conversation’s rows and takes both counters down with them', () => {
      const doomedA = userDocumentFixture();
      const doomedB = userDocumentFixture({ conversationId: doomedA.conversationId });
      const kept = userDocumentFixture();
      bind();
      expectRequest().flush(
        documentPage([doomedA, doomedB, kept], { pageSize: DOCUMENT_PAGE_SIZE }),
      );
      TestBed.tick();
      expect(store.skip()).toBe(3);

      TestBed.inject(Dispatcher).dispatch(conversationEvents.deleted(doomedA.conversationId));
      TestBed.tick();

      expect(store.entities().map((document) => document.id)).toEqual([kept.id]);
      // Both together: the total because the count would otherwise claim rows that are
      // gone, and the offset because `hasMore` compares the two.
      expect(store.totalCount()).toBe(1);
      expect(store.skip()).toBe(1);
    });
  });

  it('cancels an appended page on sign-out and empties', () => {
    loadFirstPage(600);

    store.loadMore();
    TestBed.tick();
    const inFlight = expectRequest();

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    TestBed.tick();

    // Clearing state is not cancelling work: `withResetOnSignOut` empties the store and the
    // response would write the previous user's rows straight back over it.
    expect(inFlight.cancelled).toBe(true);
    expect(store.entities()).toEqual([]);
    expect(store.loadingMore()).toBe(false);
  });
});
