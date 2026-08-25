import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Dispatcher, Events } from '@ngrx/signals/events';
import { Subscription } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MCP_AUTH_TYPE } from '@domain/api/mcp';
import { adminEvents } from '@core/events/admin-events';
import { sessionEvents } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpServerFixture } from '@testing/catalog';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { McpServerActionsStore } from './mcp-server-actions-store';
import { McpServerFormValue } from './mcp-server-form';

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

describe('McpServerActionsStore (US-1208)', () => {
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
    // All seven, and the auth type as the number the wire carries: the API registers no
    // string enum converter, so `"EntraIdOnBehalfOf"` would fail `IsInEnum`.
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

    // The `formBusy` guard is what this asserts — it short-circuits before the rxMethod,
    // so `exhaustMap` is the belt behind it rather than the thing on trial here. A
    // duplicate that slipped through either would take the "name already in use" 400 the
    // first one had just made true.
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
    // Client-valid, and refused by the server anyway — the case that matters, since the
    // client mirrors the validator but can never mirror all of it.
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

    // Nothing closes the dialog: five fields including a 1024-character description are
    // behind it, and `retryInterceptor` retries neither a POST nor a PUT.
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

    // The criterion asks for the panel on a 502 as well as on a 400, so a surface that is
    // still on screen owns the message — reporting it twice is the defect this avoids.
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

    // The reader force-closes the dialog before the save lands — a second Escape is
    // uncancelable in Chromium. Opening the *other* form in this window is no longer
    // possible at all (`beginCreate` refuses while `formBusy`), which is a stronger
    // guarantee than this test could make; what remains reachable is the closed surface.
    store.cancelForm();

    save.flush(validationProblem({ Name: ['Already in use.'] }), BAD_REQUEST);
    settle();

    // Nothing inline, and no panel, because there is nothing on screen to render against.
    expect(store.formError()).toBeNull();
    expect(store.formFailure()).toBeNull();
    expect(store.formMode()).toBeNull();
    // Raised by hand, because `fromError` silences validation problems on the assumption
    // they render inline and there is no inline left to render on.
    expect(toasts.show).toHaveBeenCalledOnce();
    expect(toasts.fromError).not.toHaveBeenCalled();
  });

  it('names the server the request was about, not the row opened after it', () => {
    const target = mcpServerFixture({ name: 'SAP Ledger' });
    store.beginEdit(target);
    // A rename that fails still names the row the reader opened, not the new name they
    // typed — that server does not exist yet, and naming it would be a fiction.
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
    // The confirmation closes at once and the request settles behind it.
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

    // mergeMap: independent rows, and exhaustMap would have silently dropped the second.
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

    // `formBusy` is also `Modal`'s Escape and backdrop suppression, so a dialog opened
    // now would hold no focusable field and no way out (WCAG 2.1.2).
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

    // It tracks the request, not the surface: a flight still occupying `exhaustMap` has
    // to stay visible or the next submit is swallowed silently.
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
