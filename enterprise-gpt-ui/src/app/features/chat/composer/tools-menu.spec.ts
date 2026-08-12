import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpFixture, modelFixture } from '@testing/catalog';
import { McpDto } from '@domain/api/mcp';
import { ModelDto } from '@domain/api/model';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { TurnSettingsStore } from '@core/chat/turn-settings-store';
import { ToolsMenu } from './tools-menu';

const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;
const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;

describe('ToolsMenu', () => {
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
    const fixture = TestBed.createComponent(ToolsMenu);
    document.body.append(fixture.nativeElement as HTMLElement);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      host,
      trigger: () => host.querySelector('.menu__trigger') as HTMLButtonElement,
      pillText: () => host.querySelector('.tools-menu__pill')?.textContent?.trim() ?? '',
      panel: () => host.querySelector('[role="menu"]'),
      rows: () => [...host.querySelectorAll<HTMLButtonElement>('.tools-menu__option')],
      async open() {
        (host.querySelector('.menu__trigger') as HTMLButtonElement).click();
        await fixture.whenStable();
      },
      async toggle(index: number) {
        const row = [...host.querySelectorAll<HTMLButtonElement>('.tools-menu__option')][index];
        row?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
        row?.click();
        await fixture.whenStable();
      },
    };
  }

  async function loadModels(models: ModelDto[]): Promise<void> {
    const loaded = TestBed.inject(ModelCatalogStore).ensureLoaded();
    backend.expectOne(MODELS_URL).flush(models);
    await loaded;
  }

  async function loadServers(servers: McpDto[]): Promise<void> {
    const loaded = TestBed.inject(McpCatalogStore).ensureLoaded();
    backend.expectOne(MCPS_URL).flush(servers);
    await loaded;
  }

  it('renders frame 2c: heading, checkbox rows by name only, and the stay-open footnote', async () => {
    const menu = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await loadServers([
      mcpFixture({ name: 'Jira Cloud' }),
      mcpFixture({ name: 'Confluence Search' }),
    ]);
    await menu.open();

    expect(menu.host.querySelector('.tools-menu__heading')?.textContent?.trim()).toBe(
      'Tool servers',
    );

    const rows = menu.rows();
    expect(rows).toHaveLength(2);
    expect(rows.map((row) => row.getAttribute('role'))).toEqual([
      'menuitemcheckbox',
      'menuitemcheckbox',
    ]);
    expect(rows[0]?.textContent?.trim()).toBe('Jira Cloud');
    // No key column exists to render: McpDto carries no key (deliberate deviation).
    expect(menu.host.querySelector('.font-mono')).toBeNull();

    expect(menu.host.querySelector('.tools-menu__footnote')?.textContent?.trim()).toBe(
      'Stays open while you toggle — applies to the next turn.',
    );
  });

  it('toggles independently while the panel stays open, and counts the label up and down', async () => {
    const menu = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await loadServers([mcpFixture({ name: 'Jira Cloud' }), mcpFixture({ name: 'Weather' })]);

    expect(menu.pillText()).toBe('Tools');
    await menu.open();

    await menu.toggle(0);
    expect(menu.panel()).not.toBeNull();
    expect(menu.rows()[0]?.getAttribute('aria-checked')).toBe('true');
    expect(menu.rows()[1]?.getAttribute('aria-checked')).toBe('false');
    expect(menu.pillText()).toBe('1 Tool');

    await menu.toggle(1);
    expect(menu.panel()).not.toBeNull();
    expect(menu.pillText()).toBe('2 Tools');

    await menu.toggle(0);
    expect(menu.rows()[0]?.getAttribute('aria-checked')).toBe('false');
    expect(menu.rows()[1]?.getAttribute('aria-checked')).toBe('true');
    expect(menu.pillText()).toBe('1 Tool');

    // The toggled row keeps focus: the panel never unmounted around it.
    expect(document.activeElement).toBe(menu.rows()[0]);
  });

  it('renders frame 2d for a non-tool model: dimmed, aria-disabled, inert, label reset', async () => {
    const menu = await render();
    const capable = modelFixture({ isDefault: true });
    const limited = modelFixture({ name: 'GPT-5 Mini', isToolEnabled: false });
    await loadModels([capable, limited]);
    const server = mcpFixture();
    await loadServers([server]);

    const settings = TestBed.inject(TurnSettingsStore);
    settings.toggleMcpServer(server.id);
    await menu.fixture.whenStable();
    expect(menu.pillText()).toBe('1 Tool');

    settings.selectModel(limited.id);
    await menu.fixture.whenStable();

    expect(menu.pillText()).toBe('Tools');
    expect(menu.trigger().getAttribute('aria-disabled')).toBe('true');
    expect(menu.trigger().hasAttribute('disabled')).toBe(false);
    expect(
      menu.host
        .querySelector('.tools-menu__pill')
        ?.classList.contains('tools-menu__pill--disabled'),
    ).toBe(true);

    menu.trigger().click();
    await menu.fixture.whenStable();
    expect(menu.panel()).toBeNull();

    expect(settings.streamSelection()).toEqual({ modelId: limited.id, mcpServerIds: [] });
  });

  it('explains an empty tool-server list rather than rendering a bare panel', async () => {
    const menu = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await loadServers([]);
    await menu.open();

    expect(menu.host.querySelector('.tools-menu__status')?.textContent).toContain(
      'No tool servers are available to you.',
    );
    expect(menu.rows()).toHaveLength(0);
  });
});
