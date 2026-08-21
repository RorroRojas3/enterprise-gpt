import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { Location } from '@angular/common';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { UserDto } from '@domain/api/user';
import { SessionStore } from '@core/session/session-store';
import { UserActionsStore } from '@core/users/user-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { ADMINISTRATOR_GRANT, MCP_GRANT, administratorFixture } from '@testing/session';
import {
  directoryUserFixture,
  flushPermissions,
  permissionFixture,
  userPage,
} from '@testing/users';
import { AdminUsers } from './admin-users';
import {
  DEFAULT_USER_PAGE_SIZE,
  toPage,
  toPageSize,
  toPermissionFilter,
} from './admin-users-route';

const USERS_URL = `${TEST_API_BASE_URL}/api/users`;

/**
 * `SearchInput`'s own 300 ms window, with room for the effect that follows it and for a
 * loaded CI runner — these are real timers, because faking the clock also stalls the
 * scheduler `whenStable()` awaits.
 */
const DEBOUNCE_MS = 450;

/** The catalog the permission filter renders its options from (US-1206). */
const UPLOAD_PERMISSION = permissionFixture({
  id: '11111111-aaaa-4bbb-8ccc-dddddddddddd',
  name: 'Upload files',
});
const ADMIN_PERMISSION = permissionFixture({
  id: ADMINISTRATOR_GRANT.id,
  name: ADMINISTRATOR_GRANT.name,
});
const CATALOG = [UPLOAD_PERMISSION, ADMIN_PERMISSION];

describe('admin-users-route', () => {
  it('reads a page number, and refuses one that would send a negative offset', () => {
    expect(toPage('3')).toBe(3);
    expect(toPage('0')).toBe(1);
    expect(toPage('-2')).toBe(1);
    expect(toPage('abc')).toBe(1);
    expect(toPage(undefined)).toBe(1);
  });

  it('reads only the sizes the select offers, so the control cannot contradict the URL', () => {
    expect(toPageSize('50')).toBe(50);
    expect(toPageSize('100')).toBe(100);
    expect(toPageSize('7')).toBe(DEFAULT_USER_PAGE_SIZE);
    expect(toPageSize(undefined)).toBe(DEFAULT_USER_PAGE_SIZE);
  });

  it('normalizes a permission id and reads an empty one as no filter (US-1206)', () => {
    // Shape only. Whether the id names a permission that exists is the catalog's answer,
    // not a transform's — the one way this differs from `?size=`.
    expect(toPermissionFilter('9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d')).toBe(
      '9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d',
    );
    expect(toPermissionFilter('  spaced  ')).toBe('spaced');
    expect(toPermissionFilter('   ')).toBeNull();
    expect(toPermissionFilter('')).toBeNull();
    expect(toPermissionFilter(undefined)).toBeNull();
  });
});

