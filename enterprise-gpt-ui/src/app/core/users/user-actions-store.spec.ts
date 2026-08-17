import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { patchState } from '@ngrx/signals';
import { Dispatcher, Events } from '@ngrx/signals/events';
import { unprotected } from '@ngrx/signals/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { UserDto } from '@domain/api/user';
import { adminEvents } from '@core/events/admin-events';
import { sessionEvents } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { SessionStore } from '@core/session/session-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { MCP_GRANT, UPLOAD_FILE_GRANT } from '@testing/session';
import { directoryUserFixture, permissionFixture } from '@testing/users';
import { UserActionsStore } from './user-actions-store';

const USERS_URL = `${TEST_API_BASE_URL}/api/users`;
const PERMISSIONS_URL = `${TEST_API_BASE_URL}/api/permissions`;

describe('UserActionsStore (US-1202)', () => {
  let backend: HttpTestingController;
  let store: InstanceType<typeof UserActionsStore>;
  let sessionUser: ReturnType<typeof signal<UserDto | null>>;

  beforeEach(() => {
    sessionUser = signal<UserDto | null>(null);

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SessionStore, useValue: { user: sessionUser } },
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(UserActionsStore);
  });

  afterEach(() => {
    backend.verify();
  });

  async function settle(): Promise<void> {
    TestBed.tick();
    await Promise.resolve();
  }

  function expectPermissions(): TestRequest {
    return backend.expectOne(PERMISSIONS_URL);
  }

  function expectCreate(): TestRequest {
    return backend.expectOne((request) => request.url === USERS_URL && request.method === 'POST');
  }

  /** Opens the create form and answers its permission load. */
  async function open(permissions = [permissionFixture()]): Promise<void> {
    store.beginCreate();
    await settle();
    expectPermissions().flush(permissions);
    await settle();
  }

  it('loads the grantable permissions once, when a form first opens', async () => {
    await open();
    expect(store.checklist()).toHaveLength(1);

    store.cancelForm();
    store.beginCreate();
    await settle();

    // No second request: `expectNone` is what `backend.verify()` would otherwise let
    // through as an unrelated outstanding call.
    backend.expectNone(PERMISSIONS_URL);
  });

  it('asks the active-permissions route, not the one that also returns deactivated rows', async () => {
    store.beginCreate();
    await settle();

    const request = expectPermissions();
    // `permissions/all` would offer rows the server answers a grant of with a 404.
    expect(request.request.url).toBe(PERMISSIONS_URL);
    request.flush([]);
    await settle();
  });

  it('puts the MCP-managed permissions after the grantable ones', async () => {
    await open([MCP_GRANT, permissionFixture({ name: 'Zeta' }), UPLOAD_FILE_GRANT]);

    expect(store.checklist().map((permission) => permission.name)).toEqual([
      'Upload File',
      'Zeta',
      MCP_GRANT.name,
    ]);
  });

  it('posts the email and the checked permissions, and announces the created user', async () => {
    const dispatched: UserDto[] = [];
    TestBed.inject(Events)
      .on(adminEvents.userCreated)
      .subscribe(({ payload }) => dispatched.push(payload));

    await open();
    store.submitCreate('grace.hopper@example.com', ['abc']);
    await settle();

    const request = expectCreate();
    expect(request.request.body).toEqual({
      email: 'grace.hopper@example.com',
      permissionIds: ['abc'],
    });

    const created = directoryUserFixture({ email: 'grace.hopper@example.com' });
    request.flush(created, { status: 201, statusText: 'Created' });
    await settle();

    expect(dispatched).toEqual([created]);
    expect(store.formMode()).toBeNull();
    expect(store.formBusy()).toBe(false);
  });

  it('refuses a second submit while one is in flight', async () => {
    await open();
    store.submitCreate('a@example.com', []);
    await settle();
    const first = expectCreate();

    store.submitCreate('a@example.com', []);
    await settle();

    // exhaustMap: a double-click is not a second user.
    backend.expectNone((request) => request.method === 'POST' && request !== first.request);
    first.flush(directoryUserFixture(), { status: 201, statusText: 'Created' });
    await settle();
  });

  it('keeps a 400 inline against the email, and raises no toast', async () => {
    const toast = vi.spyOn(TestBed.inject(ToastStore), 'show');

    await open();
    store.submitCreate('taken@example.com', []);
    await settle();

    expectCreate().flush(
      {
        type: '/problems/validation-error',
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: { Email: ['A user with this email address already exists.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();

    expect(store.formMode()).toBe('create');
    expect(store.formRejectedEmail()).toBe('taken@example.com');
    expect(store.formError()?.errors['Email']).toEqual([
      'A user with this email address already exists.',
    ]);
    expect(toast).not.toHaveBeenCalled();
  });

  it('renders the directory 404 against the field rather than as a toast', async () => {
    const toast = vi.spyOn(TestBed.inject(ToastStore), 'fromError');

    await open();
    store.submitCreate('nobody@example.com', []);
    await settle();

    // Not a validation problem: `POST api/users` resolves the address through Microsoft
    // Graph first, so this arrives with no `errors` dictionary at all.
    expectCreate().flush(
      {
        type: '/problems/resource-not-found',
        title: 'Resource not found',
        status: 404,
        detail: 'No directory user found for that email address.',
      },
      { status: 404, statusText: 'Not Found' },
    );
    await settle();

    expect(store.formMode()).toBe('create');
    expect(store.formNotFound()).toBe('No directory user found for that email address.');
    expect(store.formRejectedEmail()).toBe('nobody@example.com');
    expect(toast).not.toHaveBeenCalled();
  });

  it('falls back to a toast when the form is already closed', async () => {
    const toast = vi.spyOn(TestBed.inject(ToastStore), 'show');

    await open();
    store.submitCreate('taken@example.com', []);
    await settle();
    const request = expectCreate();

    store.cancelForm();
    request.flush(
      {
        type: '/problems/validation-error',
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: { Email: ['A user with this email address already exists.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();

    // `fromError` deliberately silences validation problems on the assumption they render
    // inline; with no form up there is no inline, so it is raised by hand instead.
    expect(toast).toHaveBeenCalled();
  });

  it('never renders one flow’s failure on the other flow’s surface', async () => {
    const toast = vi.spyOn(TestBed.inject(ToastStore), 'show');
    const user = directoryUserFixture();

    // A save goes out, the drawer is force-closed (a second Escape is uncancelable in
    // Chromium), and the create modal is opened before the response lands.
    store.beginEdit(user);
    await settle();
    expectPermissions().flush([permissionFixture()]);
    await settle();
    store.submitEdit([]);
    await settle();
    const update = backend.expectOne((request) => request.method === 'PUT');

    store.cancelForm();
    store.beginCreate();
    await settle();

    update.flush(
      {
        type: '/problems/resource-not-found',
        title: 'Resource not found',
        status: 404,
        detail: 'User not found.',
      },
      { status: 404, statusText: 'Not Found' },
    );
    await settle();

    // Routed by the flow that failed, not by whatever surface is up: the save's 404 must
    // not appear under the email field of a create form nobody has typed into.
    expect(store.formNotFound()).toBeNull();
    expect(store.formRejectedEmail()).toBeNull();
    // And it must not close that form either — those are the reader's own keystrokes.
    expect(store.formMode()).toBe('create');
    expect(toast).not.toHaveBeenCalled();
  });

  it('does not print a create’s rejection in the edit panel', async () => {
    const me = directoryUserFixture();
    sessionUser.set(me);

    await open();
    store.submitCreate('taken@example.com', []);
    await settle();
    const create = expectCreate();

    store.cancelForm();
    store.beginEdit(me);
    await settle();

    create.flush(
      {
        type: '/problems/validation-error',
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: {
          PermissionIds: ['Administrators cannot revoke their own Administrator permission.'],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();

    // `formSelfRevokedAdmin` is keyed to the row the *request* was about — a create is
    // about no row at all — so the edit panel must not mark a checkbox nobody touched.
    expect(store.formSelfRevokedAdmin()).toBe(false);
    expect(store.formError()).toBeNull();
    expect(store.formMode()).toBe('edit');
  });

  it('refuses a second save while one is in flight', async () => {
    const user = directoryUserFixture();
    store.beginEdit(user);
    await settle();
    expectPermissions().flush([permissionFixture()]);
    await settle();

    store.submitEdit([]);
    await settle();
    const first = backend.expectOne((request) => request.method === 'PUT');

    // The `formBusy` guard in `submitEdit` is what a double-click actually meets; the
    // `exhaustMap` under it is the second line, closing the race the guard cannot see.
    store.submitEdit([]);
    await settle();
    backend.expectNone((request) => request.method === 'PUT' && request !== first.request);

    // Cleared behind the guard's back, so the next call reaches the operator itself —
    // otherwise a change to `mergeMap` would leave this suite green.
    patchState(unprotected(store), { formBusy: false });
    store.submitEdit([]);
    await settle();
    backend.expectNone((request) => request.method === 'PUT' && request !== first.request);

    first.flush(user);
    await settle();
  });

  it('lets deactivations of different rows overlap', async () => {
    const first = directoryUserFixture();
    const second = directoryUserFixture();

    store.beginDeactivate(first);
    store.confirmDeactivate();
    await settle();
    store.beginDeactivate(second);
    store.confirmDeactivate();
    await settle();

    // mergeMap, not exhaustMap: two independent rows must both go, and exhaustMap would
    // silently drop the second.
    const requests = backend.match((request) => request.method === 'DELETE');
    expect(requests).toHaveLength(2);
    expect(store.pendingIds().size).toBe(2);

    requests.forEach((request) => request.flush(null, { status: 204, statusText: 'No Content' }));
    await settle();

    expect(store.pendingIds().size).toBe(0);
  });

  it('does not deactivate the same row twice', async () => {
    const user = directoryUserFixture();
    store.beginDeactivate(user);
    store.confirmDeactivate();
    await settle();
    const request = backend.expectOne((r) => r.method === 'DELETE');

    // The pending id is the guard: the confirmation cannot even reopen on that row.
    store.beginDeactivate(user);
    expect(store.deactivateTarget()).toBeNull();

    request.flush(null, { status: 204, statusText: 'No Content' });
    await settle();
  });

  it('forgets a standing refusal when the directory screen starts fresh', async () => {
    const me = directoryUserFixture();
    sessionUser.set(me);

    store.beginDeactivate(me);
    store.confirmDeactivate();
    await settle();
    backend
      .expectOne((request) => request.method === 'DELETE')
      .flush(
        {
          type: '/problems/validation-error',
          title: 'One or more validation errors occurred.',
          status: 400,
          errors: { Id: ['Administrators cannot deactivate their own account.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );
    await settle();
    expect(store.selfDeactivateRefusedId()).toBe(me.id);

    // This store outlives the route, and `role="alert"` fires on insertion — so a refusal
    // carried back to the tab would be announced as if it had just happened.
    store.clearSelfDeactivateRefusal();
    expect(store.selfDeactivateRefusedId()).toBeNull();
  });

  it('cancels every in-flight request on sign-out', async () => {
    const user = directoryUserFixture();
    store.beginEdit(user);
    await settle();
    expectPermissions().flush([permissionFixture()]);
    await settle();

    store.submitEdit([]);
    await settle();
    const update = backend.expectOne((request) => request.method === 'PUT');

    store.beginDeactivate(directoryUserFixture());
    store.confirmDeactivate();
    await settle();
    const remove = backend.expectOne((request) => request.method === 'DELETE');

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    await settle();

    // `withResetOnSignOut` empties the state; `takeUntil` is what stops these landing in
    // it a moment later.
    expect(update.cancelled).toBe(true);
    expect(remove.cancelled).toBe(true);
  });

  it('keeps the form usable when the permissions cannot be loaded', async () => {
    store.beginCreate();
    await settle();
    expectPermissions().flush(
      { type: '/problems/forbidden', title: 'Access denied', status: 403 },
      { status: 403, statusText: 'Forbidden' },
    );
    await settle();

    expect(store.permissionsError()).not.toBeNull();
    expect(store.formMode()).toBe('create');

    store.retryPermissions();
    await settle();
    expectPermissions().flush([permissionFixture()]);
    await settle();

    expect(store.permissionsError()).toBeNull();
    expect(store.checklist()).toHaveLength(1);
  });
});
