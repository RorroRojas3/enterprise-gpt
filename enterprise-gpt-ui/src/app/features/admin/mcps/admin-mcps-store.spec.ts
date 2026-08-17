import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { adminEvents } from '@core/events/admin-events';
import { sessionEvents } from '@core/events/session-events';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpServerFixture } from '@testing/catalog';
import { AdminMcpsStore } from './admin-mcps-store';

const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps/all`;

describe('AdminMcpsStore (US-1208)', () => {
  let store: InstanceType<typeof AdminMcpsStore>;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        AdminMcpsStore,
      ],
    });

    store = TestBed.inject(AdminMcpsStore);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
  });

  async function settle(): Promise<void> {
    TestBed.tick();
    await Promise.resolve();
  }

  async function loadWith(servers: ReturnType<typeof mcpServerFixture>[]): Promise<void> {
    store.load();
    await settle();
    backend.expectOne(MCPS_URL).flush(servers);
    await settle();
  }

  it('reads the administrative route, which is the only one returning deactivated servers', async () => {
    store.load();
    await settle();

    const request = backend.expectOne(MCPS_URL);
    expect(request.request.method).toBe('GET');
    // Unpaginated: no skip, no take, no name. That is what makes regime C honest.
    expect(request.request.params.keys()).toEqual([]);
    request.flush([
      mcpServerFixture(),
      mcpServerFixture({ dateDeactivated: '2026-08-16T10:00:00Z', permissionId: null }),
    ]);
    await settle();

    // The user-facing `api/mcps` would have hidden the second row, and the table has to
    // show it or Deactivate looks inert.
    expect(store.entities()).toHaveLength(2);
  });

  it('matches the description and the URL as well as the name', async () => {
    await loadWith([
      mcpServerFixture({
        name: 'Jira Cloud',
        description: 'Issue search for delivery',
        url: 'https://mcp.atlassian.test/jira',
      }),
      mcpServerFixture({
        name: 'SAP Ledger',
        description: 'Cost-center and PO queries',
        url: 'https://mcp.sap.test/ledger',
      }),
    ]);

    // Three separate strings, none of which appears in another row — so each of these can
    // only pass if that field is in the searchable text the label promises.
    store.search('atlassian');
    expect(store.results().map((server) => server.name)).toEqual(['Jira Cloud']);

    store.search('cost-center');
    expect(store.results().map((server) => server.name)).toEqual(['SAP Ledger']);

    store.search('ledger');
    expect(store.results().map((server) => server.name)).toEqual(['SAP Ledger']);
  });

  it('matches case-insensitively, on a substring', async () => {
    await loadWith([mcpServerFixture({ name: 'Confluence Search' })]);

    store.search('CONFLUENCE');
    expect(store.results()).toHaveLength(1);

    store.search('luence');
    expect(store.results()).toHaveLength(1);
  });

  it('filters without touching the server, because the set on screen is complete', async () => {
    await loadWith([mcpServerFixture(), mcpServerFixture()]);

    store.search('nothing matches this');
    await settle();

    // Regime C: no second request, ever. `backend.verify()` is the assertion.
    expect(store.results()).toHaveLength(0);
    expect(store.hasNoMatches()).toBe(true);
  });

  it('tells an empty registry apart from a search that found nothing', async () => {
    await loadWith([]);

    expect(store.isEmpty()).toBe(true);
    expect(store.hasNoMatches()).toBe(false);

    await loadWith([mcpServerFixture({ name: 'Jira Cloud' })]);
    store.search('confluence');

    expect(store.isEmpty()).toBe(false);
    expect(store.hasNoMatches()).toBe(true);
  });

  it('states the filtered figure against the total', async () => {
    await loadWith([
      mcpServerFixture({ name: 'Jira Cloud' }),
      mcpServerFixture({ name: 'SAP Ledger' }),
    ]);

    expect(store.countSummary()).toBe('2 MCP servers registered');

    store.search('jira');
    expect(store.countSummary()).toBe('Showing 1 of 2 servers');
  });

  it('offers sort only over a set it holds in full', async () => {
    await loadWith([mcpServerFixture()]);

    // Regime C's whole premise: one request is the complete set, so there is no
    // pagination to be partway through.
    expect(store.canSort()).toBe(true);
    expect(store.sortUnavailableReason()).toBeNull();
  });

  it('keeps the set authoritative while a mutation’s refetch is in flight', async () => {
    await loadWith([mcpServerFixture(), mcpServerFixture()]);

    TestBed.inject(Dispatcher).dispatch(adminEvents.mcpCatalogChanged());
    await settle();
    const refetch = backend.expectOne(MCPS_URL);

    // `isFulfilled` alone would read false here, so a sort control would vanish and come
    // back on every save. The rows on screen are still the whole registry.
    expect(store.canSort()).toBe(true);
    expect(store.sortUnavailableReason()).toBeNull();

    refetch.flush([mcpServerFixture()]);
    await settle();
    expect(store.canSort()).toBe(true);
  });

  it('refetches when the registry changes, rather than patching a row', async () => {
    await loadWith([mcpServerFixture({ name: 'SAP Ledger', permissionId: 'perm-1' })]);

    TestBed.inject(Dispatcher).dispatch(adminEvents.mcpCatalogChanged());
    await settle();

    // The event is void: a 204 carries no `dateDeactivated` for the row that has to turn
    // muted, and the cascade takes the linked permission with it. Only a refetch shows it.
    backend.expectOne(MCPS_URL).flush([
      mcpServerFixture({
        name: 'SAP Ledger',
        dateDeactivated: '2026-08-16T10:00:00Z',
        permissionId: null,
      }),
    ]);
    await settle();

    expect(store.entities()[0]?.dateDeactivated).toBe('2026-08-16T10:00:00Z');
    expect(store.entities()[0]?.permissionId).toBeNull();
  });

  it('supersedes an in-flight query rather than racing it', async () => {
    store.load();
    await settle();
    const first = backend.expectOne(MCPS_URL);

    store.refresh();
    await settle();
    const second = backend.expectOne(MCPS_URL);

    // switchMap: the first request is cancelled outright, so a slow answer cannot paint
    // over a fast one.
    expect(first.cancelled).toBe(true);

    second.flush([mcpServerFixture({ name: 'Jira Cloud' })]);
    await settle();
    expect(store.entities().map((server) => server.name)).toEqual(['Jira Cloud']);
  });

  it('records a failure and recovers on retry', async () => {
    store.load();
    await settle();
    backend.expectOne(MCPS_URL).error(new ProgressEvent('error'));
    await settle();

    expect(store.error()?.kind).toBe('network');

    store.retry();
    await settle();
    backend.expectOne(MCPS_URL).flush([mcpServerFixture()]);
    await settle();

    expect(store.error()).toBeNull();
    expect(store.entities()).toHaveLength(1);
  });

  it('cancels in flight and empties on sign-out', async () => {
    await loadWith([mcpServerFixture()]);

    store.refresh();
    await settle();
    backend.expectOne(MCPS_URL);

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    await settle();

    // `withResetOnSignOut` clears the state; `takeUntil(signedOut$)` is what cancels the
    // request, which is why the store composes both.
    expect(store.entities()).toEqual([]);
    expect(store.query()).toBe('');
  });
});
