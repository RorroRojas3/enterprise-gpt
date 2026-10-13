import { Location } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { getAllByText, getByRole, getByText } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { UserDto } from '@shared/models/api/user.model';
import {
  DEFAULT_USER_PAGE_SIZE,
  toPage,
  toPageSize,
  toPermissionFilter,
} from '@shared/utils/navigation/admin-users-route';
import { SessionStore } from '@core/session/session-store';
import { UserActionsStore } from '@state/user-actions-store';
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

const USERS_URL = `${TEST_API_BASE_URL}/api/users`;

/**
 * `SearchInput`'s 300 ms debounce plus headroom for a loaded CI runner. Real timers, because a
 * fake clock also stalls the scheduler `whenStable()` awaits.
 */
const DEBOUNCE_MS = 450;

/** The catalog the permission filter renders its options from. */
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

  it('normalizes a permission id and reads an empty one as no filter', () => {
    // Shape only: whether the id exists is the catalog's answer, unlike `?size=`.
    expect(toPermissionFilter('9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d')).toBe(
      '9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d',
    );
    expect(toPermissionFilter('  spaced  ')).toBe('spaced');
    expect(toPermissionFilter('   ')).toBeNull();
    expect(toPermissionFilter('')).toBeNull();
    expect(toPermissionFilter(undefined)).toBeNull();
  });
});

