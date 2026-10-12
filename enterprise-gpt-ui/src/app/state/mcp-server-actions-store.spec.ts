import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Dispatcher, Events } from '@ngrx/signals/events';
import { Subscription } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MCP_AUTH_TYPE } from '@shared/models/api/mcp.model';
import { McpServerFormValue } from '@shared/utils/catalog/mcp-server-form';
import { adminEvents } from '@core/events/admin-events';
import { sessionEvents } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpServerFixture } from '@testing/catalog';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { McpServerActionsStore } from './mcp-server-actions-store';

const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;

function formValue(overrides: Partial<McpServerFormValue> = {}): McpServerFormValue {
  return {
    name: 'SAP Ledger',
    description: 'Cost-center and PO queries for finance',
    url: 'https://mcp.example.test/sap',
    authType: String(MCP_AUTH_TYPE.entraIdOnBehalfOf),
    scope: 'api://sap/.default',
    iconKey: '',
    headers: '',
    ...overrides,
  };
}

const BAD_REQUEST = { status: 400, statusText: 'Bad Request' };

function validationProblem(errors: Record<string, string[]>): object {
  return { ...PROBLEM_FIXTURES.validationError, errors };
}

describe('McpServerActionsStore', () => {
  let store: InstanceType<typeof McpServerActionsStore>;
  let backend: HttpTestingController;
  let toasts: {
    success: ReturnType<typeof vi.fn>;
    fromError: ReturnType<typeof vi.fn>;
    show: ReturnType<typeof vi.fn>;
  };
  let changed: number;
  let subscription: Subscription;

  beforeEach(() => {
    toasts = { success: vi.fn(), fromError: vi.fn(), show: vi.fn() };

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastStore, useValue: toasts },
      ],
    });

    store = TestBed.inject(McpServerActionsStore);
    backend = TestBed.inject(HttpTestingController);

    changed = 0;
    subscription = TestBed.inject(Events)
      .on(adminEvents.mcpCatalogChanged)
      .subscribe(() => (changed += 1));
  });

  afterEach(() => {
    subscription.unsubscribe();
    backend.verify();
  });

  function settle(): void {
    TestBed.tick();
  }

  it('registers a server and announces the registry changed', () => {
    store.beginCreate();
    store.submitForm(formValue());
    settle();

    const request = backend.expectOne(MCPS_URL);
    expect(request.request.method).toBe('POST');
    // authType is numeric: the API has no string enum converter, so a name would fail `IsInEnum`.
    expect(request.request.body).toEqual({
      name: 'SAP Ledger',
      description: 'Cost-center and PO queries for finance',
      url: 'https://mcp.example.test/sap',
      authType: 2,
      scope: 'api://sap/.default',
      iconKey: null,
      headers: null,
    });

    request.flush(mcpServerFixture({ name: 'SAP Ledger' }), { status: 201, statusText: 'Created' });
    settle();

    expect(changed).toBe(1);
    expect(store.formMode()).toBeNull();
    expect(toasts.success).toHaveBeenCalledWith('SAP Ledger registered');
  });

  it('refuses a second submit while one is in flight', () => {
    store.beginCreate();
    store.submitForm(formValue());
    settle();
    backend.expectOne(MCPS_URL);

    // Asserts the `formBusy` guard, which short-circuits before `exhaustMap` is reached.
    store.submitForm(formValue());
    settle();

    backend.expectNone(MCPS_URL);
  });

  it('never sends a body it could not build', () => {
    store.beginCreate();
    // Entra ID with no scope: `toMcpServerBody` refuses rather than posting an empty one.
    store.submitForm(formValue({ scope: '' }));
    settle();

    backend.expectNone(MCPS_URL);
    expect(store.formBusy()).toBe(false);
  });

  it('edits a server with the full representation the PUT requires', () => {
    const target = mcpServerFixture({ name: 'SAP Ledger' });
    store.beginEdit(target);
    store.submitForm(formValue({ name: 'SAP Ledger EU' }));
    settle();

    const request = backend.expectOne(`${MCPS_URL}/${target.id}`);
    expect(request.request.method).toBe('PUT');
    // Every field, not only the changed one: the mapper assigns all six unconditionally,
    // so an omitted one is a cleared one.
    expect(Object.keys(request.request.body as object).sort()).toEqual([
      'authType',
      'description',
      'headers',
      'iconKey',
      'name',
      'scope',
      'url',
    ]);

    request.flush(mcpServerFixture({ name: 'SAP Ledger EU' }));
    settle();

    expect(changed).toBe(1);
    expect(store.isRowPending(target.id)).toBe(false);
  });

  it('keeps a validation failure inline, with the dialog and every field intact', () => {
    store.beginCreate();
    // Client-valid but refused by the server: the client can't mirror every server rule.
    const value = formValue({ url: 'https://mcp.example.test/sap' });
    store.submitForm(value);
    settle();

    backend
      .expectOne(MCPS_URL)
      .flush(
        validationProblem({ Url: ['Url must be an absolute http or https URI.'] }),
        BAD_REQUEST,
      );
    settle();

    expect(store.formMode()).toBe('create');
    expect(store.formError()?.kind).toBe('validation-error');
    expect(store.formRejectedValue()).toEqual(value);
    expect(toasts.fromError).not.toHaveBeenCalled();
    expect(toasts.show).not.toHaveBeenCalled();
  });

  it('names the server and the rejected field in frame 5h’s panel', () => {
    store.beginCreate();
    store.submitForm(formValue({ name: 'SAP Ledger' }));
    settle();

    backend
      .expectOne(MCPS_URL)
      .flush(
        validationProblem({ Url: ['Url must be an absolute http or https URI.'] }),
        BAD_REQUEST,
      );
    settle();

    expect(store.formFailure()).toEqual({
      serverName: 'SAP Ledger',
      reason: 'The server rejected the URL.',
      // The fields carry the detail; a trace id beside them is noise.
      traceLine: null,
    });
  });

  it('renders a non-validation failure in the panel rather than as a toast', () => {
    store.beginCreate();
    store.submitForm(formValue());
    settle();

    backend.expectOne(MCPS_URL).flush(null, { status: 500, statusText: 'Server Error' });
    settle();

    // A surface still on screen owns the message, so it is not reported twice.
    expect(store.formMode()).toBe('create');
    expect(store.formFailure()?.serverName).toBe('SAP Ledger');
    expect(store.formFailure()?.traceLine).not.toBeNull();
    expect(toasts.fromError).not.toHaveBeenCalled();
  });

  it('never renders a rejection on a surface that is no longer the one it came from', () => {
    const target = mcpServerFixture({ name: 'SAP Ledger' });
    store.beginEdit(target);
    store.submitForm(formValue({ name: 'SAP Ledger EU' }));
    settle();
    const save = backend.expectOne(`${MCPS_URL}/${target.id}`);

    // Force-closed before the save lands (Chromium can't cancel a second Escape); reopening is
    // refused while `formBusy`, so the closed surface is the only one left to test.
    store.cancelForm();

    save.flush(validationProblem({ Name: ['Already in use.'] }), BAD_REQUEST);
    settle();

    expect(store.formError()).toBeNull();
    expect(store.formFailure()).toBeNull();
    expect(store.formMode()).toBeNull();
    // By hand: `fromError` silences validation errors, assuming they render inline.
    expect(toasts.show).toHaveBeenCalledOnce();
    expect(toasts.fromError).not.toHaveBeenCalled();
  });

  it('names the server the request was about, not the row opened after it', () => {
    const target = mcpServerFixture({ name: 'SAP Ledger' });
    store.beginEdit(target);
    // A failed rename names the row opened: the typed name belongs to no server yet.
    store.submitForm(formValue({ name: 'SAP Ledger EU' }));
    settle();

    backend
      .expectOne(`${MCPS_URL}/${target.id}`)
      .flush(validationProblem({ Name: ['Already in use.'] }), BAD_REQUEST);
    settle();

    expect(store.formFailure()?.serverName).toBe('SAP Ledger');
  });

  it('deactivates a server and announces the registry changed', () => {
    const target = mcpServerFixture({ name: 'SAP Ledger' });
    store.beginDeactivate(target);
    expect(store.deactivateTarget()).toBe(target);

    store.confirmDeactivate();
    settle();
    expect(store.deactivateTarget()).toBeNull();

    const request = backend.expectOne(`${MCPS_URL}/${target.id}`);
    expect(request.request.method).toBe('DELETE');
    request.flush(null, { status: 204, statusText: 'No Content' });
    settle();

    expect(changed).toBe(1);
    expect(toasts.success).toHaveBeenCalledWith('SAP Ledger deactivated');
  });

  it('lets deactivations of different servers overlap', () => {
    const first = mcpServerFixture();
    const second = mcpServerFixture();

    store.beginDeactivate(first);
    store.confirmDeactivate();
    settle();
    store.beginDeactivate(second);
    store.confirmDeactivate();
    settle();

    const requests = backend.match((request) => request.method === 'DELETE');
    expect(requests).toHaveLength(2);
    requests.forEach((request) => request.flush(null, { status: 204, statusText: 'No Content' }));
    settle();

    expect(changed).toBe(2);
  });

  it('toasts a row action’s failure, because no form produced it', () => {
    const target = mcpServerFixture();
    store.beginDeactivate(target);
    store.confirmDeactivate();
    settle();

    backend.expectOne(`${MCPS_URL}/${target.id}`).flush(null, {
      status: 404,
      statusText: 'Not Found',
    });
    settle();

    expect(toasts.fromError).toHaveBeenCalled();
    expect(store.isRowPending(target.id)).toBe(false);
    expect(changed).toBe(0);
  });

  it('refuses to open a dialog it could not dismiss', () => {
    store.beginCreate();
    store.submitForm(formValue());
    settle();
    const request = backend.expectOne(MCPS_URL);

    store.cancelForm();
    expect(store.formBusy()).toBe(true);
    store.beginCreate();
    expect(store.formMode()).toBeNull();

    request.flush(mcpServerFixture(), { status: 201, statusText: 'Created' });
    settle();
    expect(store.formBusy()).toBe(false);
  });

  it('does not clear formBusy when a form is closed mid-flight', () => {
    store.beginCreate();
    store.submitForm(formValue());
    settle();
    const request = backend.expectOne(MCPS_URL);

    store.cancelForm();

    // Tracks the request, not the surface, or a reopened form would swallow the next submit.
    expect(store.formMode()).toBeNull();
    expect(store.formBusy()).toBe(true);

    request.flush(mcpServerFixture(), { status: 201, statusText: 'Created' });
    settle();
    expect(store.formBusy()).toBe(false);
  });

  it('refuses to edit a row whose own request is in flight', () => {
    const target = mcpServerFixture();
    store.beginDeactivate(target);
    store.confirmDeactivate();
    settle();
    const request = backend.expectOne(`${MCPS_URL}/${target.id}`);

    store.beginEdit(target);
    expect(store.formMode()).toBeNull();

    request.flush(null, { status: 204, statusText: 'No Content' });
    settle();
  });

  it('cancels everything on sign-out', () => {
    store.beginCreate();
    store.submitForm(formValue());
    settle();
    const request = backend.expectOne(MCPS_URL);

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    settle();

    expect(request.cancelled).toBe(true);
    expect(store.formMode()).toBeNull();
    expect(store.formBusy()).toBe(false);
  });
});
