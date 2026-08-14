import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { conversationEvents } from '@core/events/conversation-events';
import { sessionEvents } from '@core/events/session-events';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { ConversationLibraryStore, LIBRARY_PAGE_SIZE } from './conversation-library-store';

const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;

describe('ConversationLibraryStore (US-701)', () => {
  let backend: HttpTestingController;
  let store: InstanceType<typeof ConversationLibraryStore>;
  let term: ReturnType<typeof signal<string>>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        ConversationLibraryStore,
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(ConversationLibraryStore);
    term = signal('');
  });

  afterEach(() => {
    backend.verify();
  });

  /** Wires the query the way the screen's constructor does. */
  function bind(): void {
    TestBed.runInInjectionContext(() => store.bindQuery(term));
    TestBed.tick();
  }

  function expectSearch(): TestRequest {
    return backend.expectOne((request) => request.url === SEARCH_URL);
  }

  async function settle(): Promise<void> {
    TestBed.tick();
    await Promise.resolve();
  }

  it('asks for the first page, and never sends filter=', async () => {
    bind();

    const request = expectSearch();
    expect(request.request.params.get('skip')).toBe('0');
    expect(request.request.params.get('take')).toBe(String(LIBRARY_PAGE_SIZE));
    expect(request.request.params.has('name')).toBe(false);
    expect(request.request.params.has('filter')).toBe(false);
    // The fixture echoes the clamped `take`, as the server does — its default of
    // 50 would otherwise be adopted by `setFirstPage` and every later request in
    // the suite would ask for a page size this store never chose.
    request.flush(conversationPage([conversationFixture()], { pageSize: LIBRARY_PAGE_SIZE }));
    await settle();

    expect(store.entities()).toHaveLength(1);
    expect(store.take()).toBe(LIBRARY_PAGE_SIZE);
    expect(store.isFulfilled()).toBe(true);
  });

  it('supersedes an in-flight query rather than racing it', async () => {
    bind();
    const first = expectSearch();

    term.set('quarterly');
    TestBed.tick();

    // switchMap, because typing drives this: a slow response to "quar" must not
    // paint over a fast one to "quarterly".
    expect(first.cancelled).toBe(true);
    const second = expectSearch();
    expect(second.request.params.get('name')).toBe('quarterly');
    second.flush(conversationPage([]));
    await settle();
  });

  it('names the term the rows on screen belong to', async () => {
    bind();
    expectSearch().flush(conversationPage([conversationFixture()]));
    await settle();

    term.set('quarterly');
    TestBed.tick();

    // Written when the request starts, so frame 4b's heading cannot name a term
    // whose results have not arrived.
    expect(store.name()).toBe('quarterly');
    expectSearch().flush(conversationPage([]));
    await settle();

    expect(store.hasNoMatches()).toBe(true);
    expect(store.isEmpty()).toBe(true);
  });

  it('tells an empty result from an empty library', async () => {
    bind();
    expectSearch().flush(conversationPage([]));
    await settle();

    expect(store.isEmpty()).toBe(true);
    expect(store.hasNoMatches()).toBe(false);
  });

  it('retries the term it is holding, and clears the error on success', async () => {
    bind();
    expectSearch().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 500,
      statusText: 'Internal Server Error',
    });
    await settle();
    expect(store.error()).not.toBeNull();

    store.retry();
    TestBed.tick();
    expectSearch().flush(conversationPage([conversationFixture()]));
    await settle();

    expect(store.error()).toBeNull();
    expect(store.entities()).toHaveLength(1);
  });

  describe('staying in step with the sidebar', () => {
    it('patches a row the server confirmed changed', async () => {
      const conversation = conversationFixture({ name: 'Old name' });
      bind();
      expectSearch().flush(conversationPage([conversation]));
      await settle();

      TestBed.inject(Dispatcher).dispatch(
        conversationEvents.updated({ ...conversation, name: 'New name' }),
      );
      await settle();

      expect(store.entities()[0]?.name).toBe('New name');
    });

    it('ignores an update for a row it does not hold', async () => {
      bind();
      expectSearch().flush(conversationPage([conversationFixture({ name: 'Mine' })]));
      await settle();

      TestBed.inject(Dispatcher).dispatch(conversationEvents.updated(conversationFixture()));
      await settle();

      expect(store.entities()).toHaveLength(1);
      expect(store.entities()[0]?.name).toBe('Mine');
    });

    it('removes a deleted row and takes both counters down with it', async () => {
      const conversation = conversationFixture();
      bind();
      expectSearch().flush(
        conversationPage([conversation], { totalCount: 12, pageSize: LIBRARY_PAGE_SIZE }),
      );
      await settle();
      const skip = store.skip();

      TestBed.inject(Dispatcher).dispatch(conversationEvents.deleted(conversation.id));
      await settle();

      expect(store.entities()).toHaveLength(0);
      // US-702's "Showing N of M" would otherwise claim a row that is gone…
      expect(store.totalCount()).toBe(11);
      // …and its next page would start one row late, leaving a permanent hole.
      expect(store.skip()).toBe(skip - 1);
    });

    it('leaves the counters alone for a row it never held', async () => {
      bind();
      expectSearch().flush(
        conversationPage([conversationFixture()], { totalCount: 12, pageSize: LIBRARY_PAGE_SIZE }),
      );
      await settle();
      const skip = store.skip();

      TestBed.inject(Dispatcher).dispatch(conversationEvents.deleted('not-in-this-list'));
      await settle();

      expect(store.totalCount()).toBe(12);
      expect(store.skip()).toBe(skip);
    });
  });

  it('empties on sign-out and drops a response already on the wire', async () => {
    bind();
    // A first page has to land, or "empties" is asserted against a store that was
    // already empty and the reset itself goes untested.
    expectSearch().flush(conversationPage([conversationFixture()], { totalCount: 12 }));
    await settle();

    term.set('quarterly');
    TestBed.tick();
    const inFlight = expectSearch();
    expect(store.entities()).toHaveLength(1);

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    await settle();

    expect(store.entities()).toHaveLength(0);
    expect(store.name()).toBe('');
    expect(store.totalCount()).toBe(0);
    // `withResetOnSignOut` clears state and does not cancel work, which is why the
    // inner request carries its own `takeUntil` — without it this response would
    // write the previous user's rows back over an emptied store.
    expect(inFlight.cancelled).toBe(true);
  });
});
