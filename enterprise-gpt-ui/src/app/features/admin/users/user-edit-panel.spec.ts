import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { UserDto } from '@domain/api/user';
import { ToastStore } from '@core/notifications/toast-store';
import { SessionStore } from '@core/session/session-store';
import { SELF_REVOKE_ADMIN_MESSAGE } from '@core/users/self-action-messages';
import { UserActionsStore } from '@core/users/user-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { ADMINISTRATOR_GRANT, MCP_GRANT, UPLOAD_FILE_GRANT } from '@testing/session';
import { directoryUserFixture } from '@testing/users';
import { UserEditPanel } from './user-edit-panel';

const PERMISSIONS_URL = `${TEST_API_BASE_URL}/api/permissions`;

const CATALOGUE = [ADMINISTRATOR_GRANT, UPLOAD_FILE_GRANT, MCP_GRANT];

describe('UserEditPanel (US-1203)', () => {
  let backend: HttpTestingController;
  let fixture: ComponentFixture<UserEditPanel>;
  let actions: InstanceType<typeof UserActionsStore>;
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
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    actions = TestBed.inject(UserActionsStore);

    fixture = TestBed.createComponent(UserEditPanel);
    await fixture.whenStable();
  });

  afterEach(() => {
    backend.verify();
  });

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function boxes(): HTMLInputElement[] {
    return [...host().querySelectorAll<HTMLInputElement>('.checklist .form-check-input')];
  }

  function boxFor(name: string): HTMLInputElement {
    const index = CATALOGUE_ORDER.indexOf(name);

    return boxes()[index]!;
  }

  function saveButton(): HTMLButtonElement {
    return [...host().querySelectorAll<HTMLButtonElement>('button')].find(
      (button) => button.textContent?.trim() === 'Save changes',
    )!;
  }

  async function open(user: UserDto): Promise<void> {
    actions.beginEdit(user);
    await fixture.whenStable();

    // Only the first open loads the catalogue; later ones reuse it.
    const pending = backend.match(PERMISSIONS_URL);
    pending.forEach((request) => request.flush(CATALOGUE));
    await fixture.whenStable();
  }

  function expectPut(id: string): TestRequest {
    return backend.expectOne(
      (request) =>
        request.url === `${TEST_API_BASE_URL}/api/users/${id}` && request.method === 'PUT',
    );
  }

  it('checks the grants the user already holds', async () => {
    await open(directoryUserFixture({ permissions: [UPLOAD_FILE_GRANT] }));

    expect(boxFor('Upload File').checked).toBe(true);
    expect(boxFor('Administrator').checked).toBe(false);
  });

  it('states that saving replaces the whole set', async () => {
    await open(directoryUserFixture());

    expect(host().textContent).toContain('Saving replaces this user’s entire permission set.');
    expect(host().textContent).toContain('Unchecking a permission revokes it.');
  });

  it('lock-marks an MCP-derived grant by naming its server, without disabling it', async () => {
    await open(directoryUserFixture());

    expect(host().textContent).toContain(`Managed by the ${MCP_GRANT.name} MCP server`);
    expect(boxFor(MCP_GRANT.name).disabled).toBe(false);
  });

  it('echoes the profile back, because the PUT requires all three fields', async () => {
    const user = directoryUserFixture({
      firstName: 'Grace',
      lastName: 'Hopper',
      email: 'grace@example.com',
      permissions: [UPLOAD_FILE_GRANT],
    });
    await open(user);

    boxFor('Administrator').click();
    await fixture.whenStable();
    saveButton().click();
    await fixture.whenStable();

    const request = expectPut(user.id);
    expect(request.request.body).toEqual({
      firstName: 'Grace',
      lastName: 'Hopper',
      email: 'grace@example.com',
      permissionIds: [UPLOAD_FILE_GRANT.id, ADMINISTRATOR_GRANT.id],
    });

    request.flush({ ...user, permissions: [UPLOAD_FILE_GRANT, ADMINISTRATOR_GRANT] });
    await fixture.whenStable();

    expect(actions.formMode()).toBeNull();
  });

  it('sends the complete desired set, so unchecking revokes', async () => {
    const user = directoryUserFixture({ permissions: [UPLOAD_FILE_GRANT, ADMINISTRATOR_GRANT] });
    await open(user);

    boxFor('Administrator').click();
    await fixture.whenStable();
    saveButton().click();
    await fixture.whenStable();

    const request = expectPut(user.id);
    expect(request.request.body).toMatchObject({ permissionIds: [UPLOAD_FILE_GRANT.id] });
    request.flush({ ...user, permissions: [UPLOAD_FILE_GRANT] });
    await fixture.whenStable();
  });

  it('renders a self-revocation refusal at the Administrator checkbox, not as a toast', async () => {
    const toast = vi.spyOn(TestBed.inject(ToastStore), 'show');
    const me = directoryUserFixture({ permissions: [ADMINISTRATOR_GRANT] });
    sessionUser.set(me);

    await open(me);
    boxFor('Administrator').click();
    await fixture.whenStable();
    saveButton().click();
    await fixture.whenStable();

    expectPut(me.id).flush(
      {
        ...PROBLEM_FIXTURES.validationError,
        errors: {
          PermissionIds: ['Administrators cannot revoke their own Administrator permission.'],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();

    // The board's second-person wording, not the server's third-person one.
    const message = host().querySelector('.checklist__error');
    expect(message?.textContent).toContain(SELF_REVOKE_ADMIN_MESSAGE);
    expect(host().textContent).not.toContain('Administrators cannot revoke');
    expect(toast).not.toHaveBeenCalled();

    // Described by the message, and the panel stays open on the row it is about.
    expect(boxFor('Administrator').getAttribute('aria-describedby')).toBe(message?.id);
    expect(actions.formMode()).toBe('edit');
  });

  it('does not mistake another administrator’s row for the caller’s own', async () => {
    sessionUser.set(directoryUserFixture({ permissions: [ADMINISTRATOR_GRANT] }));
    const somebodyElse = directoryUserFixture({ permissions: [ADMINISTRATOR_GRANT] });

    await open(somebodyElse);
    boxFor('Administrator').click();
    await fixture.whenStable();
    saveButton().click();
    await fixture.whenStable();

    // The request validator's per-element rule, which FluentValidation keys with the
    // index rather than with the bare property name.
    expectPut(somebodyElse.id).flush(
      {
        ...PROBLEM_FIXTURES.validationError,
        errors: { 'PermissionIds[0]': ['Permission ids cannot be empty.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();

    // A message that is not the self-revocation refusal still has to be shown, and never
    // in the board's wording.
    expect(host().textContent).not.toContain(SELF_REVOKE_ADMIN_MESSAGE);
    expect(host().textContent).toContain('Permission ids cannot be empty.');
  });

  it('says something when the row has gone since it was listed', async () => {
    const toast = vi.spyOn(TestBed.inject(ToastStore), 'fromError');
    const user = directoryUserFixture();
    await open(user);

    saveButton().click();
    await fixture.whenStable();

    // `PUT api/users/{id}` answers 404 for a row somebody else deactivated, and for a
    // permission id that no longer exists. This panel has no email field to render it
    // against, so it must not take the create form's inline path and fall silent.
    expectPut(user.id).flush(
      {
        type: '/problems/resource-not-found',
        title: 'Resource not found',
        status: 404,
        detail: 'User not found.',
      },
      { status: 404, statusText: 'Not Found' },
    );
    await fixture.whenStable();

    expect(toast).toHaveBeenCalled();
    expect(actions.formMode()).toBeNull();
  });

  it('shows a message it has no checkbox for alongside the refusal at one it does', async () => {
    const me = directoryUserFixture({ permissions: [ADMINISTRATOR_GRANT] });
    sessionUser.set(me);

    await open(me);
    boxFor('Administrator').click();
    await fixture.whenStable();
    saveButton().click();
    await fixture.whenStable();

    expectPut(me.id).flush(
      {
        ...PROBLEM_FIXTURES.validationError,
        errors: {
          PermissionIds: ['Administrators cannot revoke their own Administrator permission.'],
          'PermissionIds[0]': ['Permission ids cannot be empty.'],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();

    // The bare key is suppressed from the summary because it is already said at the
    // checkbox in the board's wording; the indexed key has no row to blame, so it must
    // still surface rather than being filtered out with it.
    expect(host().querySelector('.checklist__error')?.textContent).toContain(
      SELF_REVOKE_ADMIN_MESSAGE,
    );
    expect(host().textContent).toContain('Permission ids cannot be empty.');
    expect(host().textContent).not.toContain('Administrators cannot revoke');
  });

  it('reseeds when a second row is opened', async () => {
    await open(directoryUserFixture({ permissions: [ADMINISTRATOR_GRANT] }));
    expect(boxFor('Administrator').checked).toBe(true);

    actions.cancelForm();
    await fixture.whenStable();
    await open(directoryUserFixture({ permissions: [UPLOAD_FILE_GRANT] }));

    expect(boxFor('Administrator').checked).toBe(false);
    expect(boxFor('Upload File').checked).toBe(true);
  });
});

/** The order `UserActionsStore.checklist` puts the catalogue in: MCP-derived last. */
const CATALOGUE_ORDER = ['Administrator', 'Upload File', MCP_GRANT.name];
