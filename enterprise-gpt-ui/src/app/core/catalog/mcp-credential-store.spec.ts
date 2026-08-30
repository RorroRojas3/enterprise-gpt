import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpFixture } from '@testing/catalog';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { McpDto } from '@domain/api/mcp';
import { ToastStore } from '@core/notifications/toast-store';
import { McpCatalogStore } from './mcp-catalog-store';
import { McpCredentialStore } from './mcp-credential-store';

const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;

function githubFixture(overrides: Partial<McpDto> = {}): McpDto {
  return mcpFixture({
    name: 'GitHub',
    requiresUserApiKey: true,
    hasUserApiKey: false,
    apiKeyHint: null,
    ...overrides,
  });
}

describe('McpCredentialStore', () => {
  let store: InstanceType<typeof McpCredentialStore>;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });
    store = TestBed.inject(McpCredentialStore);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
  });

  function credentialUrl(server: McpDto): string {
    return `${MCPS_URL}/${server.id}/credential`;
  }

  it('saves the key and closes the dialog on the response', async () => {
    const server = githubFixture();
    store.open(server);
    store.save('github_pat_abcdwxyz');

    const request = backend.expectOne(credentialUrl(server));
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ apiKey: 'github_pat_abcdwxyz' });
    expect(store.busy()).toBe(true);

    request.flush({
      mcpServerId: server.id,
      hasApiKey: true,
      apiKeyHint: 'wxyz',
      dateModified: '2026-08-29T00:00:00+00:00',
    });
    await Promise.resolve();

    expect(store.target()).toBeNull();
    expect(store.busy()).toBe(false);
  });

  it('patches the catalogue row rather than refetching the whole list', async () => {
    const catalog = TestBed.inject(McpCatalogStore);
    const server = githubFixture();
    const loaded = catalog.ensureLoaded();
    backend.expectOne(MCPS_URL).flush([server, mcpFixture({ name: 'Weather' })]);
    await loaded;

    store.open(server);
    store.save('github_pat_abcdwxyz');
    backend.expectOne(credentialUrl(server)).flush({
      mcpServerId: server.id,
      hasApiKey: true,
      apiKeyHint: 'wxyz',
      dateModified: '2026-08-29T00:00:00+00:00',
    });
    await Promise.resolve();
    await Promise.resolve();

    expect(catalog.servers()[0]!.hasUserApiKey).toBe(true);
    expect(catalog.servers()[0]!.apiKeyHint).toBe('wxyz');
    // Untouched, and no second GET was issued.
    expect(catalog.servers()[1]!.hasUserApiKey).toBe(false);
  });

  it('clears the catalogue row when the key is removed', async () => {
    const catalog = TestBed.inject(McpCatalogStore);
    const server = githubFixture({ hasUserApiKey: true, apiKeyHint: 'wxyz' });
    const loaded = catalog.ensureLoaded();
    backend.expectOne(MCPS_URL).flush([server]);
    await loaded;

    store.open(server);
    store.remove();

    const request = backend.expectOne(credentialUrl(server));
    expect(request.request.method).toBe('DELETE');
    request.flush(null, { status: 204, statusText: 'No Content' });
    await Promise.resolve();
    await Promise.resolve();

    expect(catalog.servers()[0]!.hasUserApiKey).toBe(false);
    expect(catalog.servers()[0]!.apiKeyHint).toBeNull();
    expect(store.target()).toBeNull();
  });

  it('keeps the dialog open on a rejection, holding the value the server refused', async () => {
    const server = githubFixture();
    store.open(server);
    store.save('github_pat_bad');

    backend.expectOne(credentialUrl(server)).flush(PROBLEM_FIXTURES.validationError, {
      status: 400,
      statusText: 'Bad Request',
    });
    await Promise.resolve();

    expect(store.target()?.id).toBe(server.id);
    expect(store.error()?.kind).toBe('validation-error');
    expect(store.rejectedValue()).toBe('github_pat_bad');
    expect(store.busy()).toBe(false);
  });

  it('reports on a toast once the dialog it belongs to is gone', async () => {
    const toasts = TestBed.inject(ToastStore);
    const server = githubFixture();
    store.open(server);
    store.save('github_pat_abcdwxyz');
    // Force-closed while the save was in flight, which Chromium allows on a second Escape
    // even though the modal is marked busy.
    store.close();

    backend.expectOne(credentialUrl(server)).flush(PROBLEM_FIXTURES.forbidden, {
      status: 403,
      statusText: 'Forbidden',
    });
    await Promise.resolve();

    expect(store.error()).toBeNull();
    expect(toasts.toasts()[0]?.title).toBe('Your API key for GitHub could not be saved');
  });

  it('does not open a second dialog while a request is in flight', () => {
    const server = githubFixture();
    store.open(server);
    store.save('github_pat_abcdwxyz');
    backend.expectOne(credentialUrl(server));

    store.open(githubFixture({ name: 'Jira' }));

    // Every field and button is disabled while busy, and Escape is suppressed: opening now
    // would present a modal with no way out.
    expect(store.target()?.id).toBe(server.id);
  });

  it('does not adopt a rejection meant for an earlier opening of the same dialog', async () => {
    const server = githubFixture();
    store.open(server);
    store.save('github_pat_first');
    store.close();

    backend.expectOne(credentialUrl(server)).flush(PROBLEM_FIXTURES.validationError, {
      status: 400,
      statusText: 'Bad Request',
    });
    await Promise.resolve();

    store.open(server);

    expect(store.error()).toBeNull();
    expect(store.rejectedValue()).toBeNull();
  });

  it('does nothing when asked to remove a key that is not stored', () => {
    store.open(githubFixture({ hasUserApiKey: false }));

    store.remove();

    backend.expectNone(() => true);
  });

  it('sends nothing when no dialog is open', () => {
    store.save('github_pat_abcdwxyz');

    backend.expectNone(() => true);
  });
});
