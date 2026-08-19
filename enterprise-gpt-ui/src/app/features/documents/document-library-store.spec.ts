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
    store.load();

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
    store.load();

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
    store.load();
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
    store.load();
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
      store.load();
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
      store.load();
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
      store.load();
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
      store.load();
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
      store.load();
      expectRequest().flush(documentPage([userDocumentFixture({ name: 'notes.txt' })]));
      TestBed.tick();

      store.bindQuery('zzz');
      TestBed.tick();

      expect(store.isEmpty()).toBe(false);
      expect(store.hasNoMatches()).toBe(true);
    });

    it('groups filtered results in first-seen order, counting only what is visible', () => {
      store.load();
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
      store.load();
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
      store.load();
      expectRequest().flush(documentPage([userDocumentFixture()]));
      TestBed.tick();

      expect(store.countSummary()).toBe('1 document');
    });

    it('stays honest under the ceiling: the envelope total, and a qualified filter', () => {
      store.load();

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

  it('cancels its drain on sign-out and empties', () => {
    store.load();
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
