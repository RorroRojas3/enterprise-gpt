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
import { adminEvents } from '@core/events/admin-events';
import { sessionEvents } from '@core/events/session-events';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { ADMINISTRATOR_GRANT } from '@testing/session';
import { directoryUserFixture, userPage } from '@testing/users';
import { AdminUsersStore } from './admin-users-store';
import { DEFAULT_USER_PAGE_SIZE } from './admin-users-route';

const USERS_URL = `${TEST_API_BASE_URL}/api/users`;

/** A permission id as the catalog would hand one to the filter. */
const PERMISSION_ID = '9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d';

describe('AdminUsersStore (US-1201)', () => {
  let backend: HttpTestingController;
  let store: InstanceType<typeof AdminUsersStore>;
  let term: ReturnType<typeof signal<string>>;
  let page: ReturnType<typeof signal<number>>;
  let pageSize: ReturnType<typeof signal<number>>;
  let permissionId: ReturnType<typeof signal<string | null>>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        AdminUsersStore,
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(AdminUsersStore);
    term = signal('');
    page = signal(1);
    pageSize = signal(DEFAULT_USER_PAGE_SIZE);
    permissionId = signal<string | null>(null);
  });

  afterEach(() => {
    backend.verify();
  });

  /** Wires the query the way the screen's constructor does — one computation, four inputs. */
  function bind(): void {
    TestBed.runInInjectionContext(() =>
      store.bindQuery(() => ({
        name: term(),
        page: page(),
        pageSize: pageSize(),
        permissionId: permissionId(),
      })),
    );
    TestBed.tick();
  }

  function expectSearch(): TestRequest {
    return backend.expectOne((request) => request.url === USERS_URL);
  }

  async function settle(): Promise<void> {
    TestBed.tick();
    await Promise.resolve();
  }

  it('asks for the first page and omits a blank name', async () => {
    bind();

    const request = expectSearch();
    expect(request.request.params.get('skip')).toBe('0');
    expect(request.request.params.get('take')).toBe(String(DEFAULT_USER_PAGE_SIZE));
    expect(request.request.params.has('name')).toBe(false);

    request.flush(
      userPage([directoryUserFixture()], { totalCount: 1, pageSize: DEFAULT_USER_PAGE_SIZE }),
    );
    await settle();

    expect(store.entities()).toHaveLength(1);
    expect(store.isFulfilled()).toBe(true);
  });

  it('sends permissionId only when the filter is on (US-1206)', async () => {
    bind();

    const unfiltered = expectSearch();
    expect(unfiltered.request.params.has('permissionId')).toBe(false);
    unfiltered.flush(
      userPage([directoryUserFixture()], { totalCount: 312, pageSize: DEFAULT_USER_PAGE_SIZE }),
    );
    await settle();

    permissionId.set(PERMISSION_ID);
    await settle();

    const filtered = expectSearch();
    // The API's own spelling on the wire, whatever the URL calls it.
    expect(filtered.request.params.get('permissionId')).toBe(PERMISSION_ID);
    filtered.flush(
      userPage([directoryUserFixture()], { totalCount: 4, pageSize: DEFAULT_USER_PAGE_SIZE }),
    );
    await settle();

    // The pager reads the filtered total, because the server applied the filter before
    // counting — the whole point of US-1205 over a client-side narrowing.
    expect(store.totalCount()).toBe(4);
    expect(store.countSummary()).toBe('Showing 1–4 of 4 users');
  });

  it('keeps the permission filter across a retry (US-1206)', async () => {
    permissionId.set(PERMISSION_ID);
    bind();
    expectSearch().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 500,
      statusText: 'Server Error',
    });
    await settle();

    store.retry();
    await settle();

    // Read back from state rather than from the computation, which a retry never re-runs.
    const retried = expectSearch();
    expect(retried.request.params.get('permissionId')).toBe(PERMISSION_ID);
    retried.flush(
      userPage([directoryUserFixture()], { totalCount: 1, pageSize: DEFAULT_USER_PAGE_SIZE }),
    );
    await settle();

    expect(store.error()).toBeNull();
  });

  it('withholds the request while the query is unresolved, then sends it (US-1206)', async () => {
    const ready = signal(false);
    TestBed.runInInjectionContext(() =>
      store.bindQuery(() =>
        ready()
          ? { name: '', page: 1, pageSize: DEFAULT_USER_PAGE_SIZE, permissionId: PERMISSION_ID }
          : null,
      ),
    );
    TestBed.tick();

    // Not "sent unfiltered and corrected afterwards": a link naming a permission would
    // otherwise put the whole directory on screen for a paint before narrowing it.
    backend.expectNone((request) => request.url === USERS_URL);
    expect(store.isIdle()).toBe(true);

    ready.set(true);
    await settle();

    const request = expectSearch();
    expect(request.request.params.get('permissionId')).toBe(PERMISSION_ID);
    request.flush(
      userPage([directoryUserFixture()], { totalCount: 1, pageSize: DEFAULT_USER_PAGE_SIZE }),
    );
    await settle();

    expect(store.isFulfilled()).toBe(true);
  });

  it('blames the permission, not an empty search box, when nothing holds it (US-1206)', async () => {
    permissionId.set(PERMISSION_ID);
    bind();
    expectSearch().flush(userPage([], { totalCount: 0, pageSize: DEFAULT_USER_PAGE_SIZE }));
    await settle();

    expect(store.isEmpty()).toBe(true);
    expect(store.hasNoMatches()).toBe(true);
  });

  it('turns a page number into the offset the endpoint takes', async () => {
    page.set(3);
    bind();

    const request = expectSearch();
    expect(request.request.params.get('skip')).toBe(String(2 * DEFAULT_USER_PAGE_SIZE));
    expect(request.request.params.get('take')).toBe(String(DEFAULT_USER_PAGE_SIZE));

    request.flush(
      userPage([directoryUserFixture()], { totalCount: 312, pageSize: DEFAULT_USER_PAGE_SIZE }),
    );
    await settle();

    expect(store.page()).toBe(3);
    expect(store.totalPages()).toBe(13);
    expect(store.countSummary()).toBe('Showing 51–75 of 312 users');
  });

  it('replaces the rows on a page change rather than appending to them', async () => {
    bind();
    expectSearch().flush(
      userPage([directoryUserFixture(), directoryUserFixture()], {
        totalCount: 4,
        pageSize: DEFAULT_USER_PAGE_SIZE,
      }),
    );
    await settle();

    page.set(2);
    await settle();
    expectSearch().flush(
      userPage([directoryUserFixture()], { totalCount: 4, pageSize: DEFAULT_USER_PAGE_SIZE }),
    );
    await settle();

    expect(store.entities()).toHaveLength(1);
  });

  it('sends the term and the page size together, in one request', async () => {
    term.set('  hopper  ');
    pageSize.set(50);
    bind();

    const request = expectSearch();
    expect(request.request.params.get('name')).toBe('hopper');
    expect(request.request.params.get('take')).toBe('50');

    request.flush(userPage([], { totalCount: 0, pageSize: 50 }));
    await settle();

    expect(store.hasNoMatches()).toBe(true);
  });

  it('supersedes an in-flight query rather than racing it', async () => {
    bind();
    const first = expectSearch();

    term.set('grace');
    await settle();
    const second = expectSearch();

    // `switchMap`: the slow first request must not paint over the fast second one.
    expect(first.cancelled).toBe(true);

    second.flush(
      userPage([directoryUserFixture()], { totalCount: 1, pageSize: DEFAULT_USER_PAGE_SIZE }),
    );
    await settle();

    expect(store.name()).toBe('grace');
    expect(store.entities()).toHaveLength(1);
  });

  it('separates an empty page past the end from an empty directory', async () => {
    page.set(99);
    bind();
    expectSearch().flush(userPage([], { totalCount: 312, pageSize: DEFAULT_USER_PAGE_SIZE }));
    await settle();

    expect(store.isPastEnd()).toBe(true);
    expect(store.hasNoMatches()).toBe(false);
  });

  it('reports a failure as an error rather than as an empty directory', async () => {
    bind();
    expectSearch().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 500,
      statusText: 'Internal Server Error',
    });
    await settle();

    expect(store.error()).not.toBeNull();
    expect(store.isEmpty()).toBe(false);
  });

  it('retries the query the rows on screen belong to', async () => {
    term.set('hopper');
    page.set(2);
    bind();
    expectSearch().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 500,
      statusText: 'Internal Server Error',
    });
    await settle();

    store.retry();
    await settle();

    const request = expectSearch();
    expect(request.request.params.get('name')).toBe('hopper');
    expect(request.request.params.get('skip')).toBe(String(DEFAULT_USER_PAGE_SIZE));
    request.flush(
      userPage([directoryUserFixture()], { totalCount: 30, pageSize: DEFAULT_USER_PAGE_SIZE }),
    );
    await settle();

    expect(store.error()).toBeNull();
  });

  it('re-reads the page when a user is created rather than inserting the row', async () => {
    bind();
    expectSearch().flush(
      userPage([directoryUserFixture()], { totalCount: 1, pageSize: DEFAULT_USER_PAGE_SIZE }),
    );
    await settle();

    // The order is last name then first name, so where the new row belongs is the
    // server's decision — and on any other page it must not appear at all.
    TestBed.inject(Dispatcher).dispatch(adminEvents.userCreated(directoryUserFixture()));
    await settle();

    expectSearch().flush(
      userPage([directoryUserFixture(), directoryUserFixture()], {
        totalCount: 2,
        pageSize: DEFAULT_USER_PAGE_SIZE,
      }),
    );
    await settle();

    expect(store.entities()).toHaveLength(2);
  });

  it('patches an updated row it holds, and ignores one it does not', async () => {
    const row = directoryUserFixture({ permissions: [] });
    bind();
    expectSearch().flush(userPage([row], { totalCount: 1, pageSize: DEFAULT_USER_PAGE_SIZE }));
    await settle();

    const dispatcher = TestBed.inject(Dispatcher);
    dispatcher.dispatch(adminEvents.userUpdated({ ...row, permissions: [ADMINISTRATOR_GRANT] }));
    await settle();
    expect(store.entityMap()[row.id]?.permissions).toHaveLength(1);

    // A row on another page must not be spliced into this one.
    dispatcher.dispatch(adminEvents.userUpdated(directoryUserFixture()));
    await settle();
    expect(store.entities()).toHaveLength(1);
  });

  it('drops a row deactivated elsewhere, and is idempotent for one already gone', async () => {
    const row = directoryUserFixture();
    bind();
    expectSearch().flush(
      userPage([row, directoryUserFixture()], { totalCount: 2, pageSize: DEFAULT_USER_PAGE_SIZE }),
    );
    await settle();

    const dispatcher = TestBed.inject(Dispatcher);
    dispatcher.dispatch(adminEvents.userDeactivated(row.id));
    await settle();
    expect(store.entities()).toHaveLength(1);
    expect(store.totalCount()).toBe(1);

    // The screen's own flow removes the row before the event fires, so this arm has to be
    // a no-op rather than decrementing the total a second time.
    dispatcher.dispatch(adminEvents.userDeactivated(row.id));
    await settle();
    expect(store.totalCount()).toBe(1);
  });

  it('refuses to restore a row into a result set the reader has replaced', async () => {
    const row = directoryUserFixture();
    bind();
    expectSearch().flush(
      userPage([row, directoryUserFixture()], { totalCount: 2, pageSize: DEFAULT_USER_PAGE_SIZE }),
    );
    await settle();

    const removed = store.takeRow(row.id);
    expect(store.entities()).toHaveLength(1);

    // A new query lands before the failed request rolls back.
    term.set('somebody else');
    await settle();
    expectSearch().flush(
      userPage([directoryUserFixture()], { totalCount: 1, pageSize: DEFAULT_USER_PAGE_SIZE }),
    );
    await settle();

    store.restoreRow(removed);
    await settle();

    // Splicing one search's row into another's is exactly what the generation prevents.
    expect(store.entities()).toHaveLength(1);
    expect(store.entityMap()[row.id]).toBeUndefined();
  });

  it('puts a row back at the index it came from', async () => {
    const rows = [directoryUserFixture(), directoryUserFixture(), directoryUserFixture()];
    bind();
    expectSearch().flush(userPage(rows, { totalCount: 3, pageSize: DEFAULT_USER_PAGE_SIZE }));
    await settle();

    const removed = store.takeRow(rows[1]!.id);
    expect(store.totalCount()).toBe(2);

    store.restoreRow(removed);

    expect(store.entities().map((user) => user.id)).toEqual(rows.map((user) => user.id));
    expect(store.totalCount()).toBe(3);
  });

  it('cancels an in-flight page on sign-out, and empties itself', async () => {
    bind();
    const request = expectSearch();

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    await settle();

    // `withResetOnSignOut` clears state; `takeUntil` is what stops the request landing
    // in the cleared store a moment later.
    expect(request.cancelled).toBe(true);
    expect(store.entities()).toHaveLength(0);
    expect(store.totalCount()).toBe(0);
  });
});
