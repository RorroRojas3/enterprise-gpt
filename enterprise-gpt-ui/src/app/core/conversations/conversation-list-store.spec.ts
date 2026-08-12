import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { sessionEvents } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { Dispatcher } from '@ngrx/signals/events';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { ConversationDto } from '@domain/api/conversation';
import { conversationFixture, conversationPage } from '@testing/conversations';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ConversationListStore } from './conversation-list-store';

const SEARCH_URL = `${TEST_API_BASE_URL}/api/conversations/search`;

describe('ConversationListStore', () => {
  let store: InstanceType<typeof ConversationListStore>;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    store = TestBed.inject(ConversationListStore);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
  });

  /**
   * Flushes the effect behind the wired query. The load is declarative — a signal
   * computation drives `rxMethod` — so state changes reach the network only after a
   * change-detection pass.
   */
  function flush(): void {
    TestBed.tick();
  }

  function signOut(): void {
    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
  }

  /** The one outstanding search request, whatever its query string. */
  function expectSearch(): TestRequest {
    return backend.expectOne((request) => request.url === SEARCH_URL);
  }

  /** Gets the store past its first page so a test can start from a loaded list. */
  function load(items = [conversationFixture(), conversationFixture()], totalCount?: number) {
    store.ensureLoaded();
    flush();
    expectSearch().flush(conversationPage(items, { totalCount: totalCount ?? items.length }));
    flush();

    return items;
  }

  it('omits name entirely on the first load', () => {
    store.ensureLoaded();
    flush();

    const request = expectSearch();

    // US-302: `name=` omitted, not sent empty. An empty value still takes the server
    // down its LIKE branch.
    expect(request.request.params.has('name')).toBe(false);
    expect(request.request.params.get('skip')).toBe('0');
    expect(request.request.params.get('take')).toBe('50');

    request.flush(conversationPage([]));
    flush();
  });

  it('issues nothing until the session bootstrap releases it', () => {
    // The store is constructed by the injector; the request must wait for
    // `ensureLoaded`, or it goes out before `POST api/users/me` has produced a token.
    flush();
    backend.expectNone(() => true);
  });

  it('renders the rows in the order the server returned them', () => {
    const [first, second] = [
      conversationFixture({ name: 'Newest' }),
      conversationFixture({ name: 'Older' }),
    ];
    load([first, second]);

    // Server order is `dateCreated` descending and no endpoint accepts a sort
    // parameter, so the client must not re-sort.
    expect(store.entities().map((c) => c.name)).toEqual(['Newest', 'Older']);
  });

  it('searches by name, not by filter', () => {
    load();

    store.search('helios');
    flush();

    const request = expectSearch();

    // The named regression from the old client, which sent `filter=` and matched
    // nothing server-side.
    expect(request.request.params.get('name')).toBe('helios');
    expect(request.request.params.has('filter')).toBe(false);

    request.flush(conversationPage([conversationFixture({ name: 'Helios 2.4' })]));
    flush();

    expect(store.entities()).toHaveLength(1);
  });

  it('trims the search term and drops a whitespace-only one', () => {
    load();

    store.search('  helios  ');
    flush();
    const trimmed = expectSearch();
    expect(trimmed.request.params.get('name')).toBe('helios');
    trimmed.flush(conversationPage([]));
    flush();

    store.search('   ');
    flush();
    const blank = expectSearch();
    expect(blank.request.params.has('name')).toBe(false);
    blank.flush(conversationPage([]));
    flush();
  });

  it('lets the newest query win over one still in flight', () => {
    // switchMap is why this store uses rxMethod at all: a slow response to "hel" must
    // not paint over a fast response to "helios".
    load();

    store.search('hel');
    flush();
    const slow = expectSearch();

    store.search('helios');
    flush();
    const fast = expectSearch();

    expect(slow.cancelled).toBe(true);

    fast.flush(conversationPage([conversationFixture({ name: 'Helios 2.4' })]));
    flush();

    expect(store.entities().map((c) => c.name)).toEqual(['Helios 2.4']);
  });

  it('replaces rather than appends when a search narrows the list', () => {
    load([conversationFixture(), conversationFixture(), conversationFixture()], 120);

    store.search('helios');
    flush();
    expectSearch().flush(conversationPage([conversationFixture({ name: 'Helios 2.4' })]));
    flush();

    expect(store.entities()).toHaveLength(1);
    // setFirstPage, not setPage: carrying the old offset forward is how a narrowed
    // search comes back empty until the user pages backwards.
    expect(store.skip()).toBe(1);
    expect(store.hasMore()).toBe(false);
  });

  it('appends the next page and advances the offset by what actually arrived', () => {
    const first = load([conversationFixture(), conversationFixture()], 3);
    expect(store.hasMore()).toBe(true);

    store.loadMore();
    const request = expectSearch();
    expect(request.request.params.get('skip')).toBe('2');

    request.flush(conversationPage([conversationFixture({ name: 'Third' })], { totalCount: 3 }));
    flush();

    expect(store.entities().map((c) => c.name)).toEqual([first[0]?.name, first[1]?.name, 'Third']);
    expect(store.skip()).toBe(3);
    expect(store.hasMore()).toBe(false);
    expect(store.loadingMore()).toBe(false);
  });

  it('ignores a second Load more while one is already in flight', () => {
    load([conversationFixture()], 10);

    store.loadMore();
    store.loadMore();

    expectSearch().flush(conversationPage([conversationFixture()], { totalCount: 10, skip: 1 }));
    flush();
  });

  it('refuses Load more while the result set beneath it is being replaced', () => {
    // The page would carry the *old* offset against the *new* result set: landing
    // after the query's setFirstPage it appends a window from the middle of a list
    // that no longer exists, leaving a hole no scroll can reach and hasMore() false.
    load([conversationFixture(), conversationFixture()], 120);

    store.search('helios');
    flush();
    const query = expectSearch();

    store.loadMore();

    expect(store.loadingMore()).toBe(false);

    query.flush(
      conversationPage([conversationFixture({ name: 'Helios 2.4' })], { totalCount: 120 }),
    );
    flush();

    expect(store.entities().map((c) => c.name)).toEqual(['Helios 2.4']);
    expect(store.skip()).toBe(1);
  });

  it('drops a page that a fresh search has already superseded', () => {
    // switchMap cancels the query pipeline's own request but never reaches the second
    // pipeline behind Load more. Left running, its rows — from the previous result set
    // — land under the new one and its setPage advances skip past a window that no
    // longer exists.
    load([conversationFixture({ name: 'Unfiltered' })], 40);

    store.loadMore();
    const stale = expectSearch();

    store.search('helios');
    flush();

    expect(stale.cancelled).toBe(true);
    expect(store.loadingMore()).toBe(false);

    expectSearch().flush(conversationPage([conversationFixture({ name: 'Helios 2.4' })]));
    flush();

    expect(store.entities().map((c) => c.name)).toEqual(['Helios 2.4']);
    expect(store.skip()).toBe(1);
  });

  it('keeps the loaded rows and raises a toast when a further page fails', () => {
    load([conversationFixture()], 10);
    const toasts = TestBed.inject(ToastStore);

    store.loadMore();
    expectSearch().flush('', { status: 503, statusText: 'Service Unavailable' });
    flush();

    // Swapping a page of valid rows for an error panel would lose them; the failure is
    // announced instead.
    expect(store.entities()).toHaveLength(1);
    expect(store.error()).toBeNull();
    expect(toasts.assertiveToasts()).toHaveLength(1);
  });

  it('reports a failed first page as a typed error carrying the traceId', () => {
    store.ensureLoaded();
    flush();
    expectSearch().flush(PROBLEM_FIXTURES.forbidden, { status: 403, statusText: 'Forbidden' });
    flush();

    expect(store.error()?.kind).toBe('forbidden');
    expect(store.error()?.traceId).toBe(PROBLEM_FIXTURES.forbidden.traceId);
  });

  it('re-issues the identical query on retry', () => {
    store.ensureLoaded();
    flush();
    expectSearch().flush('', { status: 502, statusText: 'Bad Gateway' });
    flush();
    expect(store.error()).not.toBeNull();

    store.retry();
    flush();
    expectSearch().flush(conversationPage([conversationFixture()]));
    flush();

    expect(store.error()).toBeNull();
    expect(store.entities()).toHaveLength(1);
  });

  it('tells an empty account apart from an empty search', () => {
    store.ensureLoaded();
    flush();
    expectSearch().flush(conversationPage([]));
    flush();

    expect(store.isEmpty()).toBe(true);
    expect(store.hasNoMatches()).toBe(false);

    store.search('nothing matches this');
    flush();
    expectSearch().flush(conversationPage([]));
    flush();

    expect(store.isEmpty()).toBe(true);
    expect(store.hasNoMatches()).toBe(true);
  });

  it('adopts a fresher copy of a loaded row and ignores one it does not hold', () => {
    const [first] = load();

    store.refreshRow({ ...first!, name: 'Renamed by the server' });
    expect(store.entities()[0]?.name).toBe('Renamed by the server');

    // Rows are in the server's order; inserting one the list never asked for would put
    // it in the wrong place and desynchronise `skip` from the window it describes.
    store.refreshRow(conversationFixture({ id: 'deadbeef-0000-4000-8000-000000000000' }));
    expect(store.entities()).toHaveLength(2);
  });

  it('puts a just-created conversation at the head and keeps the window in step', () => {
    // Correct only because the server orders by dateCreated descending: a row created
    // a moment ago is at server position 0 by construction. US-401 calls this.
    load([conversationFixture({ name: 'Older' })], 10);

    const created = conversationFixture({ name: 'Brand new' });
    store.prependNewest(created);

    expect(store.entities().map((c) => c.name)).toEqual(['Brand new', 'Older']);
    expect(store.skip()).toBe(2);
    expect(store.totalCount()).toBe(11);

    // Idempotent: a second call for a row already held changes nothing.
    store.prependNewest(created);
    expect(store.entities()).toHaveLength(2);
    expect(store.totalCount()).toBe(11);
  });

  it('optimistically renames a held row and ignores one it does not hold', () => {
    const [first] = load();

    store.renameRow(first!.id, 'Renamed locally');
    expect(store.entities()[0]?.name).toBe('Renamed locally');
    // dateModified stays put: the server's own value arrives via refreshRow on
    // success, and a local bump would diverge from it on the next fetch.
    expect(store.entities()[0]?.dateModified).toBe(first!.dateModified);

    store.renameRow('deadbeef-0000-4000-8000-000000000000', 'Nope');
    expect(store.entities()).toHaveLength(2);
    expect(store.entities().some((c) => c.name === 'Nope')).toBe(false);
  });

  it('tracks per-row pending state and clears it on sign-out', () => {
    const [first, second] = load();

    store.setRowPending(first!.id, true);
    expect(store.isRowPending(first!.id)).toBe(true);
    // Per-row, not global: the other row stays actionable.
    expect(store.isRowPending(second!.id)).toBe(false);

    store.setRowPending(first!.id, false);
    expect(store.isRowPending(first!.id)).toBe(false);

    store.setRowPending(first!.id, true);
    signOut();
    expect(store.isRowPending(first!.id)).toBe(false);
  });

  it('optimistically removes a held row and shifts the window back with it', () => {
    const [first, second] = load([conversationFixture(), conversationFixture()], 10);

    const removal = store.removeRow(first!.id);

    expect(removal?.conversation.id).toBe(first!.id);
    expect(removal?.index).toBe(0);
    expect(store.entities().map((c) => c.id)).toEqual([second!.id]);
    // The inverse of prependNewest: the server no longer counts the row, so the next
    // "Load more" window must not skip a survivor.
    expect(store.skip()).toBe(1);
    expect(store.totalCount()).toBe(9);
    expect(store.hasMore()).toBe(true);
  });

  it('returns null for a row it does not hold and changes nothing', () => {
    load([conversationFixture()], 5);

    expect(store.removeRow('deadbeef-0000-4000-8000-000000000000')).toBeNull();
    expect(store.entities()).toHaveLength(1);
    expect(store.skip()).toBe(1);
    expect(store.totalCount()).toBe(5);
  });

  it('restores a removed row at its previous position with the window re-advanced', () => {
    const [a, b, c] = load(
      [conversationFixture(), conversationFixture(), conversationFixture()],
      10,
    );

    const removal = store.removeRow(b!.id);
    expect(store.entities().map((x) => x.id)).toEqual([a!.id, c!.id]);

    store.restoreRow(removal!);

    expect(store.entities().map((x) => x.id)).toEqual([a!.id, b!.id, c!.id]);
    expect(store.skip()).toBe(3);
    expect(store.totalCount()).toBe(10);

    // A no-op when the id is already back — a refetch raced the failed delete.
    store.restoreRow(removal!);
    expect(store.entities()).toHaveLength(3);
    expect(store.totalCount()).toBe(10);
  });

  it('cancels a Load-more page in flight when a row is removed beneath it', () => {
    // The page's URL carries the pre-removal offset; served after the server-side
    // delete it would leave a one-row hole between the windows.
    load([conversationFixture()], 10);

    store.loadMore();
    const stale = expectSearch();

    store.removeRow(store.entities()[0]!.id);

    expect(stale.cancelled).toBe(true);
    expect(store.loadingMore()).toBe(false);
  });

  it('refuses to restore into a result set the removal never touched', () => {
    // The user searched while the delete was in flight; the failed delete's row may
    // not even match the query on screen, and the counter bump would starve a
    // genuine row out of the next Load-more window.
    const [first] = load([conversationFixture({ name: 'Doomed' })], 10);

    const removal = store.removeRow(first!.id);

    store.search('helios');
    flush();
    expectSearch().flush(
      conversationPage([conversationFixture({ name: 'Helios 2.4' })], {
        totalCount: 1,
      }),
    );
    flush();

    store.restoreRow(removal!);

    expect(store.entities().map((c) => c.name)).toEqual(['Helios 2.4']);
    expect(store.totalCount()).toBe(1);
    expect(store.skip()).toBe(1);
  });

  it('clamps a restore index that no longer exists', () => {
    const [a, b] = load([conversationFixture(), conversationFixture()], 2);

    const removal = store.removeRow(b!.id);
    store.removeRow(a!.id);

    store.restoreRow(removal!);

    expect(store.entities().map((x) => x.id)).toEqual([b!.id]);
  });

  it('keeps detail-only fields out of the list entities', () => {
    // ConversationStore hands this store a ConversationDetailDto; letting mcpServerIds
    // through would leave entities() non-uniformly shaped.
    const [first] = load();

    store.refreshRow({ ...first!, name: 'Renamed', mcpServerIds: ['x'] } as ConversationDto);

    expect(store.entities()[0]).not.toHaveProperty('mcpServerIds');
    expect(store.entities()[0]?.name).toBe('Renamed');
  });

  it('empties itself on sign-out and abandons the request in flight', () => {
    load();

    store.search('helios');
    flush();
    const request = expectSearch();

    signOut();

    expect(request.cancelled).toBe(true);
    expect(store.entities()).toHaveLength(0);
    expect(store.name()).toBe('');
    expect(store.isIdle()).toBe(true);
  });

  it('reloads for the next user after a sign-out', () => {
    load();
    signOut();
    flush();

    store.ensureLoaded();
    flush();
    expectSearch().flush(conversationPage([conversationFixture({ name: 'Theirs' })]));
    flush();

    expect(store.entities().map((c) => c.name)).toEqual(['Theirs']);
  });
});
