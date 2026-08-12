import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpFixture } from '@testing/catalog';
import { sessionEvents } from '@core/events/session-events';
import { McpCatalogStore } from './mcp-catalog-store';

const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;

describe('McpCatalogStore', () => {
  let store: InstanceType<typeof McpCatalogStore>;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    store = TestBed.inject(McpCatalogStore);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
  });

  it('issues nothing until the session bootstrap asks it to load', () => {
    backend.expectNone(MCPS_URL);
    expect(store.servers()).toEqual([]);
  });

  it('memoizes ensureLoaded and renders the permitted list as it arrives', async () => {
    const servers = [mcpFixture({ name: 'Jira Cloud' }), mcpFixture({ name: 'Weather' })];
    const first = store.ensureLoaded();
    const second = store.ensureLoaded();

    backend.expectOne(MCPS_URL).flush(servers);

    await expect(first).resolves.toBe(true);
    await expect(second).resolves.toBe(true);
    expect(store.servers()).toEqual(servers);
    backend.expectNone(MCPS_URL);
  });

  it('records a failure and recovers on reload', async () => {
    const failed = store.ensureLoaded();
    backend.expectOne(MCPS_URL).error(new ProgressEvent('error'));
    await expect(failed).resolves.toBe(false);

    expect(store.error()?.kind).toBe('network');

    const recovered = store.reload();
    backend.expectOne(MCPS_URL).flush([mcpFixture()]);
    await expect(recovered).resolves.toBe(true);

    expect(store.error()).toBeNull();
    expect(store.servers()).toHaveLength(1);
  });

  it('goes idle when sign-out cancels the flight, and can load again afterwards', async () => {
    const cancelled = store.ensureLoaded();
    backend.expectOne(MCPS_URL);

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    await expect(cancelled).resolves.toBe(false);

    expect(store.error()).toBeNull();
    expect(store.servers()).toEqual([]);

    const next = store.ensureLoaded();
    backend.expectOne(MCPS_URL).flush([mcpFixture()]);
    await expect(next).resolves.toBe(true);
  });
});
