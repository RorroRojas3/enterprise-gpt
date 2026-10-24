import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { getAllByRole, getByRole, queryByRole, within } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ModelDto } from '@shared/models/api/chat-model.model';
import { PROVIDER_ID } from '@shared/models/api/provider.model';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { TurnSettingsStore } from '@state/turn-settings-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { modelFixture } from '@testing/catalog';
import { iconOf, menuItems } from '@testing/overlays';
import { ModelMenu, formatTokenCount } from './model-menu';

const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;

describe('formatTokenCount', () => {
  it('renders thousands with k, millions with M, and small sizes verbatim', () => {
    expect(formatTokenCount(400_000)).toBe('400k');
    expect(formatTokenCount(128_000)).toBe('128k');
    expect(formatTokenCount(32_768)).toBe('33k');
    expect(formatTokenCount(1_000_000)).toBe('1M');
    expect(formatTokenCount(1_500_000)).toBe('1.5M');
    expect(formatTokenCount(512)).toBe('512');
  });
});

describe('ModelMenu', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
  });

  async function render() {
    const fixture = TestBed.createComponent(ModelMenu);
    document.body.append(fixture.nativeElement as HTMLElement);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const trigger = () => getByRole<HTMLButtonElement>(host, 'button', { name: /^Model:/ });
    return {
      fixture,
      host,
      trigger,
      pillText: () => trigger().textContent?.trim() ?? '',
      panel: () => queryByRole(host, 'menu'),
      async open() {
        trigger().click();
        await fixture.whenStable();
      },
      options: () => getAllByRole<HTMLButtonElement>(host, 'menuitemradio'),
      /** The status line: the one row that is disabled rather than chosen. */
      status: () =>
        menuItems(getByRole(host, 'menu')).find(
          (item) => item.getAttribute('aria-disabled') === 'true',
        ),
    };
  }

  /** An option's caption line, which the mono figure sits inside. */
  function caption(option: HTMLElement): string | undefined {
    return option.querySelector('.font-monospace')?.parentElement?.textContent ?? undefined;
  }

  async function loadModels(models: ModelDto[]): Promise<void> {
    const loaded = TestBed.inject(ModelCatalogStore).ensureLoaded();
    backend.expectOne(MODELS_URL).flush(models);
    await loaded;
  }

  it('shows a loading pill until the catalog resolves a model', async () => {
    const menu = await render();

    expect(menu.pillText()).toContain('Loading models…');
  });

  it('says "No models" — not a perpetual loading state — when the catalog fulfills empty', async () => {
    const menu = await render();
    await loadModels([]);
    await menu.fixture.whenStable();

    expect(menu.pillText()).toContain('No models');

    await menu.open();
    expect(menu.status()?.textContent).toContain('No models are available.');
  });

  it('preselects the default model on the pill, with its mono context figure', async () => {
    const menu = await render();
    await loadModels([
      modelFixture({ name: 'GPT-5 Mini' }),
      modelFixture({ name: 'GPT-5 Enterprise', isDefault: true, contextWindowSize: 400_000 }),
    ]);
    await menu.fixture.whenStable();

    expect(menu.pillText()).toContain('GPT-5 Enterprise');
    expect(within(menu.trigger()).getByText('400k').classList.contains('font-monospace')).toBe(
      true,
    );
    expect(menu.trigger().getAttribute('aria-label')).toBe('Model: GPT-5 Enterprise');
  });

  it('renders frame 2b: dots, captions, one DEFAULT badge, and a check on the selection', async () => {
    const menu = await render();
    await loadModels([
      modelFixture({
        name: 'Claude Opus',
        providerId: PROVIDER_ID.anthropic,
        contextWindowSize: 200_000,
      }),
      modelFixture({
        name: 'GPT-5 Enterprise',
        isDefault: true,
        contextWindowSize: 400_000,
      }),
      modelFixture({
        name: 'GPT-5 Mini',
        isToolEnabled: false,
        contextWindowSize: 128_000,
      }),
    ]);
    await menu.open();

    const options = menu.options();
    expect(options).toHaveLength(3);
    expect(options.map((option) => option.getAttribute('role'))).toEqual([
      'menuitemradio',
      'menuitemradio',
      'menuitemradio',
    ]);

    // The mono class wraps the figure alone, so the caption is the figure's parent.
    const captions = options.map((option) => caption(option));
    expect(captions[0]).toBe('Anthropic · 200k context');
    expect(captions[1]).toBe('Azure OpenAI · 400k context');
    expect(captions[2]).toBe('Azure OpenAI · 128k context · no tool calling');
    expect(options[0]?.querySelector('.font-monospace')?.textContent).toBe('200k');

    const panel = getByRole(menu.host, 'menu');
    expect(within(panel).getAllByText('Default')).toHaveLength(1);
    expect(within(options[1]!).getByText('Default')).not.toBeNull();

    // The default is the current selection: checked, and marked with the check glyph.
    expect(options.map((option) => option.getAttribute('aria-checked'))).toEqual([
      'false',
      'true',
      'false',
    ]);
    expect(options.map((option) => iconOf(option))).toEqual([null, 'bi-check-lg', null]);
  });

  it('omits the provider segment and mutes the dot for a provider this build does not know', async () => {
    const menu = await render();
    await loadModels([
      modelFixture({
        name: 'Mystery Model',
        providerId: '99999999-9999-4999-8999-999999999999',
        isDefault: true,
        contextWindowSize: 100_000,
      }),
    ]);
    await menu.open();

    const option = menu.options()[0]!;
    expect(caption(option)).toBe('100k context');
    expect(option.querySelector('.bg-secondary')).not.toBeNull();
  });

  it('selects on click, closes, and returns focus to the pill', async () => {
    const menu = await render();
    const models = [
      modelFixture({ name: 'GPT-5 Enterprise', isDefault: true }),
      modelFixture({ name: 'Claude Opus', providerId: PROVIDER_ID.anthropic }),
    ];
    await loadModels(models);
    await menu.open();

    menu.options()[1]?.click();
    await menu.fixture.whenStable();

    expect(menu.panel()).toBeNull();
    expect(document.activeElement).toBe(menu.trigger());
    expect(menu.pillText()).toContain('Claude Opus');
    expect(TestBed.inject(TurnSettingsStore).selectedModel()?.id).toBe(models[1]?.id);
  });

  it('renders frame 2j inside the menu: failure line, Retry that reloads without closing', async () => {
    const menu = await render();
    const failed = TestBed.inject(ModelCatalogStore).ensureLoaded();
    backend.expectOne(MODELS_URL).error(new ProgressEvent('error'));
    await failed;
    await menu.fixture.whenStable();

    expect(menu.pillText()).toContain('Models unavailable');

    await menu.open();
    expect(menu.status()?.textContent).toContain('The model catalog failed to load.');

    const retry = getByRole(menu.host, 'menuitem', { name: 'Retry' });
    retry.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    retry.click();
    await menu.fixture.whenStable();

    // stayOpen while errored: the reload lands into the open panel.
    expect(menu.panel()).not.toBeNull();
    backend
      .expectOne(MODELS_URL)
      .flush([modelFixture({ name: 'GPT-5 Enterprise', isDefault: true })]);
    // The click owns the reload promise, so the test awaits the store's
    // in-flight memo — whenStable alone does not cover the store's own chain.
    await TestBed.inject(ModelCatalogStore).ensureLoaded();
    await menu.fixture.whenStable();

    expect(menu.panel()).not.toBeNull();
    expect(menu.options()).toHaveLength(1);
    expect(menu.pillText()).toContain('GPT-5 Enterprise');
  });
});
