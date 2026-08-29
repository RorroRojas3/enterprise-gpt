import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { UserDocumentDto } from '@domain/api/document';
import { conversationEvents } from '@core/events/conversation-events';
import { sessionEvents } from '@core/events/session-events';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture } from '@testing/conversations';
import { documentPage, userDocumentFixture } from '@testing/documents';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import {
  DOCUMENT_LOAD_CEILING,
  DOCUMENT_PAGE_SIZE,
  DocumentLibraryStore,
} from './document-library-store';

const DOCUMENTS_URL = `${TEST_API_BASE_URL}/api/documents`;

describe('DocumentLibraryStore (US-1002/US-1003)', () => {
  let backend: HttpTestingController;
  let store: InstanceType<typeof DocumentLibraryStore>;

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
  });

  afterEach(() => {
    backend.verify();
  });

  function expectRequest(): TestRequest {
    return backend.expectOne((request) => request.url === DOCUMENTS_URL);
  }

  function expectGeneratedRequest(): TestRequest {
    return backend.expectOne(
      (request) => request.url === DOCUMENTS_URL && request.params.get('type') === 'generated',
    );
  }

  /** A full page, so the drain asks for another. */
  function fullPage(count = DOCUMENT_PAGE_SIZE): UserDocumentDto[] {
    return Array.from({ length: count }, () => userDocumentFixture());
  }

  it('starts empty and idle, with zero seeded records', () => {
    // A US-1002 criterion, asserted explicitly: the library is built from the
    // server's answer alone, so a brand-new user sees the empty state and never a
    // placeholder row.
    expect(store.entities()).toEqual([]);
    expect(store.isIdle()).toBe(true);
    expect(store.totalCount()).toBe(0);
    expect(store.truncated()).toBe(false);
    backend.expectNone((request) => request.url === DOCUMENTS_URL);
  });

  it('drains every page of a listing larger than one page', () => {
    store.bindSource(false);

    const first = expectRequest();
    expect(first.request.params.get('take')).toBe(String(DOCUMENT_PAGE_SIZE));
    expect(first.request.params.get('skip')).toBe('0');
    first.flush(documentPage(fullPage(), { totalCount: 250 }));
    TestBed.tick();

    const second = expectRequest();
    expect(second.request.params.get('skip')).toBe(String(DOCUMENT_PAGE_SIZE));
    second.flush(documentPage(fullPage(), { totalCount: 250, skip: DOCUMENT_PAGE_SIZE }));
    TestBed.tick();

    const third = expectRequest();
    expect(third.request.params.get('skip')).toBe(String(DOCUMENT_PAGE_SIZE * 2));
    third.flush(documentPage(fullPage(50), { totalCount: 250, skip: DOCUMENT_PAGE_SIZE * 2 }));
    TestBed.tick();

    expect(store.entities().length).toBe(250);
    expect(store.truncated()).toBe(false);
    expect(store.isFulfilled()).toBe(true);
  });

  it('stops at the ceiling and says so, rather than draining a whole tenant', () => {
    store.bindSource(false);

    for (let page = 0; page * DOCUMENT_PAGE_SIZE < DOCUMENT_LOAD_CEILING; page++) {
      expectRequest().flush(
        documentPage(fullPage(), { totalCount: 600, skip: page * DOCUMENT_PAGE_SIZE }),
      );
      TestBed.tick();
    }

    // No sixth request: the ceiling is the whole point.
    backend.verify();
    expect(store.entities().length).toBe(DOCUMENT_LOAD_CEILING);
    expect(store.truncated()).toBe(true);
    expect(store.ceilingNotice()).toBe('Showing the 500 most recent of 600 documents.');
  });

  it('supersedes an in-flight drain on retry rather than letting its pages land', () => {
    store.bindSource(false);
    expectRequest().flush(documentPage(fullPage(), { totalCount: 250 }));
    TestBed.tick();

    const secondPage = expectRequest();

    store.retry();
    TestBed.tick();

    expect(secondPage.cancelled).toBe(true);

    const restarted = expectRequest();
    expect(restarted.request.params.get('skip')).toBe('0');
    restarted.flush(documentPage([userDocumentFixture()]));
    TestBed.tick();

    expect(store.entities().length).toBe(1);
    expect(store.truncated()).toBe(false);
  });

  it('replaces the listing with an error mid-drain and keeps Retry meaningful', () => {
    store.bindSource(false);
    expectRequest().flush(documentPage(fullPage(), { totalCount: 250 }));
    TestBed.tick();

    expectRequest().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 500,
      statusText: 'Server Error',
    });
    TestBed.tick();

    // The listing regime: whichever page failed, the screen renders the panel
    // instead of the rows — a half-drained set counted as if it were whole is the
    // failure regime A exists to avoid. The dead pages are cleared with it, so
    // Retry's pending round trip shows the skeleton rather than the partial set.
    expect(store.error()).not.toBeNull();
    expect(store.entities().length).toBe(0);

    store.retry();
    TestBed.tick();

    expect(store.isFirstLoad()).toBe(true);
    TestBed.tick();

    const retried = expectRequest();
    expect(retried.request.params.get('skip')).toBe('0');
    retried.flush(documentPage([userDocumentFixture()]));
    TestBed.tick();

    expect(store.error()).toBeNull();
    expect(store.isFulfilled()).toBe(true);
    expect(store.entities().length).toBe(1);
  });

  describe('events', () => {
    it('renames the link column when a conversation is renamed, and evicts nothing', () => {
      store.bindSource(false);
      const kept = userDocumentFixture();
      const renamedA = userDocumentFixture();
      const renamedB = userDocumentFixture({ conversationId: renamedA.conversationId });
      expectRequest().flush(documentPage([kept, renamedA, renamedB]));
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
      // US-1003's client filter is on document names only, so a rename never
      // removes a row — or moves a counter.
      expect(store.entities().length).toBe(3);
      expect(store.totalCount()).toBe(3);
    });

    it('drops a deleted conversation’s rows and takes both counters down with them', () => {
      store.bindSource(false);
      const doomedA = userDocumentFixture();
      const doomedB = userDocumentFixture({ conversationId: doomedA.conversationId });
      const kept = userDocumentFixture();
      expectRequest().flush(documentPage([doomedA, doomedB, kept]));
      TestBed.tick();
      expect(store.skip()).toBe(3);

      TestBed.inject(Dispatcher).dispatch(conversationEvents.deleted(doomedA.conversationId));
      TestBed.tick();

      expect(store.entities().map((document) => document.id)).toEqual([kept.id]);
      // Both together: the total because the ceiling notice would otherwise count
      // rows that are gone, and the offset because `hasMore` compares the two.
      expect(store.totalCount()).toBe(1);
      expect(store.skip()).toBe(1);
    });
  });

  describe('client-side filter and grouping (US-1003)', () => {
    it('narrows on document names only, case-insensitively', () => {
      store.bindSource(false);
      const report = userDocumentFixture({ name: 'Q3-Report.pdf', conversationName: 'Alpha' });
      // The decoy's *conversation* name matches the term; its file name does not.
      const decoy = userDocumentFixture({ name: 'notes.txt', conversationName: 'report review' });
      expectRequest().flush(documentPage([report, decoy]));
      TestBed.tick();

      store.bindQuery('report');
      TestBed.tick();

      // Case-insensitive on the file name, and never on the conversation name —
      // "Filters cover file names only." is the screen's own promise.
      expect(store.results().map((document) => document.id)).toEqual([report.id]);
      expect(store.hasQuery()).toBe(true);
    });

    it('tells "nothing uploaded" apart from "no matches"', () => {
      store.bindSource(false);
      expectRequest().flush(documentPage([]));
      TestBed.tick();

      store.bindQuery('report');
      TestBed.tick();

      // Zero documents: whatever term a deep link carries, "Nothing uploaded yet"
      // is the truth — a cleared filter would reveal nothing.
      expect(store.isEmpty()).toBe(true);
      expect(store.hasNoMatches()).toBe(false);
      expect(store.countSummary()).toBe('No documents');
    });

    it('reports no matches when documents exist and the term excludes them all', () => {
      store.bindSource(false);
      expectRequest().flush(documentPage([userDocumentFixture({ name: 'notes.txt' })]));
      TestBed.tick();

      store.bindQuery('zzz');
      TestBed.tick();

      expect(store.isEmpty()).toBe(false);
      expect(store.hasNoMatches()).toBe(true);
    });

    it('groups filtered results in first-seen order, counting only what is visible', () => {
      store.bindSource(false);
      const alphaNewest = userDocumentFixture({
        name: 'alpha-plan.pdf',
        conversationName: 'Alpha',
      });
      const beta = userDocumentFixture({ name: 'beta-notes.txt', conversationName: 'Beta' });
      const alphaOlder = userDocumentFixture({
        name: 'alpha-summary.md',
        conversationId: alphaNewest.conversationId,
        conversationName: 'Alpha',
      });
      expectRequest().flush(documentPage([alphaNewest, beta, alphaOlder]));
      TestBed.tick();

      // First-seen over a newest-first listing: groups order by their newest document.
      let groups = store.groups();
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

      store.bindQuery('summary');
      TestBed.tick();

      // Counts cover the visible files, and a group with none left disappears.
      groups = store.groups();
      expect(groups.map((group) => group.conversationId)).toEqual([alphaNewest.conversationId]);
      expect(groups[0]?.countLabel).toBe('1 file');
      expect(groups[0]?.documents.map((document) => document.id)).toEqual([alphaOlder.id]);
    });

    it('counts the envelope total unfiltered, and shown-of-total filtered', () => {
      store.bindSource(false);
      expectRequest().flush(
        documentPage([
          userDocumentFixture({ name: 'report.pdf' }),
          userDocumentFixture({ name: 'notes.txt' }),
        ]),
      );
      TestBed.tick();

      expect(store.countSummary()).toBe('2 documents');

      store.bindQuery('report');
      TestBed.tick();

      expect(store.countSummary()).toBe('Showing 1 of 2 documents');
    });

    it('speaks in the singular for one document', () => {
      store.bindSource(false);
      expectRequest().flush(documentPage([userDocumentFixture()]));
      TestBed.tick();

      expect(store.countSummary()).toBe('1 document');
    });

    it('stays honest under the ceiling: the envelope total, and a qualified filter', () => {
      store.bindSource(false);

      for (let page = 0; page * DOCUMENT_PAGE_SIZE < DOCUMENT_LOAD_CEILING; page++) {
        expectRequest().flush(
          documentPage(fullPage(), { totalCount: 600, skip: page * DOCUMENT_PAGE_SIZE }),
        );
        TestBed.tick();
      }

      // The envelope's 600, not the 500 rows held — the ceiling notice carries the
      // shortfall, so the count must not double-report it.
      expect(store.countSummary()).toBe('600 documents');

      store.bindQuery('no-fixture-is-named-this');
      TestBed.tick();

      // A filter over a truncated set is qualified rather than suppressed.
      expect(store.countSummary()).toBe('Showing 0 of 600 documents');
      expect(store.resultsPartialReason()).toBe('Only the 500 most recent documents have loaded.');
    });
  });

  describe('served origin filter', () => {
    it('asks for every origin until one is bound', () => {
      store.bindSource(false);

      expect(expectRequest().request.params.has('type')).toBe(false);
    });

    it('narrows the request when the assistant-created filter is on', () => {
      store.bindSource(true);

      expectGeneratedRequest().flush(documentPage([userDocumentFixture({ type: 'Generated' })]));
      TestBed.tick();

      expect(store.generatedOnly()).toBe(true);
      expect(store.entities()).toHaveLength(1);
    });

    it('supersedes a drain still in flight rather than mixing the two sets', () => {
      store.bindSource(false);
      expectRequest().flush(documentPage(fullPage(), { totalCount: 250 }));
      TestBed.tick();

      const stale = expectRequest();
      store.bindSource(true);

      expect(stale.cancelled).toBe(true);
      expectGeneratedRequest().flush(documentPage([userDocumentFixture({ type: 'Generated' })]));
      TestBed.tick();

      // The 100 rows of the superseded drain are gone, not appended to.
      expect(store.entities()).toHaveLength(1);
      expect(store.totalCount()).toBe(1);
    });

    it('carries the filter through every page of one drain', () => {
      store.bindSource(true);
      expectGeneratedRequest().flush(documentPage(fullPage(), { totalCount: 150 }));
      TestBed.tick();

      expectGeneratedRequest().flush(
        documentPage(fullPage(50), { totalCount: 150, skip: DOCUMENT_PAGE_SIZE }),
      );
      TestBed.tick();

      expect(store.entities()).toHaveLength(150);
    });

    it('drops the previous rows the moment the origin changes', () => {
      store.bindSource(false);
      expectRequest().flush(documentPage([userDocumentFixture()]));
      TestBed.tick();
      expect(store.totalCount()).toBe(1);

      store.bindSource(true);

      // Held rows under the new filter would be counted and captioned as that filter's
      // set for a whole round trip; the skeleton is the honest thing to show instead.
      expect(store.entities()).toEqual([]);
      expect(store.totalCount()).toBe(0);
      expect(store.isFirstLoad()).toBe(true);

      expectGeneratedRequest().flush(documentPage([]));
      TestBed.tick();
    });

    it('names the narrowed set in every count and notice', () => {
      store.bindSource(true);

      for (let page = 0; page * DOCUMENT_PAGE_SIZE < DOCUMENT_LOAD_CEILING; page++) {
        expectGeneratedRequest().flush(
          documentPage(fullPage(), { totalCount: 600, skip: page * DOCUMENT_PAGE_SIZE }),
        );
        TestBed.tick();
      }

      // Every one of these would otherwise describe the whole library, which is the
      // dishonesty serving the filter exists to avoid.
      expect(store.countSummary()).toBe('600 generated documents');
      expect(store.ceilingNotice()).toBe('Showing the 500 most recent of 600 generated documents.');

      store.bindQuery('no-fixture-is-named-this');
      TestBed.tick();

      expect(store.resultsPartialReason()).toBe(
        'Only the 500 most recent generated documents have loaded.',
      );
    });

    it('retries the drain the filter last ran with', () => {
      store.bindSource(true);
      expectGeneratedRequest().error(new ProgressEvent('error'));
      TestBed.tick();

      store.retry();

      expectGeneratedRequest().flush(documentPage([]));
      TestBed.tick();
      expect(store.isFulfilled()).toBe(true);
    });
  });

  it('cancels its drain on sign-out and empties', () => {
    store.bindSource(false);
    expectRequest().flush(documentPage(fullPage(), { totalCount: 250 }));
    TestBed.tick();

    const inFlight = expectRequest();
    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    TestBed.tick();

    expect(inFlight.cancelled).toBe(true);
    expect(store.entities()).toEqual([]);
    expect(store.truncated()).toBe(false);
  });
});