describe('AdminUsers (US-1201)', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;
  let sessionUser: ReturnType<typeof signal<UserDto | null>>;

  beforeEach(async () => {
    // Stubbed rather than loaded: `SessionBootstrap` resolves the session in the
    // application, and this screen only ever reads who is signed in.
    sessionUser = signal<UserDto | null>(null);

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SessionStore, useValue: { user: sessionUser } },
        provideRouter(
          [{ path: 'admin/users', component: AdminUsers }],
          withComponentInputBinding(),
        ),
        provideLocationMocks(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
    // The harness navigates directly and never starts the router's popstate listener, so
    // without this a back press moves `Location` and leaves the router where it was.
    TestBed.inject(Router).setUpLocationChangeListener();
  });

  afterEach(() => {
    backend.verify();
  });

  function element(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function expectSearch(): TestRequest {
    return backend.expectOne((request) => request.url === USERS_URL);
  }

  /**
   * The permission filter, by its own class rather than by document order: the
   * paginator's page-size select is the other one on the page, and an index would be a
   * trap the next control added to the toolbar would spring.
   */
  function permissionSelect(): HTMLSelectElement {
    return element().querySelector<HTMLSelectElement>('.admin-users__permission select')!;
  }

  async function open(
    url = '/admin/users',
    items = [directoryUserFixture()],
    options: {
      totalCount?: number;
      pageSize?: number;
      skip?: number;
      permissions?: typeof CATALOG;
    } = {},
  ): Promise<void> {
    await harness.navigateByUrl(url, AdminUsers);
    // The screen asks for the permission catalog on mount, for the filter's options
    // (US-1206). Flushed here so every test's `verify()` sees no outstanding request.
    flushPermissions(backend, options.permissions ?? CATALOG);
    // Stabilized between the two: a `?permission=` link withholds the directory request
    // until the catalog answers, so it is not yet on the wire when the flush returns.
    await harness.fixture.whenStable();
    expectSearch().flush(
      userPage(items, {
        totalCount: options.totalCount ?? items.length,
        pageSize: options.pageSize ?? DEFAULT_USER_PAGE_SIZE,
        skip: options.skip ?? 0,
      }),
    );
    await harness.fixture.whenStable();
  }

  async function type(term: string): Promise<void> {
    const field = element().querySelector<HTMLInputElement>('.search__field');
    field!.value = term;
    field!.dispatchEvent(new Event('input'));
    await harness.fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, DEBOUNCE_MS));
    await harness.fixture.whenStable();
  }

  async function goBack(): Promise<void> {
    TestBed.inject(Location).back();
    await new Promise((resolve) => setTimeout(resolve, 0));
    await harness.fixture.whenStable();
  }

  function url(): string {
    return TestBed.inject(Router).url;
  }

  it('renders a row with initials, name, email and one badge per grant', async () => {
    await open('/admin/users', [
      directoryUserFixture({
        firstName: 'Grace',
        lastName: 'Hopper',
        fullName: 'Grace Hopper',
        email: 'grace.hopper@example.com',
        permissions: [ADMINISTRATOR_GRANT, MCP_GRANT],
      }),
    ]);

    expect(element().querySelector('.avatar')?.textContent?.trim()).toBe('GH');
    expect(element().querySelector('.admin-users__name')?.textContent).toContain('Grace Hopper');
    expect(element().querySelector('.admin-users__email')?.textContent).toContain(
      'grace.hopper@example.com',
    );
    expect(element().querySelectorAll('app-permission-badge')).toHaveLength(2);
  });

  it('names the MCP server behind a grant it did not invent', async () => {
    await open('/admin/users', [directoryUserFixture({ permissions: [MCP_GRANT] })]);

    const badge = element().querySelector('app-permission-badge');
    expect(badge?.textContent).toContain(`— managed by ${MCP_GRANT.name} MCP server`);
  });

  it('falls back to the email when the directory gave the account no name', async () => {
    await open('/admin/users', [
      directoryUserFixture({ firstName: '', lastName: '', fullName: '', email: 'nn@example.com' }),
    ]);

    expect(element().querySelector('.admin-users__name')?.textContent).toContain('nn@example.com');
    expect(element().querySelector('.avatar')?.textContent?.trim()).toBe('N');
  });

  it('marks the signed-in administrator’s own row', async () => {
    const me = administratorFixture();
    sessionUser.set(me);

    await open('/admin/users', [
      directoryUserFixture({ id: me.id, fullName: 'Ada Lovelace' }),
      directoryUserFixture(),
    ]);

    const marks = element().querySelectorAll('.admin-users__you');
    expect(marks).toHaveLength(1);
    expect(marks[0]?.textContent).toContain('(you)');
  });

  it('sends a deep link’s term, page and size on one request', async () => {
    await harness.navigateByUrl('/admin/users?name=hopper&page=2&size=50', AdminUsers);
    flushPermissions(backend);

    const request = expectSearch();
    expect(request.request.params.get('name')).toBe('hopper');
    expect(request.request.params.get('skip')).toBe('50');
    expect(request.request.params.get('take')).toBe('50');

    request.flush(userPage([directoryUserFixture()], { totalCount: 312, pageSize: 50, skip: 50 }));
    await harness.fixture.whenStable();
  });

  it('puts a search in the URL and drops the page with it', async () => {
    await open('/admin/users?page=3', [directoryUserFixture()], { totalCount: 312, skip: 50 });

    await type('hopper');
    expectSearch().flush(userPage([directoryUserFixture()], { totalCount: 1 }));
    await harness.fixture.whenStable();

    expect(url()).toBe('/admin/users?name=hopper');
  });

  it('pages through the URL, and back restores the page before it', async () => {
    await open('/admin/users', [directoryUserFixture()], { totalCount: 312 });

    element().querySelectorAll<HTMLButtonElement>('.paginator__page')[1]?.click();
    await harness.fixture.whenStable();
    expectSearch().flush(
      userPage([directoryUserFixture()], { totalCount: 312, skip: DEFAULT_USER_PAGE_SIZE }),
    );
    await harness.fixture.whenStable();
    expect(url()).toBe('/admin/users?page=2');

    await goBack();
    expectSearch().flush(userPage([directoryUserFixture()], { totalCount: 312 }));
    await harness.fixture.whenStable();

    expect(url()).toBe('/admin/users');
  });

  it('shows the size a deep link asked for, not the default', async () => {
    await open('/admin/users?size=100', [directoryUserFixture()], {
      totalCount: 312,
      pageSize: 100,
    });

    // The select is populated by `@for`, so a `[value]` binding on the element itself
    // would run against an empty list and leave the first option showing.
    const select = element().querySelector<HTMLSelectElement>('.paginator__size select');
    expect(select!.value).toBe('100');
  });

  it('returns to the first page when the page size changes', async () => {
    await open('/admin/users?page=3', [directoryUserFixture()], { totalCount: 312, skip: 50 });

    const select = element().querySelector<HTMLSelectElement>('.paginator__size select');
    select!.value = '100';
    select!.dispatchEvent(new Event('change'));
    await harness.fixture.whenStable();

    const request = expectSearch();
    expect(request.request.params.get('skip')).toBe('0');
    expect(request.request.params.get('take')).toBe('100');
    request.flush(userPage([directoryUserFixture()], { totalCount: 312, pageSize: 100 }));
    await harness.fixture.whenStable();

    expect(url()).toBe('/admin/users?size=100');
  });

  it('offers no sort control and no selection column, and never the interim filter copy', async () => {
    await open('/admin/users', [directoryUserFixture()], { totalCount: 312 });

    // A follow-up on this tab and the absent bulk endpoint respectively. Pinned so a later
    // story has to decide to add one rather than adding it back from the board.
    const headers = [...element().querySelectorAll('th')].map((cell) => cell.textContent?.trim());
    expect(headers).toEqual(['User', 'Email', 'Permissions', 'Actions']);
    // Scoped to the table: the edit panel's permission checklist renders checkboxes of
    // its own, and they are not a row-selection column.
    expect(element().querySelectorAll('app-data-table input[type="checkbox"]')).toHaveLength(0);

    // Two: the paginator's page size, and US-1206's permission filter. A third would be
    // the sort control this tab has decided not to build yet.
    expect(element().querySelectorAll('select')).toHaveLength(2);

    // Frame `5a` draws this beside the filter, and it describes the interim US-1205
    // retired: the filter narrows the whole directory, so the caption would now be false.
    expect(element().textContent).not.toContain('Filters loaded rows only');
    expect(element().textContent).not.toContain('Last active');
  });

  it('states the order it cannot change', async () => {
    await open();

    expect(element().querySelector('.admin-users__order')?.textContent).toContain('By last name');
    expect(element().querySelector('.admin-users__order-info')).not.toBeNull();
  });

  it('announces the count once — the table’s region, not the paginator’s', async () => {
    await open('/admin/users', [directoryUserFixture()], { totalCount: 312 });

    const live = [...element().querySelectorAll('[aria-live]')];
    expect(live).toHaveLength(1);
    expect(live[0]?.textContent).toContain('Showing 1–25 of 312 users');
    expect(element().querySelector('.paginator__summary')?.hasAttribute('aria-live')).toBe(false);
  });

  it('offers a way out of a page past the end of the directory', async () => {
    await open('/admin/users?page=99', [], { totalCount: 312, skip: 2450 });

    expect(element().textContent).toContain('past the end of the directory');

    element().querySelector<HTMLButtonElement>('[emptyStateAction]')?.click();
    await harness.fixture.whenStable();
    expectSearch().flush(userPage([directoryUserFixture()], { totalCount: 312 }));
    await harness.fixture.whenStable();

    expect(url()).toBe('/admin/users');
  });

  it('blames the page, not the search term, on a filtered link past the end', async () => {
    // Both empty states qualify here — there is a term *and* the page is out of range —
    // so the order the template tests them in is what decides which one the reader gets.
    await open('/admin/users?name=hopper&page=99', [], { totalCount: 2, skip: 2450 });

    expect(element().textContent).toContain('past the end of the directory');
    expect(element().textContent).not.toContain('No users match “hopper”');
    // And the count beside it must not state a range that runs backwards.
    expect(element().textContent).not.toContain('2451');
    expect(element().querySelector('[aria-live]')?.textContent).toContain(
      'No users on this page — 2 users in total',
    );
  });

  it('names the term that found nothing, and clears it', async () => {
    await harness.navigateByUrl('/admin/users?name=zzz', AdminUsers);
    flushPermissions(backend);
    expectSearch().flush(userPage([], { totalCount: 0 }));
    await harness.fixture.whenStable();

    expect(element().textContent).toContain('No users match “zzz”');

    element().querySelector<HTMLButtonElement>('[emptyStateAction]')?.click();
    await harness.fixture.whenStable();
    expectSearch().flush(userPage([directoryUserFixture()]));
    await harness.fixture.whenStable();

    expect(url()).toBe('/admin/users');
  });

  it('renders the filter from the catalog, with “Permission: any” first (US-1206)', async () => {
    await open();

    const select = permissionSelect();
    const options = [...select.options].map((option) => option.textContent?.trim());

    // The catalog's own order — `UserActionsStore.checklist`, which sorts MCP-derived
    // permissions last and the rest by name — so the filter and the edit panel's
    // checklist list the same permissions the same way.
    expect(options).toEqual(['Permission: any', 'Administrator', 'Upload files']);
    expect(select.value).toBe('');

    // The board draws this beside the control and it described the interim US-1205
    // retired: the filter narrows the directory, so it must never render.
    expect(element().textContent).not.toContain('Filters loaded rows only');
  });

  it('puts the chosen permission in the URL, drops the page, and filters (US-1206)', async () => {
    await open('/admin/users?page=3', [directoryUserFixture()], { totalCount: 312, skip: 50 });

    const select = permissionSelect();
    select.value = ADMIN_PERMISSION.id;
    select.dispatchEvent(new Event('change'));
    await harness.fixture.whenStable();

    const request = expectSearch();
    expect(request.request.params.get('permissionId')).toBe(ADMIN_PERMISSION.id);
    // A narrowed directory has fewer pages, so page 3 cannot survive the change.
    expect(request.request.params.get('skip')).toBe('0');
    request.flush(userPage([directoryUserFixture()], { totalCount: 2 }));
    await harness.fixture.whenStable();

    expect(url()).toBe(`/admin/users?permission=${ADMIN_PERMISSION.id}`);

    // Not a refinement: back returns to the directory as it was, rather than skipping it.
    await goBack();
    expectSearch().flush(userPage([directoryUserFixture()], { totalCount: 312, skip: 50 }));
    await harness.fixture.whenStable();

    expect(url()).toBe('/admin/users?page=3');
  });

  it('sends a deep link’s permission on one request and selects it (US-1206)', async () => {
    await harness.navigateByUrl(`/admin/users?permission=${UPLOAD_PERMISSION.id}`, AdminUsers);

    // The request is withheld until the catalog answers, so there is no unfiltered page to
    // paint over — and then exactly one request goes out, carrying the filter.
    backend.expectNone((request) => request.url === USERS_URL);
    expect(flushPermissions(backend, CATALOG)).toBe(1);
    await harness.fixture.whenStable();

    const request = expectSearch();
    expect(request.request.params.get('permissionId')).toBe(UPLOAD_PERMISSION.id);
    request.flush(userPage([directoryUserFixture()], { totalCount: 1 }));
    await harness.fixture.whenStable();

    expect(permissionSelect().value).toBe(UPLOAD_PERMISSION.id);
    expect(url()).toBe(`/admin/users?permission=${UPLOAD_PERMISSION.id}`);
  });

  it('reads a permission the catalog does not list as “any”, and repairs the URL (US-1206)', async () => {
    await harness.navigateByUrl(
      '/admin/users?permission=00000000-0000-4000-8000-000000000000',
      AdminUsers,
    );
    expect(flushPermissions(backend, CATALOG)).toBe(1);
    await harness.fixture.whenStable();

    // Deactivated since the link was shared, or hand-edited. Neither is an error the
    // reader can act on, so the parameter is dropped rather than sent for the server to
    // refuse — the rule `?size=` already follows.
    const request = expectSearch();
    expect(request.request.params.has('permissionId')).toBe(false);
    request.flush(userPage([directoryUserFixture()], { totalCount: 312 }));
    await harness.fixture.whenStable();

    expect(permissionSelect().value).toBe('');
    expect(url()).toBe('/admin/users');
    expect(element().querySelector('app-error-panel')).toBeNull();
  });

  it('names the permission that found nothing, and clears both filters (US-1206)', async () => {
    await harness.navigateByUrl(
      `/admin/users?name=zzz&permission=${ADMIN_PERMISSION.id}`,
      AdminUsers,
    );
    flushPermissions(backend, CATALOG);
    await harness.fixture.whenStable();
    expectSearch().flush(userPage([], { totalCount: 0 }));
    await harness.fixture.whenStable();

    // Blaming the term alone would send the reader to clear a search that was working.
    expect(element().textContent).toContain('No users match “zzz” and hold “Administrator”');

    const action = element().querySelector<HTMLButtonElement>('[emptyStateAction]');
    expect(action?.textContent?.trim()).toBe('Clear filters');

    action?.click();
    await harness.fixture.whenStable();
    expectSearch().flush(userPage([directoryUserFixture()], { totalCount: 312 }));
    await harness.fixture.whenStable();

    // One navigation, so no intermediate half-cleared directory lands in the history.
    expect(url()).toBe('/admin/users');
  });

  it('names the permission alone when no term is in play (US-1206)', async () => {
    await harness.navigateByUrl(`/admin/users?permission=${ADMIN_PERMISSION.id}`, AdminUsers);
    flushPermissions(backend, CATALOG);
    await harness.fixture.whenStable();
    expectSearch().flush(userPage([], { totalCount: 0 }));
    await harness.fixture.whenStable();

    expect(element().textContent).toContain('No users hold “Administrator”');
    expect(element().textContent).toContain('Only users holding this permission are listed.');
    expect(
      element().querySelector<HTMLButtonElement>('[emptyStateAction]')?.textContent?.trim(),
    ).toBe('Clear filter');
  });

  it('keeps a filter the catalog cannot name, and still lets it be cleared (US-1206)', async () => {
    await harness.navigateByUrl(`/admin/users?permission=${UPLOAD_PERMISSION.id}`, AdminUsers);

    // The catalog is the only thing that can vouch for an id, so when it fails nothing
    // can — the filter goes to the server, which is the authority left.
    backend
      .expectOne((request) => request.url.endsWith('/api/permissions'))
      .flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, { status: 500, statusText: 'Server Error' });
    await harness.fixture.whenStable();

    const request = expectSearch();
    expect(request.request.params.get('permissionId')).toBe(UPLOAD_PERMISSION.id);
    request.flush(userPage([directoryUserFixture()], { totalCount: 1 }));
    await harness.fixture.whenStable();

    // The control must not read "Permission: any" while the request underneath it is
    // narrowed, and it must offer somewhere to go: re-selecting an already-selected
    // option fires no change event, which would strand the reader on the filter.
    const select = permissionSelect();
    expect(select.value).toBe(UPLOAD_PERMISSION.id);
    expect(select.options).toHaveLength(2);

    select.value = '';
    select.dispatchEvent(new Event('change'));
    await harness.fixture.whenStable();
    expectSearch().flush(userPage([directoryUserFixture()], { totalCount: 312 }));
    await harness.fixture.whenStable();

    expect(url()).toBe('/admin/users');
  });

  it('names the filter it cannot resolve rather than quoting an empty term (US-1206)', async () => {
    await harness.navigateByUrl(`/admin/users?permission=${UPLOAD_PERMISSION.id}`, AdminUsers);
    backend
      .expectOne((request) => request.url.endsWith('/api/permissions'))
      .flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, { status: 500, statusText: 'Server Error' });
    await harness.fixture.whenStable();
    expectSearch().flush(userPage([], { totalCount: 0 }));
    await harness.fixture.whenStable();

    // Filtered but unnameable. Branching this on the permission's *name* rather than on
    // the filter being on renders `No users match “”` over an empty search box.
    expect(element().textContent).toContain('No users hold this permission');
    expect(element().textContent).not.toContain('No users match “”');
    expect(
      element().querySelector<HTMLButtonElement>('[emptyStateAction]')?.textContent?.trim(),
    ).toBe('Clear filter');
  });

  it('keeps the filter while a failed catalog is being retried (US-1206)', async () => {
    await harness.navigateByUrl(`/admin/users?permission=${UPLOAD_PERMISSION.id}`, AdminUsers);
    backend
      .expectOne((request) => request.url.endsWith('/api/permissions'))
      .flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, { status: 500, statusText: 'Server Error' });
    await harness.fixture.whenStable();
    expectSearch().flush(userPage([directoryUserFixture()], { totalCount: 1 }));
    await harness.fixture.whenStable();

    // Retrying clears the error before any rows arrive. A catalog flag that meant "a load
    // answered once" rather than "this load answered" would read that window as settled —
    // and the repair effect would drop a live filter, with `replaceUrl`, before the retry
    // could vouch for it.
    TestBed.inject(UserActionsStore).retryPermissions();
    await harness.fixture.whenStable();

    expect(url()).toBe(`/admin/users?permission=${UPLOAD_PERMISSION.id}`);
    backend.expectNone((request) => request.url === USERS_URL);

    // The control must not flip back to "any" while the rows behind it are still filtered.
    expect(permissionSelect().value).toBe(UPLOAD_PERMISSION.id);

    flushPermissions(backend, CATALOG);
    await harness.fixture.whenStable();

    // The query was withheld for the length of the retry, so it re-fires when the catalog
    // answers — carrying the same filter it had before. One redundant request on a path
    // that only exists after a failure, in exchange for never showing unfiltered rows.
    const resumed = expectSearch();
    expect(resumed.request.params.get('permissionId')).toBe(UPLOAD_PERMISSION.id);
    resumed.flush(userPage([directoryUserFixture()], { totalCount: 1 }));
    await harness.fixture.whenStable();

    // Now it can be named, so the extra option gives way to the catalog's own.
    expect(permissionSelect().value).toBe(UPLOAD_PERMISSION.id);
    expect(permissionSelect().options).toHaveLength(3);
    expect(url()).toBe(`/admin/users?permission=${UPLOAD_PERMISSION.id}`);
  });

  it('treats an empty catalog as an answer rather than a load still pending (US-1206)', async () => {
    await harness.navigateByUrl(`/admin/users?permission=${UPLOAD_PERMISSION.id}`, AdminUsers);

    // A `200 []` settles the question. Reading "loaded" as `permissions.length > 0` would
    // make this a fixed point: the request is withheld forever, and the reader gets a
    // permanent skeleton with no request, no error and no way out.
    flushPermissions(backend, []);
    await harness.fixture.whenStable();

    const request = expectSearch();
    expect(request.request.params.has('permissionId')).toBe(false);
    request.flush(userPage([directoryUserFixture()], { totalCount: 312 }));
    await harness.fixture.whenStable();

    expect(url()).toBe('/admin/users');
    expect(element().querySelector('app-data-table')).not.toBeNull();
  });

  it('pushes turning the filter on, and replaces swapping one for another (US-1206)', async () => {
    await open();

    async function choose(id: string): Promise<void> {
      const select = permissionSelect();
      select.value = id;
      select.dispatchEvent(new Event('change'));
      await harness.fixture.whenStable();
      expectSearch().flush(userPage([directoryUserFixture()], { totalCount: 2 }));
      await harness.fixture.whenStable();
    }

    await choose(UPLOAD_PERMISSION.id);
    expect(url()).toBe(`/admin/users?permission=${UPLOAD_PERMISSION.id}`);

    await choose(ADMIN_PERMISSION.id);
    expect(url()).toBe(`/admin/users?permission=${ADMIN_PERMISSION.id}`);

    // One Back, not two. A closed select commits on every arrow key, so swapping one
    // permission for another is a refinement and overwrites its entry — otherwise a
    // keyboard reader walking the list would owe one Back per permission passed, where a
    // pointer owes one. Turning the filter *on* is still a transition, so this lands on
    // the unfiltered directory rather than on the permission chosen first.
    await goBack();
    expectSearch().flush(userPage([directoryUserFixture()], { totalCount: 312 }));
    await harness.fixture.whenStable();

    expect(url()).toBe('/admin/users');
  });

  it('shows the missing permission rather than an empty table on a 403', async () => {
    await harness.navigateByUrl('/admin/users', AdminUsers);
    flushPermissions(backend);
    expectSearch().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 403,
      statusText: 'Forbidden',
    });
    await harness.fixture.whenStable();

    expect(element().querySelector('app-error-panel')).not.toBeNull();
    expect(element().querySelector('app-data-table')).toBeNull();
  });
});
