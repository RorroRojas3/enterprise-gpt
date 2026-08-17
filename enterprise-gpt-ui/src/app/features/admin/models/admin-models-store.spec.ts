import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { PROVIDER_ID } from '@domain/api/provider';
import { adminEvents } from '@core/events/admin-events';
import { sessionEvents } from '@core/events/session-events';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { modelFixture } from '@testing/catalog';
import { AdminModelsStore } from './admin-models-store';

const MODELS_URL = `${TEST_API_BASE_URL}/api/models/all`;

describe('AdminModelsStore (US-1207)', () => {
  let store: InstanceType<typeof AdminModelsStore>;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        AdminModelsStore,
      ],
    });

    store = TestBed.inject(AdminModelsStore);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
  });

  async function settle(): Promise<void> {
    TestBed.tick();
    await Promise.resolve();
  }

  async function loadWith(models: ReturnType<typeof modelFixture>[]): Promise<void> {
    store.load();
    await settle();
    backend.expectOne(MODELS_URL).flush(models);
    await settle();
  }

  it('reads the administrative route, which is the only one returning retired models', async () => {
    store.load();
    await settle();

    const request = backend.expectOne(MODELS_URL);
    expect(request.request.method).toBe('GET');
    // Unpaginated: no skip, no take, no name. That is what makes regime C honest.
    expect(request.request.params.keys()).toEqual([]);
    request.flush([modelFixture()]);
    await settle();

    expect(store.entities()).toHaveLength(1);
  });

  it('matches the provider name as well as the model name', async () => {
    await loadWith([
      modelFixture({ name: 'GPT-5 Enterprise', providerId: PROVIDER_ID.azureOpenAi }),
      modelFixture({ name: 'Claude Sonnet 4.5', providerId: PROVIDER_ID.anthropic }),
    ]);

    // Frame `5e`'s placeholder promises both, and neither string appears in the other's
    // name — so this can only pass if the provider is in the searchable text.
    store.search('anthropic');
    expect(store.results().map((model) => model.name)).toEqual(['Claude Sonnet 4.5']);

    store.search('gpt-5');
    expect(store.results().map((model) => model.name)).toEqual(['GPT-5 Enterprise']);
  });

  it('matches case-insensitively, on a substring', async () => {
    await loadWith([modelFixture({ name: 'GPT-5 Enterprise' })]);

    store.search('ENTERPRISE');
    expect(store.results()).toHaveLength(1);

    store.search('rprise');
    expect(store.results()).toHaveLength(1);
  });

  it('filters without touching the server, because the set on screen is complete', async () => {
    await loadWith([modelFixture(), modelFixture()]);

    store.search('nothing matches this');
    await settle();

    // Regime C: no second request, ever. `backend.verify()` is the assertion.
    expect(store.results()).toHaveLength(0);
    expect(store.hasNoMatches()).toBe(true);
  });

  it('tells an empty catalog apart from a search that found nothing', async () => {
    await loadWith([]);

    expect(store.isEmpty()).toBe(true);
    expect(store.hasNoMatches()).toBe(false);

    await loadWith([modelFixture({ name: 'GPT-5' })]);
    store.search('claude');

    expect(store.isEmpty()).toBe(false);
    expect(store.hasNoMatches()).toBe(true);
  });

  it('states the filtered figure against the total', async () => {
    await loadWith([modelFixture({ name: 'GPT-5' }), modelFixture({ name: 'Claude' })]);

    expect(store.countSummary()).toBe('2 models in the catalog');

    store.search('gpt');
    expect(store.countSummary()).toBe('Showing 1 of 2 models');
  });

  it('offers sort only over a set it holds in full', async () => {
    await loadWith([modelFixture()]);

    // Regime C's whole premise: one request is the complete set, so there is no
    // pagination to be partway through.
    expect(store.canSort()).toBe(true);
    expect(store.sortUnavailableReason()).toBeNull();
  });

  it('keeps the set authoritative while a mutation’s refetch is in flight', async () => {
    await loadWith([modelFixture(), modelFixture()]);

    TestBed.inject(Dispatcher).dispatch(adminEvents.modelCatalogChanged());
    await settle();
    const refetch = backend.expectOne(MODELS_URL);

    // `isFulfilled` alone would read false here, so US-902's sort control would vanish
    // and come back on every save, and `isResultPartial` would call a complete result
    // partial. The rows on screen are still the whole catalog.
    expect(store.canSort()).toBe(true);
    expect(store.sortUnavailableReason()).toBeNull();

    refetch.flush([modelFixture()]);
    await settle();
    expect(store.canSort()).toBe(true);
  });

  it('refetches when the catalog changes, rather than patching a row', async () => {
    await loadWith([modelFixture({ name: 'GPT-5', isDefault: true })]);

    TestBed.inject(Dispatcher).dispatch(adminEvents.modelCatalogChanged());
    await settle();

    // The event is void: saving a model with `isDefault` demotes a row no response
    // describes, so only a refetch can show the catalog as it now stands.
    backend
      .expectOne(MODELS_URL)
      .flush([
        modelFixture({ name: 'GPT-5', isDefault: false }),
        modelFixture({ name: 'Claude', isDefault: true }),
      ]);
    await settle();

    expect(store.entities().filter((model) => model.isDefault)).toHaveLength(1);
  });

  it('records a failure and recovers on retry', async () => {
    store.load();
    await settle();
    backend.expectOne(MODELS_URL).error(new ProgressEvent('error'));
    await settle();

    expect(store.error()?.kind).toBe('network');

    store.retry();
    await settle();
    backend.expectOne(MODELS_URL).flush([modelFixture()]);
    await settle();

    expect(store.error()).toBeNull();
    expect(store.entities()).toHaveLength(1);
  });

  it('cancels in flight and empties on sign-out', async () => {
    await loadWith([modelFixture()]);

    store.refresh();
    await settle();
    backend.expectOne(MODELS_URL);

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    await settle();

    // `withResetOnSignOut` clears the state; `takeUntil(signedOut$)` is what cancels the
    // request, which is why the store composes both.
    expect(store.entities()).toEqual([]);
    expect(store.query()).toBe('');
  });
});
