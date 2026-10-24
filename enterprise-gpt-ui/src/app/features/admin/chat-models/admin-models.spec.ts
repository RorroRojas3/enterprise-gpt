import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { getAllByRole, getAllByText, getByRole } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ModelDto } from '@shared/models/api/chat-model.model';
import { PROVIDER_ID } from '@shared/models/api/provider.model';
import { ModelActionsStore } from '@state/model-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { modelFixture } from '@testing/catalog';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { NARROW_VIEWPORT, resetMediaQueries, setMediaQuery } from '@testing/media-query';
import { menuItems, openMenu } from '@testing/overlays';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { AdminModels } from './admin-models';

const MODELS_URL = `${TEST_API_BASE_URL}/api/models/all`;

/** `SearchInput`'s 300 ms plus headroom; real timers, since a fake clock stalls `whenStable()`. */
const DEBOUNCE_MS = 450;

/** Somewhere to navigate to, so leaving the tab genuinely destroys the screen. */
@Component({
  selector: 'app-elsewhere',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
})
class Elsewhere {}

describe('AdminModels', () => {
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

  /** The labels of the row menu named `name`, opened. */
  async function menuLabels(name: string): Promise<(string | undefined)[]> {
    const menu = await openMenu(harness.fixture, name, element());
    return menuItems(menu).map((node) => node.textContent?.trim());
  }

  async function type(term: string): Promise<void> {
    const field = getByRole<HTMLInputElement>(element(), 'searchbox', {
      name: 'Search models by provider or name',
    });
    field.value = term;
    field.dispatchEvent(new Event('input'));
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

    const headers = getAllByRole(element(), 'columnheader').map((node) => node.textContent?.trim());
    expect(rowText()).toContain('GPT-5 Enterprise');
    expect(rowText()).toContain('Azure OpenAI');
    expect(rowText()).toContain('gpt-5-ent-eastus2');
    expect(rowText()).toContain('400k');
    expect(rowText()).toContain('32,768');
    expect(headers.join(' ')).toContain('Provider');
  });

  it('marks exactly the default model', async () => {
    await open([
      modelFixture({ name: 'GPT-5', isDefault: true }),
      modelFixture({ name: 'Claude', isDefault: false }),
    ]);

    const pills = getAllByText(element(), 'Default');
    expect(pills).toHaveLength(1);
    expect(pills[0]?.closest('[role="cell"]')?.textContent).toContain('GPT-5');
  });

  it('says whether tools are enabled in text, not only in colour', async () => {
    await open([
      modelFixture({ name: 'With tools', isToolEnabled: true }),
      modelFixture({ name: 'Without tools', isToolEnabled: false }),
    ]);

    expect(rowText()).toContain('Tools enabled');
    expect(rowText()).toContain('Tools not enabled');
  });

  it('renders a retired model muted, marked, and with no actions at all', async () => {
    await open([
      modelFixture({ name: 'Current' }),
      modelFixture({ name: 'Legacy', dateDeactivated: '2026-08-01T00:00:00Z' }),
    ]);

    expect(rowText()).toContain('Retired');
    // Dimmed: the second channel besides the Status column.
    expect(element().querySelectorAll('[role="cell"] .opacity-75')).toHaveLength(1);
    // One live row, one menu: there is no reactivate route to offer a retired one.
    expect(element().querySelectorAll('app-menu')).toHaveLength(1);
  });

  it('offers Edit, Set as default and Delete on a live row', async () => {
    await open([modelFixture({ name: 'GPT-5', isDefault: false })]);

    expect(await menuLabels('Actions for GPT-5')).toEqual(['Edit', 'Set as default', 'Delete']);
  });

  it('hides Set as default on the row that already is', async () => {
    await open([modelFixture({ name: 'GPT-5', isDefault: true })]);

    expect(await menuLabels('Actions for GPT-5')).toEqual(['Edit', 'Delete']);
  });

  /** A hidden default would leave every composer with no model to start a turn on. */
  it('hides Set as default on a row that is hidden from the picker', async () => {
    await open([modelFixture({ name: 'GPT-5', isDefault: false, isUserSelectable: false })]);

    expect(await menuLabels('Actions for GPT-5')).toEqual(['Edit', 'Delete']);
  });

  it('names an active row that is hidden from the picker as its own state', async () => {
    await open([
      modelFixture({ name: 'GPT-5' }),
      modelFixture({ name: 'Summarizer', isUserSelectable: false }),
    ]);

    // A third state, not a shade of Active: the row still serves a turn that names it by id.
    expect(rowText()).toContain('Hidden');
    expect(rowText()).toContain('Active');
  });

  it('filters on provider as well as name, without a second request', async () => {
    await open([
      modelFixture({ name: 'GPT-5 Enterprise', providerId: PROVIDER_ID.azureOpenAi }),
      modelFixture({ name: 'Claude Sonnet 4.5', providerId: PROVIDER_ID.anthropic }),
    ]);

    await type('anthropic');

    // `backend.verify()` proves the server was not asked again.
    expect(rowText()).toContain('Claude Sonnet 4.5');
    expect(rowText()).not.toContain('GPT-5 Enterprise');
  });

  it('names the term it could not match, and clears it', async () => {
    await open([modelFixture({ name: 'GPT-5 Enterprise' })]);
    await type('llama');

    expect(rowText()).toContain('No models match “llama”');

    getByRole(element(), 'button', { name: 'Clear search' }).click();
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

    // An empty table would show "no models" as if it were the answer.
    expect(element().querySelector('app-error-panel')).not.toBeNull();
    expect(element().querySelector('app-data-table')).toBeNull();
  });

  it('mounts both dialogs once, outside every branch', async () => {
    await open();

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

    // Coming back must not re-mount the dialog with `open()` still true.
    await open();

    expect(actions.formMode()).toBeNull();
    expect(element().querySelector('dialog[open]')).toBeNull();
  });

  it('renders a provider this build does not know without breaking the row', async () => {
    await open([modelFixture({ name: 'Mystery', providerId: 'not-a-seeded-provider' })]);

    expect(rowText()).toContain('not-a-seeded-provider');
    expect(rowText()).toContain('Mystery');
  });

  it('states the count over the rows on screen', async () => {
    await open([modelFixture({ name: 'GPT-5' }), modelFixture({ name: 'Claude' })]);
    expect(rowText()).toContain('2 models in the catalog');

    await type('gpt');
    expect(rowText()).toContain('Showing 1 of 2 models');
  });

  it('puts the row menu beside the card title rather than across its foot', async () => {
    await open([modelFixture({ name: 'GPT-5 Enterprise' })]);

    setMediaQuery(NARROW_VIEWPORT, true);
    await harness.fixture.whenStable();
    resetMediaQueries();

    // A lone kebab in the action foot is stretched the width of the card, which reads as a
    // broken button rather than a menu.
    expect(element().querySelector('[data-card-slot="trailing"] app-menu')).not.toBeNull();
    expect(element().querySelector('[data-card-slot="actions"] app-menu')).toBeNull();
  });

  it('labels every card meta value with its column header', async () => {
    await open([modelFixture({ deploymentName: 'gpt-5-ent-eastus2' })]);

    setMediaQuery(NARROW_VIEWPORT, true);
    await harness.fixture.whenStable();
    resetMediaQueries();

    const labels = [...element().querySelectorAll('dt')].map((node) => node.textContent);
    expect(labels).toEqual(['Deployment', 'Context', 'Max output']);
  });
});
