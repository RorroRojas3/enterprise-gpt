import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { UserActionsStore } from '@core/users/user-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { ADMINISTRATOR_GRANT, MCP_GRANT } from '@testing/session';
import { directoryUserFixture } from '@testing/users';
import { UserFormDialog } from './user-form-dialog';

const USERS_URL = `${TEST_API_BASE_URL}/api/users`;
const PERMISSIONS_URL = `${TEST_API_BASE_URL}/api/permissions`;

describe('UserFormDialog (US-1202)', () => {
  let backend: HttpTestingController;
  let fixture: ComponentFixture<UserFormDialog>;
  let actions: InstanceType<typeof UserActionsStore>;

  beforeEach(async () => {
    installDialogPolyfill();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    backend = TestBed.inject(HttpTestingController);
    actions = TestBed.inject(UserActionsStore);

    fixture = TestBed.createComponent(UserFormDialog);
    await fixture.whenStable();
  });

  afterEach(() => {
    backend.verify();
  });

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function emailField(): HTMLInputElement {
    return host().querySelector('#user-form-email') as HTMLInputElement;
  }

  function submitButton(): HTMLButtonElement {
    return host().querySelector('button[type="submit"]') as HTMLButtonElement;
  }

  function errors(): string[] {
    return [...host().querySelectorAll('.user-form__error')].map(
      (node) => node.textContent?.trim() ?? '',
    );
  }

  async function type(text: string): Promise<void> {
    const field = emailField();
    field.value = text;
    field.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  async function open(permissions = [ADMINISTRATOR_GRANT, MCP_GRANT]): Promise<void> {
    actions.beginCreate();
    await fixture.whenStable();
    backend.expectOne(PERMISSIONS_URL).flush(permissions);
    await fixture.whenStable();
  }

  function expectCreate(): TestRequest {
    return backend.expectOne((request) => request.url === USERS_URL && request.method === 'POST');
  }

  async function submitWith(address: string): Promise<TestRequest> {
    await open();
    await type(address);
    submitButton().click();
    await fixture.whenStable();

    return expectCreate();
  }

  it('collects a work email and permissions, and nothing else', async () => {
    await open();

    expect(emailField()).not.toBeNull();
    expect(host().querySelectorAll('input, textarea, select')).toHaveLength(
      // The email field plus one checkbox per permission. No name, role, status,
      // password or confirm-password field exists, because the API has none.
      1 + 2,
    );
    expect(host().textContent).not.toContain('Password');
    expect(host().textContent).not.toContain('Role');
  });

  it('marks an MCP-derived permission without disabling it', async () => {
    await open();

    const boxes = host().querySelectorAll<HTMLInputElement>('.checklist .form-check-input');
    expect(boxes).toHaveLength(2);
    // Granting MCP tool access is the only way anybody gets it — the lock says the
    // permission's definition belongs to its server, not that the grant is frozen.
    expect([...boxes].some((box) => box.disabled)).toBe(false);
    expect(host().textContent).toContain('managed by its MCP server');
  });

  it('refuses to submit an address the schema rejects, and says why once it is left', async () => {
    await open();
    await type('not-an-email');

    // The submit button is disabled while the form is invalid, so the message cannot
    // wait on a press — it appears when the field is left, which is what `touched` is.
    submitButton().click();
    await fixture.whenStable();
    backend.expectNone((request) => request.method === 'POST');
    expect(submitButton().disabled).toBe(true);

    emailField().dispatchEvent(new Event('blur'));
    await fixture.whenStable();

    expect(errors()).toContain('Enter a valid email address.');
  });

  it('sends the trimmed address with the checked permissions', async () => {
    await open();
    await type('  grace@example.com  ');
    host().querySelector<HTMLInputElement>('.checklist .form-check-input')!.click();
    await fixture.whenStable();

    submitButton().click();
    await fixture.whenStable();

    const request = expectCreate();
    expect(request.request.body).toEqual({
      email: 'grace@example.com',
      permissionIds: [ADMINISTRATOR_GRANT.id],
    });
    request.flush(directoryUserFixture(), { status: 201, statusText: 'Created' });
    await fixture.whenStable();
  });

  it('renders the server’s 400 against the field, and clears it on the next keystroke', async () => {
    const request = await submitWith('taken@example.com');
    request.flush(
      {
        ...PROBLEM_FIXTURES.validationError,
        errors: { Email: ['A user with this email address already exists.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();

    expect(errors()).toContain('A user with this email address already exists.');

    // Signal Forms has no `setErrors`, so the message is reported by a rule that only
    // fires while the field still holds the exact value the server rejected.
    await type('taken2@example.com');
    expect(errors()).not.toContain('A user with this email address already exists.');
  });

  it('renders the directory 404 against the field too', async () => {
    const request = await submitWith('nobody@example.com');
    request.flush(
      {
        type: '/problems/resource-not-found',
        title: 'Resource not found',
        status: 404,
        detail: 'No directory user found for that email address.',
      },
      { status: 404, statusText: 'Not Found' },
    );
    await fixture.whenStable();

    expect(errors()).toContain('No directory user found for that email address.');
  });

  it('shows a message the form does not model rather than dropping it', async () => {
    const request = await submitWith('grace@example.com');
    request.flush(
      {
        ...PROBLEM_FIXTURES.validationError,
        errors: { PermissionIds: ['Permission ids cannot be empty.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();

    expect(errors()).toContain('Permission ids cannot be empty.');
  });

  it('starts empty again on a second open', async () => {
    await open();
    await type('first@example.com');
    actions.cancelForm();
    await fixture.whenStable();

    actions.beginCreate();
    await fixture.whenStable();

    expect(emailField().value).toBe('');
  });
});
