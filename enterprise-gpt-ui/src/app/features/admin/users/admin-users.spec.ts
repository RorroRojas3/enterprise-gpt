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
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { FRAMEWORK_PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { ADMINISTRATOR_GRANT, MCP_GRANT, administratorFixture } from '@testing/session';
import { directoryUserFixture, userPage } from '@testing/users';
import { AdminUsers } from './admin-users';
import { DEFAULT_USER_PAGE_SIZE, toPage, toPageSize } from './admin-users-route';

const USERS_URL = `${TEST_API_BASE_URL}/api/users`;

/**
 * `SearchInput`'s own 300 ms window, with room for the effect that follows it and for a
 * loaded CI runner — these are real timers, because faking the clock also stalls the
 * scheduler `whenStable()` awaits.
 */
const DEBOUNCE_MS = 450;

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

  async function open(
    url = '/admin/users',
    items = [directoryUserFixture()],
    options: { totalCount?: number; pageSize?: number; skip?: number } = {},
  ): Promise<void> {
    await harness.navigateByUrl(url, AdminUsers);
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

  it('offers no sort control, no selection column and no permission filter', async () => {
    await open('/admin/users', [directoryUserFixture()], { totalCount: 312 });

    // A follow-up on this tab, US-1206 and the absent bulk endpoint respectively. Pinned
    // so a later story has to decide to add one rather than adding it back from the board.
    const headers = [...element().querySelectorAll('th')].map((cell) => cell.textContent?.trim());
    expect(headers).toEqual(['User', 'Email', 'Permissions', 'Actions']);
    expect(element().querySelectorAll('input[type="checkbox"]')).toHaveLength(0);
    expect(element().querySelectorAll('select')).toHaveLength(1);
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
    expectSearch().flush(userPage([], { totalCount: 0 }));
    await harness.fixture.whenStable();

    expect(element().textContent).toContain('No users match “zzz”');

    element().querySelector<HTMLButtonElement>('[emptyStateAction]')?.click();
    await harness.fixture.whenStable();
    expectSearch().flush(userPage([directoryUserFixture()]));
    await harness.fixture.whenStable();

    expect(url()).toBe('/admin/users');
  });

  it('shows the missing permission rather than an empty table on a 403', async () => {
    await harness.navigateByUrl('/admin/users', AdminUsers);
    expectSearch().flush(FRAMEWORK_PROBLEM_FIXTURES.serverError, {
      status: 403,
      statusText: 'Forbidden',
    });
    await harness.fixture.whenStable();

    expect(element().querySelector('app-error-panel')).not.toBeNull();
    expect(element().querySelector('app-data-table')).toBeNull();
  });
});
