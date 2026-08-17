import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { McpServerActionsStore } from '@core/catalog/mcp-server-actions-store';
import { ToastStore } from '@core/notifications/toast-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpServerFixture } from '@testing/catalog';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { DeactivateMcpServerDialog } from './deactivate-mcp-server-dialog';

const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;

describe('DeactivateMcpServerDialog (US-1208)', () => {
  let backend: HttpTestingController;
  let fixture: ComponentFixture<DeactivateMcpServerDialog>;
  let actions: InstanceType<typeof McpServerActionsStore>;

  beforeEach(async () => {
    installDialogPolyfill();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastStore, useValue: { success: vi.fn(), fromError: vi.fn(), show: vi.fn() } },
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    actions = TestBed.inject(McpServerActionsStore);

    fixture = TestBed.createComponent(DeactivateMcpServerDialog);
    await fixture.whenStable();
  });

  afterEach(() => {
    backend.verify();
  });

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return host().textContent?.replace(/\s+/g, ' ') ?? '';
  }

  function confirmButton(): HTMLButtonElement {
    return host().querySelector('.btn-danger') as HTMLButtonElement;
  }

  async function open(server = mcpServerFixture({ name: 'SAP Ledger' })): Promise<void> {
    actions.beginDeactivate(server);
    await fixture.whenStable();
  }

  it('names the server and styles the destructive action red', async () => {
    await open();

    expect(text()).toContain('SAP Ledger');
    expect(confirmButton().textContent?.trim()).toBe('Deactivate server');
  });

  it('lands initial focus on Cancel', async () => {
    await open();

    // Enter on a freshly opened destructive confirmation must revoke nobody's access.
    const autofocused = host().querySelector('[data-modal-autofocus]');
    expect(autofocused?.textContent?.trim()).toBe('Cancel');
  });

  it('says the permission and its grants go with the server', async () => {
    await open();

    // `DeactivateMcpServerAsync` deactivates the linked permission and every grant of it
    // in the same save. That is a change to other people's access, and the part of this
    // action a reader is least likely to expect.
    expect(text()).toContain('revoked from everyone who held it');
    expect(text()).toContain('cannot be brought back from here');
  });

  it('says what it does to a turn in progress rather than only to the list', async () => {
    await open();

    expect(text()).toContain('removed from every user’s tool picker');
  });

  it('confirms on the store, because there is no row to remove', async () => {
    const target = mcpServerFixture({ name: 'SAP Ledger' });
    await open(target);

    confirmButton().click();
    await fixture.whenStable();

    // Unlike a user deactivation there is no optimistic removal and no undo to thread
    // back to the screen: the delete is soft, and the row turns muted on the refetch.
    const request = backend.expectOne(`${MCPS_URL}/${target.id}`);
    expect(request.request.method).toBe('DELETE');
    request.flush(null, { status: 204, statusText: 'No Content' });
    await fixture.whenStable();

    expect(actions.deactivateTarget()).toBeNull();
  });

  it('closes without a request when it is cancelled', async () => {
    await open();

    host().querySelector<HTMLButtonElement>('[data-modal-autofocus]')?.click();
    await fixture.whenStable();

    expect(actions.deactivateTarget()).toBeNull();
    // `backend.verify()` in afterEach is the assertion that nothing was sent.
  });
});
