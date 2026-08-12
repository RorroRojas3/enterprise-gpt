import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpFixture, modelFixture } from '@testing/catalog';
import { ModelDto } from '@domain/api/model';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { TurnSettingsStore } from '@core/chat/turn-settings-store';
import { Composer } from './composer';

const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;
const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;

describe('Composer', () => {
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
    const fixture = TestBed.createComponent(Composer);
    document.body.append(fixture.nativeElement as HTMLElement);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      host,
      send: () => host.querySelector('.composer__send') as HTMLButtonElement,
      note: () => host.querySelector('.composer__note') as HTMLElement,
      async type(text: string) {
        const prompt = host.querySelector('.composer__prompt') as HTMLTextAreaElement;
        prompt.value = text;
        prompt.dispatchEvent(new Event('input', { bubbles: true }));
        await fixture.whenStable();
      },
    };
  }

  async function loadModels(models: ModelDto[]): Promise<void> {
    const loaded = TestBed.inject(ModelCatalogStore).ensureLoaded();
    backend.expectOne(MODELS_URL).flush(models);
    await loaded;
  }

  async function failModels(): Promise<void> {
    const failed = TestBed.inject(ModelCatalogStore).ensureLoaded();
    backend.expectOne(MODELS_URL).error(new ProgressEvent('error'));
    await failed;
  }

  it('disables send until both a model and text exist', async () => {
    const composer = await render();

    expect(composer.send().disabled).toBe(true);

    await composer.type('Hello there');
    // Text alone is not enough — no model has resolved yet.
    expect(composer.send().disabled).toBe(true);

    await loadModels([modelFixture({ isDefault: true })]);
    await composer.fixture.whenStable();
    expect(composer.send().disabled).toBe(false);

    await composer.type('   ');
    expect(composer.send().disabled).toBe(true);
  });

  it('renders frame 2j when the catalog fails: note, disabled send, unavailable pill', async () => {
    const composer = await render();
    await failModels();
    await composer.type('Still here');

    expect(composer.note().getAttribute('role')).toBe('status');
    expect(composer.note().textContent).toContain(
      'The model catalog failed to load — sending is disabled. Retry from the model menu.',
    );
    expect(composer.send().disabled).toBe(true);
    expect(composer.host.querySelector('.model-menu__pill')?.textContent).toContain(
      'Models unavailable',
    );
  });

  it("renders frame 2d's warning when a model change cleared the tool selection", async () => {
    const composer = await render();
    const capable = modelFixture({ isDefault: true });
    const limited = modelFixture({ name: 'GPT-5 Mini', isToolEnabled: false });
    await loadModels([capable, limited]);
    const serversLoaded = TestBed.inject(McpCatalogStore).ensureLoaded();
    const server = mcpFixture();
    backend.expectOne(MCPS_URL).flush([server]);
    await serversLoaded;

    const settings = TestBed.inject(TurnSettingsStore);
    settings.toggleMcpServer(server.id);
    settings.selectModel(limited.id);
    await composer.fixture.whenStable();

    expect(composer.note().textContent).toContain(
      "GPT-5 Mini can't call tools — your previous selection was cleared.",
    );

    settings.selectModel(capable.id);
    await composer.fixture.whenStable();
    expect(composer.note().textContent?.trim()).toBe('');
  });

  it('ships the unbuilt affordances as absent, not disabled', async () => {
    const composer = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await composer.fixture.whenStable();

    // Attach (US-801), project (US-307), mic (US-413), download (US-1502).
    const buttons = [...composer.host.querySelectorAll('button')];
    const labels = buttons.map(
      (button) => button.getAttribute('aria-label') ?? button.textContent?.trim() ?? '',
    );
    expect(labels.some((label) => /attach|project|mic|record|download/i.test(label))).toBe(false);
    expect(composer.host.querySelector('.model-menu__pill')).not.toBeNull();
    expect(composer.send()).not.toBeNull();
  });
});
