import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { MCP_AUTH_TYPE } from '@domain/api/mcp';
import { McpServerActionsStore } from '@core/catalog/mcp-server-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpServerFixture } from '@testing/catalog';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { McpServerFormDialog } from './mcp-server-form-dialog';

const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;
const BAD_REQUEST = { status: 400, statusText: 'Bad Request' };
const NONE = String(MCP_AUTH_TYPE.none);
const ENTRA = String(MCP_AUTH_TYPE.entraIdOnBehalfOf);

describe('McpServerFormDialog (US-1208)', () => {
  let backend: HttpTestingController;
  let fixture: ComponentFixture<McpServerFormDialog>;
  let actions: InstanceType<typeof McpServerActionsStore>;

  beforeEach(async () => {
    installDialogPolyfill();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    backend = TestBed.inject(HttpTestingController);
    actions = TestBed.inject(McpServerActionsStore);

    fixture = TestBed.createComponent(McpServerFormDialog);
    await fixture.whenStable();
  });

  afterEach(() => {
    backend.verify();
  });

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function field(id: string): HTMLInputElement | HTMLSelectElement {
    return host().querySelector(`#${id}`) as HTMLInputElement | HTMLSelectElement;
  }

  function submitButton(): HTMLButtonElement {
    return host().querySelector('button[type="submit"]') as HTMLButtonElement;
  }

  function errors(): string[] {
    return [...host().querySelectorAll('.mcp-form__error')].map(
      (node) => node.textContent?.trim() ?? '',
    );
  }

  function summary(): string {
    return host().querySelector('.mcp-form__summary')?.textContent?.replace(/\s+/g, ' ') ?? '';
  }

  async function type(id: string, text: string): Promise<void> {
    const control = field(id);
    control.value = text;
    control.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  /** Leaves the field, which is what marks it touched — and what reveals its messages. */
  async function blur(id: string): Promise<void> {
    field(id).dispatchEvent(new Event('blur'));
    await fixture.whenStable();
  }

  async function choose(id: string, value: string): Promise<void> {
    const control = field(id) as HTMLSelectElement;
    control.value = value;
    // `input`, not `change`: the `[formField]` directive listens on `input` for every
    // native control, `<select>` included.
    control.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  /** Fills every required field with something valid, on the None arm. */
  async function fill(): Promise<void> {
    await type('mcp-form-name', 'SAP Ledger');
    await type('mcp-form-description', 'Cost-center and PO queries for finance');
    await type('mcp-form-url', 'https://mcp.example.test/sap');
  }

  async function openCreate(): Promise<void> {
    actions.beginCreate();
    await fixture.whenStable();
  }

  async function openEdit(server = mcpServerFixture()): Promise<void> {
    actions.beginEdit(server);
    await fixture.whenStable();
  }

  async function submit(): Promise<TestRequest> {
    submitButton().click();
    await fixture.whenStable();

    return backend.expectOne((request) => request.url.startsWith(MCPS_URL));
  }

  it('binds the five fields the API accepts, and no linked-permission control', async () => {
    await openCreate();
    // Scope is conditional, so the Entra ID arm is the one that shows all five at once.
    await choose('mcp-form-auth', ENTRA);

    for (const id of [
      'mcp-form-name',
      'mcp-form-auth',
      'mcp-form-description',
      'mcp-form-url',
      'mcp-form-scope',
    ]) {
      expect(field(id), id).not.toBeNull();
    }

    // Frame `5h` draws a "Linked permission" select and neither action DTO carries one:
    // `McpServerService` creates that permission itself and names it after the server.
    // A disabled control here would be the shown-and-inert affordance US-203 rejected.
    expect(host().textContent).not.toContain('Linked permission');
    expect(host().querySelector('#mcp-form-permission')).toBeNull();
  });

  it('says why a name can be refused for colliding with a permission', async () => {
    await openCreate();

    // Server and permission names share one uniqueness space, and this note is the only
    // place the reader can learn that before the 400 explains it.
    expect(host().querySelector('#mcp-form-name-note')?.textContent).toContain(
      'A permission with this name is created alongside the server, and renamed with it.',
    );
  });

  it('offers the two auth types the API has, not frame `5h`’s three', async () => {
    await openCreate();

    const options = [...(field('mcp-form-auth') as HTMLSelectElement).options].map((option) => ({
      value: option.value,
      label: option.textContent?.trim(),
    }));

    expect(options).toEqual([
      { value: NONE, label: 'None' },
      { value: ENTRA, label: 'Entra ID (on behalf of)' },
    ]);
  });

  it('shows Scope only for Entra ID, because None refuses one outright', async () => {
    await openCreate();

    // The default arm is None — `IsInEnum` refuses the zero an empty select would parse to.
    expect(host().querySelector('#mcp-form-scope')).toBeNull();

    await choose('mcp-form-auth', ENTRA);
    expect(host().querySelector('#mcp-form-scope')).not.toBeNull();
  });

  it('clears the scope when the auth type stops accepting one', async () => {
    await openCreate();
    await fill();
    await choose('mcp-form-auth', ENTRA);
    await type('mcp-form-scope', 'api://sap/.default');

    await choose('mcp-form-auth', NONE);
    await choose('mcp-form-auth', ENTRA);

    // Cleared rather than hidden: a value the save would discard must not sit behind a
    // hidden field, which is the "blank versus zero" defect one screen over.
    expect((field('mcp-form-scope') as HTMLInputElement).value).toBe('');
  });

  it('stays submittable when an edit seeds a None server that carries a scope', async () => {
    // Reachable data: `McpServerDto.scope` is nullable and the API forbids the
    // combination only on write, so a legacy or seeded row can hold both. The clearing
    // effect tracks the auth type, which does not change when this replaces the create
    // defaults — so `hidden()` and `toMcpServerBody`'s normalization are what carry it,
    // and this is the case that pins them.
    await openEdit(
      mcpServerFixture({ authType: MCP_AUTH_TYPE.none, scope: 'api://legacy/.default' }),
    );

    expect(host().querySelector('#mcp-form-scope')).toBeNull();
    // A hidden field contributes nothing to its parent's validity, so the reader can
    // never reach a disabled Save explained only by a control that is not on screen.
    expect(submitButton().disabled).toBe(false);

    const request = await submit();
    expect(request.request.body).toMatchObject({ authType: 1, scope: null });
    request.flush(mcpServerFixture(), { status: 200, statusText: 'OK' });
    await fixture.whenStable();
  });

  it('explains an auth type the wire would not take, without waiting to be touched', async () => {
    // The table renders an unknown auth type rather than breaking the row, so opening
    // that row leaves the select with no matching option. Without this, Save would be
    // disabled over a body `toMcpServerBody` refuses, with nothing on screen saying why —
    // and the disabled button prevents the very interaction that would reveal it.
    await openEdit(mcpServerFixture({ authType: 99 }));

    expect((field('mcp-form-auth') as HTMLSelectElement).selectedIndex).toBe(-1);
    expect(submitButton().disabled).toBe(true);
    expect(errors().join(' ')).toContain('this version doesn’t support (99)');
    expect(field('mcp-form-auth').classList).toContain('is-invalid');
  });

  it('recovers once a supported auth type is chosen', async () => {
    await openEdit(mcpServerFixture({ authType: 99 }));
    await choose('mcp-form-auth', NONE);

    expect(errors()).toEqual([]);
    expect(submitButton().disabled).toBe(false);

    const request = await submit();
    expect(request.request.body).toMatchObject({ authType: 1 });
    request.flush(mcpServerFixture(), { status: 200, statusText: 'OK' });
    await fixture.whenStable();
  });

  it('sends the five fields, with the auth type as the number the wire carries', async () => {
    await openCreate();
    await fill();
    await choose('mcp-form-auth', ENTRA);
    await type('mcp-form-scope', 'api://sap/.default');

    const request = await submit();
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      name: 'SAP Ledger',
      description: 'Cost-center and PO queries for finance',
      url: 'https://mcp.example.test/sap',
      authType: 2,
      scope: 'api://sap/.default',
    });

    request.flush(mcpServerFixture(), { status: 201, statusText: 'Created' });
    await fixture.whenStable();
  });

  it('sends a null scope for a server with no authentication', async () => {
    await openCreate();
    await fill();

    const request = await submit();
    expect(request.request.body).toMatchObject({ authType: 1, scope: null });

    request.flush(mcpServerFixture(), { status: 201, statusText: 'Created' });
    await fixture.whenStable();
  });

  it('refuses a URL that is not absolute http or https, in the client’s own words', async () => {
    await openCreate();
    await fill();
    // The board's own typo — a comma where the dot belongs — which is why frame `5h`
    // draws this field invalid.
    await type('mcp-form-url', 'mcp.andessoftware,net/sap');
    await blur('mcp-form-url');

    // The server would refuse this too; the point is that it never has to.
    // `backend.verify()` proves the request never went out, and Save is disabled behind it.
    expect(errors()).toContain('Enter a full URL beginning with http:// or https://.');
    expect(submitButton().disabled).toBe(true);
  });

  it('requires a scope on the Entra ID arm', async () => {
    await openCreate();
    await fill();
    await choose('mcp-form-auth', ENTRA);
    await blur('mcp-form-scope');

    // The other direction of the same server rule — required here, refused for None.
    expect(errors()).toContain('A scope is required for an Entra ID server.');
    expect(submitButton().disabled).toBe(true);
  });

  it('seeds an edit from the row, without fetching it first', async () => {
    await openEdit(
      mcpServerFixture({
        name: 'SAP Ledger',
        description: 'Finance queries',
        url: 'https://mcp.example.test/sap',
        authType: MCP_AUTH_TYPE.entraIdOnBehalfOf,
        scope: 'api://sap/.default',
      }),
    );

    // `GET api/mcps/all` returns the complete DTO, so the row already carries every field
    // the full-representation PUT has to echo. `backend.verify()` is the assertion.
    expect((field('mcp-form-name') as HTMLInputElement).value).toBe('SAP Ledger');
    expect((field('mcp-form-url') as HTMLInputElement).value).toBe('https://mcp.example.test/sap');
    expect((field('mcp-form-auth') as HTMLSelectElement).value).toBe(ENTRA);
    expect((field('mcp-form-scope') as HTMLInputElement).value).toBe('api://sap/.default');
    expect(host().textContent).toContain('Edit MCP server');
  });

  it('renders the server’s message at its field, and clears it on the next edit', async () => {
    await openCreate();
    await fill();

    (await submit()).flush(
      {
        ...PROBLEM_FIXTURES.validationError,
        errors: {
          Name: ["The name 'SAP Ledger' is already in use by an MCP server or permission."],
        },
      },
      BAD_REQUEST,
    );
    await fixture.whenStable();

    expect(errors()).toContain(
      "The name 'SAP Ledger' is already in use by an MCP server or permission.",
    );

    // Signal Forms has no `setErrors`, so the message is reported only while the field
    // still holds the exact value that was sent — which is what clears it.
    await type('mcp-form-name', 'SAP Ledger EU');
    expect(errors()).not.toContain(
      "The name 'SAP Ledger' is already in use by an MCP server or permission.",
    );
  });

  it('names the server and what was rejected in frame `5h`’s panel, keeping every value', async () => {
    await openCreate();
    await fill();
    await choose('mcp-form-auth', ENTRA);
    await type('mcp-form-scope', 'api://sap/.default');

    (await submit()).flush(
      {
        ...PROBLEM_FIXTURES.validationError,
        errors: { Url: ['Url must be an absolute http or https URI.'] },
      },
      BAD_REQUEST,
    );
    await fixture.whenStable();

    expect(summary()).toContain('Couldn’t save');
    expect(summary()).toContain('SAP Ledger');
    expect(summary()).toContain('The server rejected the URL.');
    expect(summary()).toContain('Everything you typed is kept below.');

    // The dialog stays open and every field keeps what the reader typed.
    expect(host().querySelector('dialog[open]')).not.toBeNull();
    expect((field('mcp-form-url') as HTMLInputElement).value).toBe('https://mcp.example.test/sap');
    expect((field('mcp-form-scope') as HTMLInputElement).value).toBe('api://sap/.default');
    expect(field('mcp-form-url').classList).toContain('is-invalid');
  });

  it('renders a transport-level failure in the same panel, with a trace line', async () => {
    await openCreate();
    await fill();

    (await submit()).flush(null, { status: 502, statusText: 'Bad Gateway' });
    await fixture.whenStable();

    expect(summary()).toContain('SAP Ledger');
    // Support asks for the correlation id, and the panel is now the only place it can go.
    expect(host().querySelector('.mcp-form__trace')).not.toBeNull();
    expect(host().querySelector('dialog[open]')).not.toBeNull();
  });

  it('shows a message the form does not model rather than dropping it', async () => {
    await openCreate();
    await fill();

    (await submit()).flush(
      {
        ...PROBLEM_FIXTURES.validationError,
        errors: { '': ['That combination is not supported.'] },
      },
      BAD_REQUEST,
    );
    await fixture.whenStable();

    // A server message the reader cannot see is a save that silently fails.
    expect(summary()).toContain('That combination is not supported.');
  });

  it('starts a second open empty', async () => {
    await openCreate();
    await fill();
    actions.cancelForm();
    await fixture.whenStable();

    await openCreate();

    expect((field('mcp-form-name') as HTMLInputElement).value).toBe('');
    expect((field('mcp-form-auth') as HTMLSelectElement).value).toBe(NONE);
    expect(errors()).toEqual([]);
  });
});
