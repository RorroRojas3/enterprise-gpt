import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Dispatcher, Events } from '@ngrx/signals/events';
import { Subscription } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PROVIDER_ID } from '@domain/api/provider';
import { adminEvents } from '@core/events/admin-events';
import { sessionEvents } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { modelFixture } from '@testing/catalog';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { ModelActionsStore } from './model-actions-store';
import { ModelFormValue } from './model-form';

const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;

function formValue(overrides: Partial<ModelFormValue> = {}): ModelFormValue {
  return {
    providerId: PROVIDER_ID.anthropic,
    name: 'Claude Sonnet 4.5',
    deploymentName: 'claude-sonnet-4-5',
    description: 'General-purpose reasoning model.',
    contextWindowSize: '200000',
    maxOutputTokens: '64000',
    inputPricePerMillionTokens: '3',
    outputPricePerMillionTokens: '15',
    isToolEnabled: true,
    isReasoningEnabled: true,
    isDefault: false,
    ...overrides,
  };
}

const BAD_REQUEST = { status: 400, statusText: 'Bad Request' };

function validationProblem(errors: Record<string, string[]>): object {
  return { ...PROBLEM_FIXTURES.validationError, errors };
}

describe('ModelActionsStore (US-1207)', () => {
  let store: InstanceType<typeof ModelActionsStore>;
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

    store = TestBed.inject(ModelActionsStore);
    backend = TestBed.inject(HttpTestingController);

    changed = 0;
    subscription = TestBed.inject(Events)
      .on(adminEvents.modelCatalogChanged)
      .subscribe(() => (changed += 1));
  });

  afterEach(() => {
    subscription.unsubscribe();
    backend.verify();
  });

  function settle(): void {
    TestBed.tick();
  }

  it('creates a model and announces the catalog changed', () => {
    store.beginCreate();
    store.submitForm(formValue());
    settle();

    const request = backend.expectOne(MODELS_URL);
    expect(request.request.method).toBe('POST');
    // All eleven, including the three frame `5f` does not draw: `ModelMapper` assigns
    // every one unconditionally.
    expect(request.request.body).toEqual({
      providerId: PROVIDER_ID.anthropic,
      name: 'Claude Sonnet 4.5',
      deploymentName: 'claude-sonnet-4-5',
      description: 'General-purpose reasoning model.',
      contextWindowSize: 200_000,
      maxOutputTokens: 64_000,
      isToolEnabled: true,
      isReasoningEnabled: true,
      isDefault: false,
      inputPricePerMillionTokens: 3,
      outputPricePerMillionTokens: 15,
    });

    request.flush(modelFixture({ name: 'Claude Sonnet 4.5' }));
    settle();

    expect(changed).toBe(1);
    expect(store.formMode()).toBeNull();
    expect(toasts.success).toHaveBeenCalledWith('Claude Sonnet 4.5 added');
  });

  it('refuses a second submit while one is in flight', () => {
    store.beginCreate();
    store.submitForm(formValue());
    settle();
    const inFlight = backend.expectOne(MODELS_URL);

    // exhaustMap: a second submit while one is on the wire is a double-click, not a
    // second model — and a duplicate would take the "already exists" 400 the first one
    // had just made true.
    store.submitForm(formValue());
    settle();
    backend.expectNone(MODELS_URL);

    inFlight.flush(modelFixture());
    settle();
    expect(changed).toBe(1);
  });

  it('refuses to send a body it cannot build, rather than sending NaN', () => {
    store.beginCreate();
    store.submitForm(formValue({ contextWindowSize: '400k' }));
    settle();

    // `backend.verify()` is the assertion: nothing went out.
    expect(store.formBusy()).toBe(false);
  });

  it('keeps a validation problem inline and raises no toast', () => {
    store.beginCreate();
    const value = formValue({ deploymentName: 'taken' });
    store.submitForm(value);
    settle();

    backend
      .expectOne(MODELS_URL)
      .flush(
        validationProblem({ DeploymentName: ['That deployment name is in use.'] }),
        BAD_REQUEST,
      );
    settle();

    expect(store.formError()?.kind).toBe('validation-error');
    // The raw form value, not the body built from it, so the dialog's comparison is exact.
    expect(store.formRejectedValue()).toEqual(value);
    expect(store.formMode()).toBe('create');
    expect(toasts.fromError).not.toHaveBeenCalled();
    expect(toasts.show).not.toHaveBeenCalled();
  });

  it('never renders a rejection on a surface that is no longer the one it came from', () => {
    const target = modelFixture({ name: 'GPT-5' });
    store.beginEdit(target);
    store.submitForm(formValue({ name: 'GPT-5 renamed' }));
    settle();
    const inFlight = backend.expectOne(`${MODELS_URL}/${target.id}`);

    // The reader force-closes the edit before the save lands — a second Escape is
    // uncancelable in Chromium. Opening the *other* form in this window is no longer
    // possible at all (`beginCreate` refuses while `formBusy`), which is a stronger
    // guarantee than this test could make; what remains reachable is the closed surface.
    store.cancelForm();

    inFlight.flush(validationProblem({ Name: ['That name is in use.'] }), BAD_REQUEST);
    settle();

    // Nothing inline, because there is nothing on screen to render it against.
    expect(store.formError()).toBeNull();
    expect(store.formMode()).toBeNull();
    // With no surface to render it on, the validation problem is toasted by hand —
    // `fromError` silences validation problems on the assumption they render inline.
    expect(toasts.show).toHaveBeenCalledOnce();
  });

  it('sets a model as default from the row, echoing the ten fields it does not change', () => {
    const target = modelFixture({
      name: 'GPT-5',
      isDefault: false,
      isReasoningEnabled: true,
      inputPricePerMillionTokens: 1.25,
      outputPricePerMillionTokens: 10,
    });

    store.setAsDefault(target);
    settle();

    const request = backend.expectOne(`${MODELS_URL}/${target.id}`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toMatchObject({
      isDefault: true,
      isReasoningEnabled: true,
      inputPricePerMillionTokens: 1.25,
      outputPricePerMillionTokens: 10,
      deploymentName: target.deploymentName,
    });

    request.flush({ ...target, isDefault: true });
    settle();

    expect(changed).toBe(1);
  });

  it('does nothing when a model is already the default', () => {
    store.setAsDefault(modelFixture({ isDefault: true }));
    settle();
  });

  it('never sets formBusy for a row action, so a dialog opened meanwhile can be closed', () => {
    const target = modelFixture({ isDefault: false });
    store.setAsDefault(target);
    settle();
    const inFlight = backend.expectOne(`${MODELS_URL}/${target.id}`);

    // The reader opens Add model while the row's PUT is still on the wire. `formBusy`
    // disables every field, both footer buttons, *and* `Modal`'s Escape and backdrop
    // dismissal — so setting it here would trap them inside a dialog they opened,
    // waiting on a request that is not theirs (WCAG 2.1.2).
    store.beginCreate();
    expect(store.formBusy()).toBe(false);

    inFlight.flush({ ...target, isDefault: true });
    settle();
    expect(store.formBusy()).toBe(false);
  });

  it('does not let a row action swallow the reader’s own save', () => {
    const promoted = modelFixture({ isDefault: false });
    const edited = modelFixture({ name: 'Edited' });

    store.setAsDefault(promoted);
    settle();
    const rowRequest = backend.expectOne(`${MODELS_URL}/${promoted.id}`);

    store.beginEdit(edited);
    store.submitForm(formValue({ name: 'Edited twice' }));
    settle();

    // A separate rxMethod, so the row's PUT cannot occupy the form's `exhaustMap`. Shared,
    // this submit would have been dropped with the dialog left open and nothing said.
    const formRequest = backend.expectOne(`${MODELS_URL}/${edited.id}`);
    expect(store.formBusy()).toBe(true);

    rowRequest.flush({ ...promoted, isDefault: true });
    formRequest.flush(edited);
    settle();

    expect(changed).toBe(2);
  });

  it('keeps the dialog and its eleven fields when a save fails for any other reason', () => {
    store.beginCreate();
    const value = formValue();
    store.submitForm(value);
    settle();
    backend.expectOne(MODELS_URL).error(new ProgressEvent('error'));
    settle();

    // `retryInterceptor` retries GETs only, so a transient blip on Save is not retried —
    // and closing would discard eleven fields, one of them 1024 characters, for good.
    expect(store.formMode()).toBe('create');
    expect(toasts.fromError).toHaveBeenCalledOnce();
  });

  it('renders a create’s provider 404 at the field rather than as a toast', () => {
    store.beginCreate();
    const value = formValue();
    store.submitForm(value);
    settle();

    // `CreateModelAsync` throws exactly one NotFoundException, and it is
    // `EnsureProviderExistsAsync`'s — a provider this deployment has since deactivated.
    backend
      .expectOne(MODELS_URL)
      .flush(
        { ...PROBLEM_FIXTURES.resourceNotFound, detail: 'Provider not found.' },
        { status: 404, statusText: 'Not Found' },
      );
    settle();

    expect(store.formNotFound()).toBe('Provider not found.');
    expect(store.formRejectedValue()).toEqual(value);
    expect(store.formMode()).toBe('create');
    expect(toasts.fromError).not.toHaveBeenCalled();
  });

  it('toasts an edit’s 404, which could be the model rather than the provider', () => {
    const target = modelFixture();
    store.beginEdit(target);
    store.submitForm(formValue());
    settle();

    backend
      .expectOne(`${MODELS_URL}/${target.id}`)
      .flush(
        { ...PROBLEM_FIXTURES.resourceNotFound, detail: 'Model not found.' },
        { status: 404, statusText: 'Not Found' },
      );
    settle();

    // Ambiguous on this flow — somebody else may have retired the model — so it is said
    // rather than guessed at, and the dialog still keeps what was typed.
    expect(store.formNotFound()).toBeNull();
    expect(store.formMode()).toBe('edit');
    expect(toasts.fromError).toHaveBeenCalledOnce();
  });

  it('toasts a row action’s failure, because it has no form to render on', () => {
    const target = modelFixture();
    store.setAsDefault(target);
    settle();

    backend
      .expectOne(`${MODELS_URL}/${target.id}`)
      .flush(validationProblem({ Name: ['Something about the name.'] }), BAD_REQUEST);
    settle();

    // No form produced it, so there is nowhere inline for it to land.
    expect(store.formError()).toBeNull();
    expect(toasts.fromError).toHaveBeenCalledOnce();
  });

  it('retires a model and announces the catalog changed', () => {
    const target = modelFixture({ name: 'GPT-4.1 (legacy)' });
    store.beginDeactivate(target);
    expect(store.deactivateTarget()).toBe(target);

    store.confirmDeactivate();
    settle();

    const request = backend.expectOne(`${MODELS_URL}/${target.id}`);
    expect(request.request.method).toBe('DELETE');
    request.flush(null, { status: 204, statusText: 'No Content' });
    settle();

    // The same event as a save, for a reason no payload could carry: the server also
    // clears `IsDefault` on the row it retires.
    expect(changed).toBe(1);
    expect(store.deactivateTarget()).toBeNull();
    expect(toasts.success).toHaveBeenCalledWith('GPT-4.1 (legacy) retired');
  });

  it('lets retirements of different models overlap', () => {
    const first = modelFixture();
    const second = modelFixture();

    store.beginDeactivate(first);
    store.confirmDeactivate();
    settle();
    store.beginDeactivate(second);
    store.confirmDeactivate();
    settle();

    // mergeMap: independent rows. exhaustMap would silently drop the second.
    backend.expectOne(`${MODELS_URL}/${first.id}`).flush(null, { status: 204, statusText: '' });
    backend.expectOne(`${MODELS_URL}/${second.id}`).flush(null, { status: 204, statusText: '' });
    settle();

    expect(changed).toBe(2);
  });

  it('announces nothing when a retirement fails', () => {
    const target = modelFixture();
    store.beginDeactivate(target);
    store.confirmDeactivate();
    settle();

    backend.expectOne(`${MODELS_URL}/${target.id}`).error(new ProgressEvent('error'));
    settle();

    // Events carry server-confirmed facts only, so a failure announces nothing at all.
    expect(changed).toBe(0);
    expect(toasts.fromError).toHaveBeenCalledOnce();
  });

  it('refuses to open a surface on a row whose own request is in flight', () => {
    const target = modelFixture();
    store.setAsDefault(target);
    settle();

    store.beginEdit(target);
    store.beginDeactivate(target);

    expect(store.formMode()).toBeNull();
    expect(store.deactivateTarget()).toBeNull();

    backend.expectOne(`${MODELS_URL}/${target.id}`).flush(target);
    settle();
  });

  it('cancels everything on sign-out', () => {
    store.beginCreate();
    store.submitForm(formValue());
    settle();
    backend.expectOne(MODELS_URL);

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    settle();

    expect(store.formMode()).toBeNull();
    expect(store.formBusy()).toBe(false);
    expect(changed).toBe(0);
  });

  it('refuses to reopen a dialog it could not dismiss', () => {
    store.beginCreate();
    store.submitForm(formValue());
    settle();
    const inFlight = backend.expectOne(MODELS_URL);

    // The dialog is force-closed mid-flight — a second Escape is uncancelable in
    // Chromium — or the reader navigates away and `AdminModels` closes it on the way
    // back. `formBusy` survives both, by design.
    store.cancelForm();
    expect(store.formBusy()).toBe(true);

    // Reopening now would give a dialog whose fields, buttons and Escape handling are all
    // driven by that flag: no focusable element and no way out (WCAG 2.1.2).
    store.beginCreate();
    expect(store.formMode()).toBeNull();

    store.beginEdit(modelFixture());
    expect(store.formMode()).toBeNull();

    inFlight.flush(modelFixture());
    settle();

    // And it opens again the moment the request settles.
    store.beginCreate();
    expect(store.formMode()).toBe('create');
  });

  it('does not clear formBusy when the dialog is closed mid-flight', () => {
    store.beginCreate();
    store.submitForm(formValue());
    settle();
    const inFlight = backend.expectOne(MODELS_URL);

    store.cancelForm();

    // The flag tracks the request, not the dialog: a reopened form must not report itself
    // ready while `exhaustMap` is still occupied and would swallow the next submit.
    expect(store.formBusy()).toBe(true);

    inFlight.flush(modelFixture());
    settle();
    expect(store.formBusy()).toBe(false);
  });
});
