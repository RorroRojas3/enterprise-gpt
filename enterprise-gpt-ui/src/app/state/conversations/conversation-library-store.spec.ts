import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Dispatcher, Events } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ConversationDto } from '@domain/api/conversation';
import { conversationEvents } from '@core/events/conversation-events';
import { sessionEvents } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { ConversationLibraryStore, LIBRARY_PAGE_SIZE } from './conversation-library-store';
import {
  CONVERSATION_SORTS,
  ConversationSortOption,
  DEFAULT_CONVERSATION_SORT,
} from './conversations-route';

const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;
const BULK_URL = `${TEST_API_BASE_URL}/api/conversations/bulk`;

describe('ConversationLibraryStore', () => {
  let backend: HttpTestingController;
  let store: InstanceType<typeof ConversationLibraryStore>;
  let term: ReturnType<typeof signal<string>>;
  let favorites: ReturnType<typeof signal<boolean>>;
  let sort: ReturnType<typeof signal<ConversationSortOption>>;

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
    favorites = signal(false);
    sort = signal(DEFAULT_CONVERSATION_SORT);
  });

  afterEach(() => {
    backend.verify();
  });

  /** Binds the query the way the screen's constructor does. */
  function bind(): void {
    TestBed.runInInjectionContext(() =>
      store.bindQuery(() => ({ name: term(), favorites: favorites(), sort: sort() })),
    );
    TestBed.tick();
  }

  function option(value: string): ConversationSortOption {
    const found = CONVERSATION_SORTS.find((candidate) => candidate.value === value);

    if (found === undefined) {
      throw new Error(`No sort option named ${value}.`);
    }

    return found;
  }

  function expectSearch(): TestRequest {
    return backend.expectOne((request) => request.url === SEARCH_URL);
  }

  async function settle(): Promise<void> {
    TestBed.tick();
    await Promise.resolve();
  }

  describe('the order', () => {
    it('sends the default order rather than relying on the server having one', async () => {
      bind();

      const request = expectSearch();
      expect(request.request.params.get('sort')).toBe('created');
      expect(request.request.params.get('dir')).toBe('desc');

      request.flush(conversationPage([conversationFixture()], { pageSize: LIBRARY_PAGE_SIZE }));
      await settle();
    });

    it('maps an option onto the API pair rather than sending its own vocabulary', async () => {
      sort.set(option('oldest'));
      bind();

      const request = expectSearch();
      expect(request.request.params.get('sort')).toBe('created');
      expect(request.request.params.get('dir')).toBe('asc');

      request.flush(conversationPage([conversationFixture()], { pageSize: LIBRARY_PAGE_SIZE }));
      await settle();
    });

    it('replaces the rows when the order changes rather than appending a second run', async () => {
      bind();
      expectSearch().flush(
        conversationPage([conversationFixture(), conversationFixture()], {
          totalCount: 2,
          pageSize: LIBRARY_PAGE_SIZE,
        }),
      );
      await settle();

      sort.set(option('name'));
      await settle();

      const reordered = expectSearch();
      expect(reordered.request.params.get('skip')).toBe('0');
      expect(reordered.request.params.get('sort')).toBe('name');
      reordered.flush(
        conversationPage([conversationFixture()], { totalCount: 1, pageSize: LIBRARY_PAGE_SIZE }),
      );
      await settle();

      expect(store.entities()).toHaveLength(1);
    });
  });

  it('asks for the first page, and never sends filter=', async () => {
    bind();

    const request = expectSearch();
    expect(request.request.params.get('skip')).toBe('0');
    expect(request.request.params.get('take')).toBe(String(LIBRARY_PAGE_SIZE));
    expect(request.request.params.has('name')).toBe(false);
    expect(request.request.params.has('filter')).toBe(false);
    // Echoes the clamped `take` as the server does, or `setFirstPage` adopts the fixture's 50.
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

    // Written when the request starts, not when it settles.
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

  describe('paging through the list', () => {
    /** A first page that leaves more behind, so `hasMore()` is true. */
    async function loadFirstPage(totalCount = 60): Promise<ConversationDto[]> {
      const items = Array.from({ length: LIBRARY_PAGE_SIZE }, () => conversationFixture());
      bind();
      expectSearch().flush(conversationPage(items, { totalCount, pageSize: LIBRARY_PAGE_SIZE }));
      await settle();
      return items;
    }

    it('counts the rows on screen against the envelope total', async () => {
      await loadFirstPage(312);

      expect(store.countSummary()).toBe('Showing 25 of 312 conversations');
      expect(store.hasMore()).toBe(true);
    });

    it('appends the next page in server order', async () => {
      const first = await loadFirstPage();

      store.loadMore();
      TestBed.tick();

      const request = expectSearch();
      expect(request.request.params.get('skip')).toBe(String(LIBRARY_PAGE_SIZE));
      expect(request.request.params.get('take')).toBe(String(LIBRARY_PAGE_SIZE));

      const second = [conversationFixture(), conversationFixture()];
      request.flush(
        conversationPage(second, {
          totalCount: 60,
          pageSize: LIBRARY_PAGE_SIZE,
          skip: LIBRARY_PAGE_SIZE,
        }),
      );
      await settle();

      expect(store.entities().map((row) => row.id)).toEqual(
        [...first, ...second].map((row) => row.id),
      );
      expect(store.countSummary()).toBe('Showing 27 of 60 conversations');
    });

    it('never asks for more than the server clamps to, whatever the envelope echoes', async () => {
      bind();
      // `setFirstPage` adopts the envelope's `pageSize`, the one way `take` could exceed 100.
      expectSearch().flush(
        conversationPage(
          Array.from({ length: LIBRARY_PAGE_SIZE }, () => conversationFixture()),
          { totalCount: 600, pageSize: 500 },
        ),
      );
      await settle();
      expect(store.take()).toBe(100);

      store.loadMore();
      TestBed.tick();

      const request = expectSearch();
      expect(Number(request.request.params.get('take'))).toBeLessThanOrEqual(100);

      request.flush(conversationPage([], { totalCount: 600, pageSize: 100, skip: 25 }));
      await settle();
    });

    it('drops a page when a row is deleted out from under its offset', async () => {
      const items = await loadFirstPage();

      store.loadMore();
      TestBed.tick();
      const page = expectSearch();

      TestBed.inject(Dispatcher).dispatch(conversationEvents.deleted(items[0]!.id));
      await settle();

      // Built against the pre-removal offset, it would skip the row that slid into the gap.
      expect(page.cancelled).toBe(true);
      expect(store.loadingMore()).toBe(false);
    });

    it('drops a page in flight on sign-out', async () => {
      await loadFirstPage();

      store.loadMore();
      TestBed.tick();
      const page = expectSearch();

      TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
      await settle();

      // The sign-out arm of `takeUntil(merge(...))`; the superseded-query test covers the other.
      expect(page.cancelled).toBe(true);
      expect(store.entities()).toHaveLength(0);
    });

    it('stops offering more once everything is loaded', async () => {
      await loadFirstPage(LIBRARY_PAGE_SIZE);

      expect(store.hasMore()).toBe(false);
      expect(store.isFullyLoaded()).toBe(true);

      // The store guards too, not just the template.
      store.loadMore();
      TestBed.tick();
      backend.expectNone(() => true);
    });

    it('ignores a second press while a page is on the wire', async () => {
      await loadFirstPage();

      store.loadMore();
      TestBed.tick();
      const inFlight = expectSearch();
      expect(store.loadingMore()).toBe(true);

      store.loadMore();
      TestBed.tick();
      backend.expectNone(() => true);

      inFlight.flush(
        conversationPage([], { totalCount: 60, pageSize: LIBRARY_PAGE_SIZE, skip: 25 }),
      );
      await settle();
      expect(store.loadingMore()).toBe(false);
    });

    it('keeps the loaded rows when a page fails, and toasts instead', async () => {
      await loadFirstPage();
      const toasts = TestBed.inject(ToastStore);

      store.loadMore();
      TestBed.tick();
      expectSearch().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
        status: 500,
        statusText: 'Internal Server Error',
      });
      await settle();

      expect(store.entities()).toHaveLength(LIBRARY_PAGE_SIZE);
      expect(store.error()).toBeNull();
      expect(store.loadingMore()).toBe(false);
      expect(toasts.toasts()).toHaveLength(1);
    });

    it('drops a page whose result set a newer query replaced', async () => {
      await loadFirstPage();

      store.loadMore();
      TestBed.tick();
      const page = expectSearch();

      term.set('quarterly');
      TestBed.tick();

      expect(page.cancelled).toBe(true);
      expect(store.loadingMore()).toBe(false);

      expectSearch().flush(conversationPage([], { pageSize: LIBRARY_PAGE_SIZE }));
      await settle();
    });
  });

  describe('filtering to favourites', () => {
    it('sends isFavorite=true, and never isFavorite=false', async () => {
      bind();
      const unfiltered = expectSearch();
      expect(unfiltered.request.params.has('isFavorite')).toBe(false);
      unfiltered.flush(conversationPage([], { pageSize: LIBRARY_PAGE_SIZE }));
      await settle();

      favorites.set(true);
      TestBed.tick();
      const filtered = expectSearch();
      expect(filtered.request.params.get('isFavorite')).toBe('true');
      filtered.flush(conversationPage([], { pageSize: LIBRARY_PAGE_SIZE }));
      await settle();

      favorites.set(false);
      TestBed.tick();
      const off = expectSearch();
      // `isFavorite=false` means "only unstarred", a state the screen has no control for.
      expect(off.request.params.has('isFavorite')).toBe(false);
      off.flush(conversationPage([], { pageSize: LIBRARY_PAGE_SIZE }));
      await settle();
    });

    it('sends both parameters on one request', async () => {
      term.set('quarterly');
      favorites.set(true);
      bind();

      const request = expectSearch();
      expect(request.request.params.get('name')).toBe('quarterly');
      expect(request.request.params.get('isFavorite')).toBe('true');
      request.flush(conversationPage([], { pageSize: LIBRARY_PAGE_SIZE }));
      await settle();
    });

    it('carries the filter on to the next page', async () => {
      favorites.set(true);
      bind();
      expectSearch().flush(
        conversationPage([conversationFixture({ isFavorite: true })], {
          totalCount: 40,
          pageSize: LIBRARY_PAGE_SIZE,
        }),
      );
      await settle();

      store.loadMore();
      TestBed.tick();

      const request = expectSearch();
      expect(request.request.params.get('isFavorite')).toBe('true');
      request.flush(conversationPage([], { totalCount: 40, pageSize: LIBRARY_PAGE_SIZE, skip: 1 }));
      await settle();
    });

    it('drops a row the server confirms is no longer a favourite', async () => {
      const conversation = conversationFixture({ isFavorite: true });
      favorites.set(true);
      bind();
      expectSearch().flush(
        conversationPage([conversation], { totalCount: 12, pageSize: LIBRARY_PAGE_SIZE }),
      );
      await settle();
      const skip = store.skip();

      TestBed.inject(Dispatcher).dispatch(
        conversationEvents.updated({ ...conversation, isFavorite: false }),
      );
      await settle();

      expect(store.entities()).toHaveLength(0);
      expect(store.totalCount()).toBe(11);
      expect(store.skip()).toBe(skip - 1);
      expect(store.hasNoFavorites()).toBe(true);
    });

    it('keeps an unfavourited row when the filter is off', async () => {
      const conversation = conversationFixture({ isFavorite: true });
      bind();
      expectSearch().flush(
        conversationPage([conversation], { totalCount: 12, pageSize: LIBRARY_PAGE_SIZE }),
      );
      await settle();

      TestBed.inject(Dispatcher).dispatch(
        conversationEvents.updated({ ...conversation, isFavorite: false }),
      );
      await settle();

      expect(store.entities()).toHaveLength(1);
      expect(store.entities()[0]?.isFavorite).toBe(false);
      expect(store.totalCount()).toBe(12);
    });

    it('tells an empty favourites filter from an empty search', async () => {
      favorites.set(true);
      bind();
      expectSearch().flush(conversationPage([], { pageSize: LIBRARY_PAGE_SIZE }));
      await settle();

      expect(store.hasNoFavorites()).toBe(true);
      // The no-matches state quotes a term, and there is none.
      expect(store.hasNoMatches()).toBe(false);
      expect(store.hasQuery()).toBe(true);
    });
  });

  describe('deleting several at once', () => {
    async function loadRows(count = 3, totalCount = 12): Promise<ConversationDto[]> {
      const items = Array.from({ length: count }, () => conversationFixture());
      bind();
      expectSearch().flush(conversationPage(items, { totalCount, pageSize: LIBRARY_PAGE_SIZE }));
      await settle();
      return items;
    }

    function expectBulk(): TestRequest {
      return backend.expectOne((request) => request.url === BULK_URL);
    }

    it('select-all covers the loaded rows only, and says so', async () => {
      const items = await loadRows(3, 312);

      store.toggleSelectAll();
      await settle();

      expect(store.selectedCount()).toBe(items.length);
      expect(store.allLoadedSelected()).toBe(true);
      // The endpoint takes ids, not a query, so the bar states select-all's reach.
      expect(store.selectAllNote()).toBe('Select-all covers the 3 loaded rows only');

      store.toggleSelectAll();
      await settle();
      expect(store.selectedCount()).toBe(0);
    });

    it('sends every id on one request and removes the rows at once', async () => {
      const items = await loadRows(3);
      store.setSelection(new Set([items[0]!.id, items[2]!.id]));
      await settle();

      store.deleteSelected();
      await settle();

      const request = expectBulk();
      expect(request.request.method).toBe('DELETE');
      // `HttpClient.delete` drops a body unless it goes through the options object,
      // and the endpoint binds `[FromBody]`.
      expect(request.request.body).toEqual({
        conversationIds: [items[0]!.id, items[2]!.id],
      });

      // Optimistic: the rows are gone before the server answers.
      expect(store.entities().map((row) => row.id)).toEqual([items[1]!.id]);
      expect(store.totalCount()).toBe(10);
      expect(store.selectedCount()).toBe(0);

      request.flush(null, { status: 204, statusText: 'No Content' });
      await settle();
    });

    it('announces each deletion once the server has confirmed the batch', async () => {
      const items = await loadRows(2);
      const seen: string[] = [];
      TestBed.inject(Events)
        .on(conversationEvents.deleted)
        .subscribe(({ payload }) => seen.push(payload));

      store.toggleSelectAll();
      await settle();
      store.deleteSelected();
      await settle();

      // Nothing announced yet: listeners react to server-confirmed facts only.
      expect(seen).toEqual([]);

      expectBulk().flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      expect(seen).toEqual(items.map((row) => row.id));
      // The store's own `deleted` handler skips rows it no longer holds, so none count twice.
      expect(store.totalCount()).toBe(10);
    });

    it('puts every row back, re-selects it and raises one toast when the batch fails', async () => {
      const items = await loadRows(3);
      const toasts = TestBed.inject(ToastStore);
      store.setSelection(new Set([items[0]!.id, items[2]!.id]));
      await settle();

      store.deleteSelected();
      await settle();
      expectBulk().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
        status: 500,
        statusText: 'Internal Server Error',
      });
      await settle();

      expect(store.entities().map((row) => row.id)).toEqual(items.map((row) => row.id));
      expect(store.totalCount()).toBe(12);
      expect([...store.selectedIds()]).toEqual([items[0]!.id, items[2]!.id]);
      expect(toasts.toasts()).toHaveLength(1);
    });

    it('refuses to restore into a result set the reader has replaced', async () => {
      const items = await loadRows(3);
      store.toggleSelectAll();
      await settle();
      store.deleteSelected();
      await settle();
      const bulk = expectBulk();

      term.set('quarterly');
      TestBed.tick();
      expectSearch().flush(
        conversationPage([conversationFixture({ name: 'Quarterly' })], {
          pageSize: LIBRARY_PAGE_SIZE,
        }),
      );
      await settle();

      bulk.flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
        status: 500,
        statusText: 'Internal Server Error',
      });
      await settle();

      expect(store.entities().map((row) => row.name)).toEqual(['Quarterly']);
      expect(store.entities().map((row) => row.id)).not.toContain(items[0]!.id);
    });

    it('prunes the selection to the rows still loaded', async () => {
      const items = await loadRows(3);
      store.toggleSelectAll();
      await settle();

      TestBed.inject(Dispatcher).dispatch(conversationEvents.deleted(items[0]!.id));
      await settle();
      expect(store.selectedCount()).toBe(2);

      term.set('quarterly');
      TestBed.tick();
      expectSearch().flush(conversationPage([], { pageSize: LIBRARY_PAGE_SIZE }));
      await settle();

      expect(store.selectedCount()).toBe(0);
    });

    it('keeps the selection across a Load more', async () => {
      const items = await loadRows(3, 40);
      store.setSelection(new Set([items[0]!.id]));
      await settle();

      store.loadMore();
      TestBed.tick();
      expectSearch().flush(
        conversationPage([conversationFixture()], {
          totalCount: 40,
          pageSize: LIBRARY_PAGE_SIZE,
          skip: 3,
        }),
      );
      await settle();

      expect([...store.selectedIds()]).toEqual([items[0]!.id]);
    });

    it('ignores a delete with nothing selected', async () => {
      await loadRows();

      store.deleteSelected();
      await settle();

      // An empty id list is a 400 from the validator.
      backend.expectNone(() => true);
    });
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
      expect(store.totalCount()).toBe(11);
      // Or the next page would start one row late and skip a row.
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
    // Land a first page, or "empties" would pass against a store that was never filled.
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
    // The reset clears state but cancels nothing; without its own `takeUntil` this response
    // would write the previous user's rows back.
    expect(inFlight.cancelled).toBe(true);
  });
});
