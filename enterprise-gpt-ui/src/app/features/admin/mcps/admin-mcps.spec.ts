import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { MCP_AUTH_TYPE, McpServerDto } from '@domain/api/mcp';
import { McpServerActionsStore } from '@core/catalog/mcp-server-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpServerFixture } from '@testing/catalog';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { AdminMcps } from './admin-mcps';

const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps/all`;

/**
 * `SearchInput`'s own 300 ms window, with room for the effect that follows it and for a
 * loaded CI runner — these are real timers, because faking the clock also stalls the
 * scheduler `whenStable()` awaits.
 */
const DEBOUNCE_MS = 450;

/** Somewhere to navigate to, so leaving the tab genuinely destroys the screen. */
@Component({
  selector: 'app-elsewhere',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
})
class Elsewhere {}

describe('AdminMcps (US-1208)', () => {
  let backend: HttpTestingController;
  let harness: RouterTestingHarness;

  beforeEach(async () => {
    installDialogPolyfill();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'admin/mcps', component: AdminMcps },
          { path: 'elsewhere', component: Elsewhere },
        ]),
        provideLocationMocks(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => {
    backend.verify();
  });

  function element(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  async function open(servers: McpServerDto[] = [mcpServerFixture()]): Promise<void> {
    await harness.navigateByUrl('/admin/mcps', AdminMcps);
    backend.expectOne(MCPS_URL).flush(servers);
    await harness.fixture.whenStable();
  }

  function rowText(): string {
    return element().querySelector('app-data-table')?.textContent?.replace(/\s+/g, ' ') ?? '';
  }

  async function type(term: string): Promise<void> {
    const field = element().querySelector<HTMLInputElement>('.search__field');
    field!.value = term;
    field!.dispatchEvent(new Event('input'));
    await harness.fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, DEBOUNCE_MS));
    await harness.fixture.whenStable();
  }

  it('renders the columns frame `5g` draws', async () => {
    await open([
      mcpServerFixture({
        name: 'SAP Ledger',
        description: 'Cost-center and PO queries for finance',
        url: 'https://mcp.example.test/sap',
        authType: MCP_AUTH_TYPE.entraIdOnBehalfOf,
        scope: 'api://sap/.default',
      }),
    ]);

    const headers = [...element().querySelectorAll('.data-table__head *, th')].map((node) =>
      node.textContent?.trim(),
    );
    expect(rowText()).toContain('SAP Ledger');
    expect(rowText()).toContain('Cost-center and PO queries for finance');
    expect(rowText()).toContain('https://mcp.example.test/sap');
    expect(rowText()).toContain('Entra ID (on behalf of)');
    expect(rowText()).toContain('api://sap/.default');
    expect(rowText()).toContain('Active');
    expect(headers.join(' ')).toContain('Permission');
  });

  it('renders the URL and the scope in the mono face frame `5g` uses', async () => {
    await open([
      mcpServerFixture({
        url: 'https://mcp.example.test/sap',
        authType: MCP_AUTH_TYPE.entraIdOnBehalfOf,
        scope: 'api://sap/.default',
      }),
    ]);

    const mono = [...element().querySelectorAll('.font-mono')].map((node) =>
      node.textContent?.trim(),
    );
    expect(mono).toContain('https://mcp.example.test/sap');
    expect(mono).toContain('api://sap/.default');
  });

  it('names the linked permission without a second request', async () => {
    await open([mcpServerFixture({ name: 'Jira Cloud', permissionId: 'perm-1' })]);

    // `McpServerService` creates the permission with the server's name and re-syncs it on
    // rename, so the badge is exact rather than approximate. `backend.verify()` is the
    // other half: nothing was fetched to resolve it.
    const badge = element().querySelector('app-permission-badge');
    expect(badge?.textContent).toContain('Jira Cloud');
  });

  it('says a server has no scope rather than leaving the cell blank', async () => {
    await open([mcpServerFixture({ authType: MCP_AUTH_TYPE.none, scope: null })]);

    expect(rowText()).toContain('None');
    // The em dash is aria-hidden; the meaning is text, because a blank cell reads as a
    // rendering fault rather than as "there is none".
    expect(rowText()).toContain('No scope');
  });

  it('renders a deactivated server muted, marked, and with no actions at all', async () => {
    await open([
      mcpServerFixture({ name: 'Current' }),
      mcpServerFixture({
        name: 'Legacy',
        dateDeactivated: '2026-08-01T00:00:00Z',
        permissionId: null,
      }),
    ]);

    expect(rowText()).toContain('Inactive');
    expect(element().querySelectorAll('.admin-mcps__name--retired')).toHaveLength(1);
    // There is no reactivate route, so every item would be an affordance that cannot
    // work — the shown-and-disabled pattern US-203 rejected. One live row, one menu.
    expect(element().querySelectorAll('app-menu')).toHaveLength(1);
    // The cascade takes the permission with the server, so there is no badge to render.
    expect(rowText()).toContain('No linked permission');
  });

  it('offers Edit and Deactivate on a live row', async () => {
    await open([mcpServerFixture({ name: 'SAP Ledger' })]);

    element().querySelector<HTMLButtonElement>('app-menu button')?.click();
    await harness.fixture.whenStable();

    const items = [...element().querySelectorAll('[appMenuItem]')].map((node) =>
      node.textContent?.trim(),
    );
    expect(items).toEqual(['Edit', 'Deactivate']);
  });

  it('filters on the URL as well as the name, without a second request', async () => {
    await open([
      mcpServerFixture({ name: 'Jira Cloud', url: 'https://mcp.atlassian.test/jira' }),
      mcpServerFixture({ name: 'SAP Ledger', url: 'https://mcp.sap.test/ledger' }),
    ]);

    await type('atlassian');

    // Regime C: the whole registry is already here, so the filter is exact and the server
    // is never asked again. `backend.verify()` is the second half of that claim.
    expect(rowText()).toContain('Jira Cloud');
    expect(rowText()).not.toContain('SAP Ledger');
  });

  it('names the term it could not match, and clears it', async () => {
    await open([mcpServerFixture({ name: 'Jira Cloud' })]);
    await type('llama');

    expect(rowText()).toContain('No servers match “llama”');

    element().querySelector<HTMLButtonElement>('[emptyStateAction]')?.click();
    await harness.fixture.whenStable();

    expect(rowText()).toContain('Jira Cloud');
  });

  it('tells an empty registry apart from a search that matched nothing', async () => {
    await open([]);

    expect(rowText()).toContain('No MCP servers yet');
    expect(rowText()).not.toContain('No servers match');
  });

  it('offers no sort control, because frame `5g` draws none', async () => {
    await open([mcpServerFixture(), mcpServerFixture()]);

    expect(element().querySelectorAll('[aria-sort]')).toHaveLength(0);
    expect(rowText()).not.toContain('Sort');
  });

  it('replaces the table with the problem when the request is refused', async () => {
    await harness.navigateByUrl('/admin/mcps', AdminMcps);
    backend
      .expectOne(MCPS_URL)
      .flush(PROBLEM_FIXTURES.permissionRequired, { status: 403, statusText: 'Forbidden' });
    await harness.fixture.whenStable();

    // The rows on screen would belong to a request that failed, so leaving an empty table
    // up would show "no servers" as if it were the answer.
    expect(element().querySelector('app-error-panel')).not.toBeNull();
    expect(element().querySelector('app-data-table')).toBeNull();
  });

  it('mounts both dialogs once, outside every branch', async () => {
    await open();

    // The dialog is the reader's own state: a re-render of the controls above it must not
    // destroy the form they are filling in.
    expect(element().querySelectorAll('app-mcp-server-form-dialog')).toHaveLength(1);
    expect(element().querySelectorAll('app-deactivate-mcp-server-dialog')).toHaveLength(1);
  });

  it('does not reopen a dialog left standing when the tab is revisited', async () => {
    await open();
    const actions = TestBed.inject(McpServerActionsStore);
    actions.beginCreate();
    await harness.fixture.whenStable();
    expect(element().querySelector('dialog[open]')).not.toBeNull();

    // Leave the tab without closing it — the store is root-scoped, so `formMode` survives.
    await harness.navigateByUrl('/elsewhere');
    await harness.fixture.whenStable();
    expect(actions.formMode()).toBe('create');

    await open();

    expect(actions.formMode()).toBeNull();
    expect(element().querySelector('dialog[open]')).toBeNull();
  });

  it('renders an auth type this build does not know without breaking the row', async () => {
    await open([mcpServerFixture({ name: 'Mystery', authType: 99 })]);

    // A value added server-side after this build. The row names it rather than showing a
    // blank cell, the way the model table's unknown provider does.
    expect(rowText()).toContain('99');
    expect(rowText()).toContain('Mystery');
  });

  it('states the count over the rows on screen', async () => {
    await open([
      mcpServerFixture({ name: 'Jira Cloud' }),
      mcpServerFixture({ name: 'SAP Ledger' }),
    ]);
    expect(rowText()).toContain('2 MCP servers registered');

    await type('jira');
    expect(rowText()).toContain('Showing 1 of 2 servers');
  });
});