describe('AdminUsers', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;
  let sessionUser: ReturnType<typeof signal<UserDto | null>>;

  beforeEach(async () => {
    // Stubbed: this screen only reads who is signed in.
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

  /** By name, not document order: the paginator's page-size select is also on the page. */
  function permissionSelect(): HTMLSelectElement {
    return getByRole(element(), 'combobox', { name: 'Filter users by permission' });
  }

  function pageSizeSelect(): HTMLSelectElement {
    return getByRole(element(), 'combobox', { name: /rows per page/i });
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
    // The screen fetches the permission catalog on mount; flushed so `verify()` stays clean.
    flushPermissions(backend, options.permissions ?? CATALOG);
    // A `?permission=` link withholds the directory request until the catalog answers.
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
    const field = getByRole<HTMLInputElement>(element(), 'searchbox', {
      name: 'Search users by name or email',
    });
    field.value = term;
    field.dispatchEvent(new Event('input'));
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
    expect(getByText(element(), 'grace.hopper@example.com')).toBeTruthy();
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

    expect(getAllByText(element(), '(you)')).toHaveLength(1);
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

    getByRole(element(), 'button', { name: 'Page 2' }).click();
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

    // Options come from `@for`, so a `[value]` on the select would leave the first one showing.
    expect(pageSizeSelect().value).toBe('100');
  });

  it('returns to the first page when the page size changes', async () => {
    await open('/admin/users?page=3', [directoryUserFixture()], { totalCount: 312, skip: 50 });

    const select = pageSizeSelect();
    select.value = '100';
    select.dispatchEvent(new Event('change'));
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

    // Pinned so adding either is deliberate: no sort control yet, and no bulk endpoint.
    const headers = [...element().querySelectorAll('th')].map((cell) => cell.textContent?.trim());
    expect(headers).toEqual(['User', 'Email', 'Permissions', 'Actions']);
    // Scoped to the table: the edit panel's permission checklist has checkboxes of its own.
    expect(element().querySelectorAll('app-data-table input[type="checkbox"]')).toHaveLength(0);

    // The paginator's page size and the permission filter; a third would be a sort control.
    expect(element().querySelectorAll('select')).toHaveLength(2);

    // The filter narrows the whole directory, so a "loaded rows only" caption would be false.
    expect(element().textContent).not.toContain('Filters loaded rows only');
    expect(element().textContent).not.toContain('Last active');
  });

  it('states the order it cannot change', async () => {
    await open();

    expect(getByText(element(), 'By last name')).toBeTruthy();
    expect(getByRole(element(), 'button', { name: 'About this list' })).toBeTruthy();
  });

  it('announces the count once — the table’s region, not the paginator’s', async () => {
    await open('/admin/users', [directoryUserFixture()], { totalCount: 312 });

    const live = [...element().querySelectorAll('[aria-live]')];
    expect(live).toHaveLength(1);
    expect(live[0]?.textContent).toContain('Showing 1–25 of 312 users');
    // The paginator states the same sentence visibly, outside any live region.
    const visible = getAllByText(element(), 'Showing 1–25 of 312 users').filter(
      (node) => !node.hasAttribute('aria-live'),
    );
    expect(visible).toHaveLength(1);
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
    // Both empty states qualify, so the template's test order decides which one shows.
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

  it('renders the filter from the catalog, with “Permission: any” first', async () => {
    await open();

    const select = permissionSelect();
    const options = [...select.options].map((option) => option.textContent?.trim());

    // `UserActionsStore.checklist` order, so the filter matches the edit panel's checklist.
    expect(options).toEqual(['Permission: any', 'Administrator', 'Upload files']);
    expect(select.value).toBe('');

    // The filter narrows the whole directory, so this caption would be false.
    expect(element().textContent).not.toContain('Filters loaded rows only');
  });

  it('puts the chosen permission in the URL, drops the page, and filters', async () => {
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

  it('sends a deep link’s permission on one request and selects it', async () => {
    await harness.navigateByUrl(`/admin/users?permission=${UPLOAD_PERMISSION.id}`, AdminUsers);

    // Withheld until the catalog answers, then exactly one request carrying the filter.
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

  it('reads a permission the catalog does not list as “any”, and repairs the URL', async () => {
    await harness.navigateByUrl(
      '/admin/users?permission=00000000-0000-4000-8000-000000000000',
      AdminUsers,
    );
    expect(flushPermissions(backend, CATALOG)).toBe(1);
    await harness.fixture.whenStable();

    // A stale or hand-edited id is dropped, as `?size=` is, rather than sent to be refused.
    const request = expectSearch();
    expect(request.request.params.has('permissionId')).toBe(false);
    request.flush(userPage([directoryUserFixture()], { totalCount: 312 }));
    await harness.fixture.whenStable();

    expect(permissionSelect().value).toBe('');
    expect(url()).toBe('/admin/users');
    expect(element().querySelector('app-error-panel')).toBeNull();
  });

  it('names the permission that found nothing, and clears both filters', async () => {
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

  it('names the permission alone when no term is in play', async () => {
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

  it('keeps a filter the catalog cannot name, and still lets it be cleared', async () => {
    await harness.navigateByUrl(`/admin/users?permission=${UPLOAD_PERMISSION.id}`, AdminUsers);

    // With the catalog failed nothing can vouch for the id, so the server decides.
    backend
      .expectOne((request) => request.url.endsWith('/api/permissions'))
      .flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, { status: 500, statusText: 'Server Error' });
    await harness.fixture.whenStable();

    const request = expectSearch();
    expect(request.request.params.get('permissionId')).toBe(UPLOAD_PERMISSION.id);
    request.flush(userPage([directoryUserFixture()], { totalCount: 1 }));
    await harness.fixture.whenStable();

    // The select must not claim "any" over a narrowed request, and must offer an option to
    // move off, since re-selecting the selected option fires no change event.
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

  it('names the filter it cannot resolve rather than quoting an empty term', async () => {
    await harness.navigateByUrl(`/admin/users?permission=${UPLOAD_PERMISSION.id}`, AdminUsers);
    backend
      .expectOne((request) => request.url.endsWith('/api/permissions'))
      .flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, { status: 500, statusText: 'Server Error' });
    await harness.fixture.whenStable();
    expectSearch().flush(userPage([], { totalCount: 0 }));
    await harness.fixture.whenStable();

    // Filtered but unnameable: branching on the name, not the filter, would render
    // `No users match “”`.
    expect(element().textContent).toContain('No users hold this permission');
    expect(element().textContent).not.toContain('No users match “”');
    expect(
      element().querySelector<HTMLButtonElement>('[emptyStateAction]')?.textContent?.trim(),
    ).toBe('Clear filter');
  });

  it('keeps the filter while a failed catalog is being retried', async () => {
    await harness.navigateByUrl(`/admin/users?permission=${UPLOAD_PERMISSION.id}`, AdminUsers);
    backend
      .expectOne((request) => request.url.endsWith('/api/permissions'))
      .flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, { status: 500, statusText: 'Server Error' });
    await harness.fixture.whenStable();
    expectSearch().flush(userPage([directoryUserFixture()], { totalCount: 1 }));
    await harness.fixture.whenStable();

    // Retrying clears the error early; reading that window as settled would let the repair
    // effect drop a live filter before the retry could vouch for it.
    TestBed.inject(UserActionsStore).retryPermissions();
    await harness.fixture.whenStable();

    expect(url()).toBe(`/admin/users?permission=${UPLOAD_PERMISSION.id}`);
    backend.expectNone((request) => request.url === USERS_URL);

    // The control must not flip back to "any" while the rows behind it are still filtered.
    expect(permissionSelect().value).toBe(UPLOAD_PERMISSION.id);

    flushPermissions(backend, CATALOG);
    await harness.fixture.whenStable();

    // Withheld during the retry, the query re-fires with the same filter once it answers.
    const resumed = expectSearch();
    expect(resumed.request.params.get('permissionId')).toBe(UPLOAD_PERMISSION.id);
    resumed.flush(userPage([directoryUserFixture()], { totalCount: 1 }));
    await harness.fixture.whenStable();

    // Now it can be named, so the extra option gives way to the catalog's own.
    expect(permissionSelect().value).toBe(UPLOAD_PERMISSION.id);
    expect(permissionSelect().options).toHaveLength(3);
    expect(url()).toBe(`/admin/users?permission=${UPLOAD_PERMISSION.id}`);
  });

  it('treats an empty catalog as an answer rather than a load still pending', async () => {
    await harness.navigateByUrl(`/admin/users?permission=${UPLOAD_PERMISSION.id}`, AdminUsers);

    // Reading "loaded" as `permissions.length > 0` would withhold the request forever.
    flushPermissions(backend, []);
    await harness.fixture.whenStable();

    const request = expectSearch();
    expect(request.request.params.has('permissionId')).toBe(false);
    request.flush(userPage([directoryUserFixture()], { totalCount: 312 }));
    await harness.fixture.whenStable();

    expect(url()).toBe('/admin/users');
    expect(element().querySelector('app-data-table')).not.toBeNull();
  });

  it('pushes turning the filter on, and replaces swapping one for another', async () => {
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

    // Swapping replaced the entry (a closed select commits per arrow key) while turning the
    // filter on pushed one, so one Back reaches the unfiltered directory.
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
