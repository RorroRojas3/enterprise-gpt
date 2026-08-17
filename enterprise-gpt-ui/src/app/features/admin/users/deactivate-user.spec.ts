import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { UserDto } from '@domain/api/user';
import { ToastStore } from '@core/notifications/toast-store';
import { SessionStore } from '@core/session/session-store';
import { SELF_DEACTIVATE_MESSAGE } from '@core/users/self-action-messages';
import { UserActionsStore } from '@core/users/user-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { directoryUserFixture, userPage } from '@testing/users';
import { AdminUsers } from './admin-users';
import { DEFAULT_USER_PAGE_SIZE } from './admin-users-route';

const USERS_URL = `${TEST_API_BASE_URL}/api/users`;

describe('Deactivate a user (US-1204)', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;
  let sessionUser: ReturnType<typeof signal<UserDto | null>>;

  beforeEach(async () => {
    installDialogPolyfill();
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
    TestBed.inject(Router).setUpLocationChangeListener();
  });

  afterEach(() => {
    backend.verify();
  });

  function element(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function rows(): HTMLElement[] {
    return [...element().querySelectorAll<HTMLElement>('tbody tr')];
  }

  async function open(users: UserDto[], totalCount = users.length): Promise<void> {
    await harness.navigateByUrl('/admin/users', AdminUsers);
    backend
      .expectOne((request) => request.url === USERS_URL)
      .flush(userPage(users, { totalCount, pageSize: DEFAULT_USER_PAGE_SIZE }));
    await harness.fixture.whenStable();
  }

  /** Presses Deactivate on a row and confirms the modal. */
  async function deactivate(index: number): Promise<TestRequest> {
    rows()[index]!.querySelector<HTMLButtonElement>('.admin-users__deactivate')!.click();
    await harness.fixture.whenStable();

    const confirm = [...element().querySelectorAll<HTMLButtonElement>('.btn-danger')].at(-1);
    confirm!.click();
    await harness.fixture.whenStable();

    return backend.expectOne(
      (request) => request.method === 'DELETE' && request.url.startsWith(`${USERS_URL}/`),
    );
  }

  it('names the user and styles the destructive action red', async () => {
    const user = directoryUserFixture({ fullName: 'Grace Hopper' });
    await open([user]);

    rows()[0]!.querySelector<HTMLButtonElement>('.admin-users__deactivate')!.click();
    await harness.fixture.whenStable();

    const dialog = element().querySelector('app-deactivate-user-dialog');
    expect(dialog?.textContent).toContain('Grace Hopper');
    expect(dialog?.querySelector('.btn-danger')?.textContent?.trim()).toBe('Deactivate');
    // Enter on a fresh destructive confirmation must revoke nobody's access.
    expect(dialog?.querySelector('[data-modal-autofocus]')?.textContent?.trim()).toBe('Cancel');
  });

  it('removes the row on the 204 and raises a success toast', async () => {
    const success = vi.spyOn(TestBed.inject(ToastStore), 'success');
    const user = directoryUserFixture({ fullName: 'Grace Hopper' });
    await open([user, directoryUserFixture()], 2);

    const request = await deactivate(0);
    expect(request.request.url).toBe(`${USERS_URL}/${user.id}`);

    request.flush(null, { status: 204, statusText: 'No Content' });
    await harness.fixture.whenStable();

    expect(rows()).toHaveLength(1);
    expect(success).toHaveBeenCalledWith('Grace Hopper deactivated');
  });

  it('puts the row back where it was when the request fails', async () => {
    const first = directoryUserFixture({ fullName: 'A One' });
    const second = directoryUserFixture({ fullName: 'B Two' });
    const third = directoryUserFixture({ fullName: 'C Three' });
    await open([first, second, third], 3);

    const request = await deactivate(1);
    expect(rows()).toHaveLength(2);

    request.flush(PROBLEM_FIXTURES.validationError, {
      status: 500,
      statusText: 'Internal Server Error',
    });
    await harness.fixture.whenStable();

    expect(rows()).toHaveLength(3);
    // Position-faithful: the restored row goes back between its neighbours, not at the end.
    expect(rows()[1]?.textContent).toContain('B Two');
  });

  it('renders the self-deactivation refusal at the control, never as a toast', async () => {
    const toast = vi.spyOn(TestBed.inject(ToastStore), 'fromError');
    const me = directoryUserFixture({ fullName: 'Sofia Herrera' });
    sessionUser.set(me);
    await open([me]);

    const request = await deactivate(0);
    request.flush(
      {
        ...PROBLEM_FIXTURES.validationError,
        errors: { Id: ['Administrators cannot deactivate their own account.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await harness.fixture.whenStable();

    // Restored, and carrying the board's wording rather than the server's.
    expect(rows()).toHaveLength(1);
    const refusal = element().querySelector('.admin-users__refusal');
    expect(refusal?.textContent).toContain(SELF_DEACTIVATE_MESSAGE);
    expect(element().textContent).not.toContain('Administrators cannot deactivate');
    expect(toast).not.toHaveBeenCalled();

    // Attached to the control it refused, whichever way the reader meets it.
    const button = rows()[0]!.querySelector('.admin-users__deactivate');
    expect(button?.getAttribute('aria-describedby')).toBe(refusal?.id);
  });

  it('clears a standing refusal when the row is tried again', async () => {
    const me = directoryUserFixture();
    sessionUser.set(me);
    await open([me]);

    const request = await deactivate(0);
    request.flush(
      {
        ...PROBLEM_FIXTURES.validationError,
        errors: { Id: ['Administrators cannot deactivate their own account.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await harness.fixture.whenStable();
    expect(element().querySelector('.admin-users__refusal')).not.toBeNull();

    rows()[0]!.querySelector<HTMLButtonElement>('.admin-users__deactivate')!.click();
    await harness.fixture.whenStable();

    expect(element().querySelector('.admin-users__refusal')).toBeNull();
    TestBed.inject(UserActionsStore).cancelDeactivate();
    await harness.fixture.whenStable();
  });

  it('moves focus to the main landmark when the invoking row is destroyed', async () => {
    // The skip link's target, and the one always-present programmatic focus destination.
    const landmark = document.createElement('div');
    landmark.id = 'main-content';
    landmark.tabIndex = -1;
    document.body.appendChild(landmark);

    try {
      await open([directoryUserFixture()]);
      const request = await deactivate(0);
      request.flush(null, { status: 204, statusText: 'No Content' });
      await harness.fixture.whenStable();

      // The row the confirmation was invoked from is gone, so the modal's own focus
      // return has nothing to restore to and focus would otherwise fall to `<body>`.
      expect(document.activeElement).toBe(landmark);
    } finally {
      landmark.remove();
    }
  });

  it('toasts a failure that is not the self-deactivation refusal', async () => {
    const toast = vi.spyOn(TestBed.inject(ToastStore), 'fromError');
    await open([directoryUserFixture()]);

    const request = await deactivate(0);
    request.flush(PROBLEM_FIXTURES.validationError, { status: 500, statusText: 'Server Error' });
    await harness.fixture.whenStable();

    expect(toast).toHaveBeenCalled();
    expect(element().querySelector('.admin-users__refusal')).toBeNull();
  });
});
