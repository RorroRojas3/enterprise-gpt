import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ModelDto } from '@domain/api/model';
import { PROVIDER_ID } from '@domain/api/provider';
import { ModelActionsStore } from '@core/catalog/model-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { modelFixture } from '@testing/catalog';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { AdminModels } from './admin-models';

const MODELS_URL = `${TEST_API_BASE_URL}/api/models/all`;

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

describe('AdminModels (US-1207)', () => {
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
          { path: 'admin/models', component: AdminModels },
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

  async function open(models: ModelDto[] = [modelFixture()]): Promise<void> {
    await harness.navigateByUrl('/admin/models', AdminModels);
    backend.expectOne(MODELS_URL).flush(models);
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

  it('renders the columns frame `5e` draws', async () => {
    await open([
      modelFixture({
        name: 'GPT-5 Enterprise',
        providerId: PROVIDER_ID.azureOpenAi,
        deploymentName: 'gpt-5-ent-eastus2',
        contextWindowSize: 400_000,
        maxOutputTokens: 32_768,
        isToolEnabled: true,
      }),
    ]);

    const headers = [...element().querySelectorAll('.data-table__head *, th')].map((node) =>
      node.textContent?.trim(),
    );
    expect(rowText()).toContain('GPT-5 Enterprise');
    expect(rowText()).toContain('Azure OpenAI');
    expect(rowText()).toContain('gpt-5-ent-eastus2');
    // Abbreviated, then thousands-separated — the two treatments frame `5e` uses.
    expect(rowText()).toContain('400k');
    expect(rowText()).toContain('32,768');
    expect(headers.join(' ')).toContain('Provider');
  });

  it('marks exactly the default model', async () => {
    await open([
      modelFixture({ name: 'GPT-5', isDefault: true }),
      modelFixture({ name: 'Claude', isDefault: false }),
    ]);

    const pills = [...element().querySelectorAll('.admin-models__default')];
    expect(pills).toHaveLength(1);
    expect(pills[0]?.closest('.admin-models__identity')?.textContent).toContain('GPT-5');
  });

  it('says whether tools are enabled in text, not only in colour', async () => {
    await open([
      modelFixture({ name: 'With tools', isToolEnabled: true }),
      modelFixture({ name: 'Without tools', isToolEnabled: false }),
    ]);

    // WCAG 1.4.1: a tick and a dash in two colours is one channel, and "Tools" over a
    // bare glyph reads as nothing at all to a screen reader.
    expect(rowText()).toContain('Tools enabled');
    expect(rowText()).toContain('Tools not enabled');
  });

  it('renders a retired model muted, marked, and with no actions at all', async () => {
    await open([
      modelFixture({ name: 'Current' }),
      modelFixture({ name: 'Legacy', dateDeactivated: '2026-08-01T00:00:00Z' }),
    ]);

    expect(rowText()).toContain('Retired');
    expect(element().querySelectorAll('.admin-models__identity--retired')).toHaveLength(1);
    // There is no reactivate route, so every item would be an affordance that cannot
    // work — the shown-and-disabled pattern US-203 rejected. One live row, one menu.
    expect(element().querySelectorAll('app-menu')).toHaveLength(1);
  });

  it('offers Edit, Set as default and Delete on a live row', async () => {
    await open([modelFixture({ name: 'GPT-5', isDefault: false })]);

    element().querySelector<HTMLButtonElement>('app-menu button')?.click();
    await harness.fixture.whenStable();

    const items = [...element().querySelectorAll('[appMenuItem]')].map((node) =>
      node.textContent?.trim(),
    );
    expect(items).toEqual(['Edit', 'Set as default', 'Delete']);
  });

  it('hides Set as default on the row that already is', async () => {
    await open([modelFixture({ name: 'GPT-5', isDefault: true })]);

    element().querySelector<HTMLButtonElement>('app-menu button')?.click();
    await harness.fixture.whenStable();

    const items = [...element().querySelectorAll('[appMenuItem]')].map((node) =>
      node.textContent?.trim(),
    );
    expect(items).toEqual(['Edit', 'Delete']);
  });

  it('filters on provider as well as name, without a second request', async () => {
    await open([
      modelFixture({ name: 'GPT-5 Enterprise', providerId: PROVIDER_ID.azureOpenAi }),
      modelFixture({ name: 'Claude Sonnet 4.5', providerId: PROVIDER_ID.anthropic }),
    ]);

    await type('anthropic');

    // Regime C: the whole catalog is already here, so the filter is exact and the server
    // is never asked again. `backend.verify()` is the second half of that claim.
    expect(rowText()).toContain('Claude Sonnet 4.5');
    expect(rowText()).not.toContain('GPT-5 Enterprise');
  });

  it('names the term it could not match, and clears it', async () => {
    await open([modelFixture({ name: 'GPT-5 Enterprise' })]);
    await type('llama');

    expect(rowText()).toContain('No models match “llama”');

    element().querySelector<HTMLButtonElement>('[emptyStateAction]')?.click();
    await harness.fixture.whenStable();

    expect(rowText()).toContain('GPT-5 Enterprise');
  });

  it('tells an empty catalog apart from a search that matched nothing', async () => {
    await open([]);

    expect(rowText()).toContain('No models yet');
    expect(rowText()).not.toContain('No models match');
  });

  it('offers no sort control, because frame `5e` draws none', async () => {
    await open([modelFixture(), modelFixture()]);

    expect(element().querySelectorAll('[aria-sort]')).toHaveLength(0);
    expect(rowText()).not.toContain('Sort');
  });

  it('replaces the table with the problem when the request is refused', async () => {
    await harness.navigateByUrl('/admin/models', AdminModels);
    backend
      .expectOne(MODELS_URL)
      .flush(PROBLEM_FIXTURES.permissionRequired, { status: 403, statusText: 'Forbidden' });
    await harness.fixture.whenStable();

    // The rows on screen would belong to a request that failed, so leaving an empty table
    // up would show "no models" as if it were the answer.
    expect(element().querySelector('app-error-panel')).not.toBeNull();
    expect(element().querySelector('app-data-table')).toBeNull();
  });

  it('mounts both dialogs once, outside every branch', async () => {
    await open();

    // The dialog is the reader's own state: a re-render of the controls above it must not
    // destroy the form they are filling in.
    expect(element().querySelectorAll('app-model-form-dialog')).toHaveLength(1);
    expect(element().querySelectorAll('app-deactivate-model-dialog')).toHaveLength(1);
  });

  it('does not reopen a dialog left standing when the tab is revisited', async () => {
    await open();
    const actions = TestBed.inject(ModelActionsStore);
    actions.beginCreate();
    await harness.fixture.whenStable();
    expect(element().querySelector('dialog[open]')).not.toBeNull();

    // Leave the tab without closing it — the store is root-scoped, so `formMode` survives.
    await harness.navigateByUrl('/elsewhere');
    await harness.fixture.whenStable();
    expect(actions.formMode()).toBe('create');

    // Coming back used to re-mount the dialog with `open()` still true, and `Modal` would
    // `showModal()` a surface the reader never asked for.
    await open();

    expect(actions.formMode()).toBeNull();
    expect(element().querySelector('dialog[open]')).toBeNull();
  });

  it('renders a provider this build does not know without breaking the row', async () => {
    await open([modelFixture({ name: 'Mystery', providerId: 'not-a-seeded-provider' })]);

    // A provider seeded server-side after this build. There is no `GET api/providers` to
    // resolve it, so the row names the id rather than showing a blank cell.
    expect(rowText()).toContain('not-a-seeded-provider');
    expect(rowText()).toContain('Mystery');
  });

  it('states the count over the rows on screen', async () => {
    await open([modelFixture({ name: 'GPT-5' }), modelFixture({ name: 'Claude' })]);
    expect(rowText()).toContain('2 models in the catalog');

    await type('gpt');
    expect(rowText()).toContain('Showing 1 of 2 models');
  });
});
