import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { modelFixture } from '@testing/catalog';
import { sessionEvents } from '@core/events/session-events';
import { ModelCatalogStore } from './model-catalog-store';

const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;

describe('ModelCatalogStore', () => {
  let store: InstanceType<typeof ModelCatalogStore>;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    store = TestBed.inject(ModelCatalogStore);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
  });

  it('issues nothing until the session bootstrap asks it to load', () => {
    backend.expectNone(MODELS_URL);
    expect(store.defaultModel()).toBeNull();
  });

  it('memoizes ensureLoaded and surfaces the default model', async () => {
    const preferred = modelFixture({ isDefault: true });
    const first = store.ensureLoaded();
    const second = store.ensureLoaded();

    backend.expectOne(MODELS_URL).flush([modelFixture(), preferred]);

    await expect(first).resolves.toBe(true);
    await expect(second).resolves.toBe(true);
    expect(store.models()).toHaveLength(2);
    expect(store.defaultModel()).toEqual(preferred);
    backend.expectNone(MODELS_URL);
  });

  it('lets only the newest reload write, however late the older response lands', async () => {
    const stale = store.reload();
    const staleRequest = backend.expectOne(MODELS_URL);

    const fresh = store.reload();
    backend.expectOne(MODELS_URL).flush([modelFixture({ name: 'Fresh', isDefault: true })]);
    await fresh;

    staleRequest.flush([modelFixture({ name: 'Stale', isDefault: true })]);
    await expect(stale).resolves.toBe(false);

    expect(store.defaultModel()?.name).toBe('Fresh');
    expect(store.isFulfilled()).toBe(true);
  });

  it('records a failure and recovers on reload', async () => {
    const failed = store.ensureLoaded();
    backend.expectOne(MODELS_URL).error(new ProgressEvent('error'));
    await expect(failed).resolves.toBe(false);

    expect(store.error()?.kind).toBe('network');

    const recovered = store.reload();
    backend.expectOne(MODELS_URL).flush([modelFixture({ isDefault: true })]);
    await expect(recovered).resolves.toBe(true);

    expect(store.error()).toBeNull();
    expect(store.models()).toHaveLength(1);
  });

  it('goes idle when sign-out cancels the flight, and can load again afterwards', async () => {
    const cancelled = store.ensureLoaded();
    backend.expectOne(MODELS_URL);

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
    await expect(cancelled).resolves.toBe(false);

    // The user's own action is not an error, and the memo is cleared so the
    // next session can load fresh.
    expect(store.error()).toBeNull();
    expect(store.models()).toEqual([]);

    const next = store.ensureLoaded();
    backend.expectOne(MODELS_URL).flush([modelFixture({ isDefault: true })]);
    await expect(next).resolves.toBe(true);
  });
});
