import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ModelActionsStore } from '@core/catalog/model-actions-store';
import { ToastStore } from '@core/notifications/toast-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { modelFixture } from '@testing/catalog';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { DeactivateModelDialog } from './deactivate-model-dialog';

const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;

describe('DeactivateModelDialog (US-1207)', () => {
  let backend: HttpTestingController;
  let fixture: ComponentFixture<DeactivateModelDialog>;
  let actions: InstanceType<typeof ModelActionsStore>;

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
    actions = TestBed.inject(ModelActionsStore);

    fixture = TestBed.createComponent(DeactivateModelDialog);
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

  async function open(model = modelFixture({ name: 'GPT-4.1 (legacy)' })): Promise<void> {
    actions.beginDeactivate(model);
    await fixture.whenStable();
  }

  it('names the model and styles the destructive action red', async () => {
    await open();

    expect(text()).toContain('GPT-4.1 (legacy)');
    expect(confirmButton().textContent?.trim()).toBe('Delete model');
  });

  it('lands initial focus on Cancel', async () => {
    await open();

    // Enter on a freshly opened destructive confirmation must retire nothing.
    const autofocus = host().querySelector('[data-modal-autofocus]');
    expect(autofocus?.textContent?.trim()).toBe('Cancel');
  });

  it('says what a soft delete does, and does not', async () => {
    await open();

    // The transcripts keep the model; the picker loses it. Both are true, and a
    // confirmation that implied otherwise would be the client inventing a guarantee.
    expect(text()).toContain('removed from every user’s model picker');
    expect(text()).toContain('stays on the conversations that already used it');
  });

  it('warns that retiring the default leaves the deployment with none', async () => {
    await open(modelFixture({ name: 'GPT-5', isDefault: true }));

    // `DeactivateModelAsync` clears `IsDefault` in the same statement and promotes
    // nothing, so this states what is about to be true rather than cautioning.
    expect(text()).toContain('leaves the deployment with no default');
  });

  it('says nothing about the default when the model is not one', async () => {
    await open(modelFixture({ isDefault: false }));

    expect(text()).not.toContain('leaves the deployment with no default');
  });

  it('confirms on the store, because there is no row to remove', async () => {
    const target = modelFixture({ name: 'GPT-4.1 (legacy)' });
    await open(target);

    confirmButton().click();
    await fixture.whenStable();

    // Nothing is optimistic: the delete is soft, the row stays and turns muted on the
    // refetch, so there is no undo for the screen to hand back.
    backend
      .expectOne(`${MODELS_URL}/${target.id}`)
      .flush(null, { status: 204, statusText: 'No Content' });
    await fixture.whenStable();

    expect(actions.deactivateTarget()).toBeNull();
  });

  it('sends nothing when it is dismissed', async () => {
    await open();

    actions.cancelDeactivate();
    await fixture.whenStable();

    // `backend.verify()` is the assertion.
    expect(actions.deactivateTarget()).toBeNull();
  });
});
