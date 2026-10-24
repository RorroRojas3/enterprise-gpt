import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { getByRole, getByText, queryByRole, within } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ModelDto } from '@shared/models/api/chat-model.model';
import { McpDto } from '@shared/models/api/mcp.model';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { McpCredentialStore } from '@state/mcp-credential-store';
import { TurnSettingsStore } from '@state/turn-settings-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpFixture, modelFixture } from '@testing/catalog';
import { iconOf, menuItems } from '@testing/overlays';
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
    const trigger = () => getByRole<HTMLButtonElement>(host, 'button', { name: /^Tool servers:/ });
    const panel = () => queryByRole(host, 'menu');
    /**
     * One per server: not the status line, which is disabled, nor a stored key's own control,
     * which carries its own name.
     */
    const rows = () =>
      panel() === null
        ? []
        : menuItems(panel()!).filter(
            (item) =>
              item.getAttribute('aria-disabled') !== 'true' && !item.hasAttribute('aria-label'),
          );
    return {
      fixture,
      host,
      trigger,
      /** The visible face: the trigger's own name is `label`, not its text. */
      pillText: () => trigger().textContent?.trim() ?? '',
      /** The count, which below md is the only label left, and only once something is armed. */
      pillLabel: () => getByText(trigger(), /Tools?$/),
      panel,
      rows,
      switches: () => rows().flatMap((row) => [...row.querySelectorAll('app-switch')]),
      async open() {
        trigger().click();
        await fixture.whenStable();
      },
      async toggle(index: number) {
        const row = rows()[index];
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

  it('renders frame 2c: heading, switch rows by name only, and the stay-open footnote', async () => {
    const menu = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await loadServers([
      mcpFixture({ name: 'Jira Cloud' }),
      mcpFixture({ name: 'Confluence Search' }),
    ]);
    await menu.open();

    // Visual only: the panel's own name already says it.
    expect(within(menu.panel()!).getByText('Tool servers').getAttribute('aria-hidden')).toBe(
      'true',
    );

    const rows = menu.rows();
    expect(rows).toHaveLength(2);
    expect(rows.map((row) => row.getAttribute('role'))).toEqual([
      'menuitemcheckbox',
      'menuitemcheckbox',
    ]);
    expect(rows[0]?.textContent?.trim()).toBe('Jira Cloud');
    // No key column exists to render: McpDto carries no key (deliberate deviation).
    expect(menu.host.querySelector('.font-monospace')).toBeNull();

    // The switch is decoration; the row keeps the semantics, because a native
    // `<input role="switch">` is not a valid child of `role="menu"`.
    expect(menu.switches()).toHaveLength(2);
    expect(menu.host.querySelector('[role="switch"]')).toBeNull();
    expect(menu.host.querySelector('[role="menu"] input')).toBeNull();

    expect(
      within(menu.panel()!)
        .getByText('Stays open while you toggle — applies to the next turn.')
        .getAttribute('aria-hidden'),
    ).toBe('true');
  });

  it('toggles independently while the panel stays open, and counts the label up and down', async () => {
    const menu = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await loadServers([mcpFixture({ name: 'Jira Cloud' }), mcpFixture({ name: 'Weather' })]);

    expect(menu.pillText()).toBe('Tools');
    // Nothing to report below md, so the label gives way there.
    expect(menu.pillLabel().classList).toContain('d-none');
    await menu.open();

    await menu.toggle(0);
    expect(menu.panel()).not.toBeNull();
    expect(menu.rows()[0]?.getAttribute('aria-checked')).toBe('true');
    expect(menu.rows()[1]?.getAttribute('aria-checked')).toBe('false');
    // How the switch draws itself is `switch.spec.ts`'s business; what this menu owes is
    // that the row's `aria-checked` is what moved, since that is the state AT reads.
    expect(menu.pillText()).toBe('1 Tool');

    await menu.toggle(1);
    expect(menu.panel()).not.toBeNull();
    expect(menu.pillText()).toBe('2 Tools');
    // A count is the only visible sign that servers are armed, so it stays at every width.
    expect(menu.pillLabel().classList).not.toContain('d-none');

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

    menu.trigger().click();
    await menu.fixture.whenStable();
    expect(menu.panel()).toBeNull();

    expect(settings.streamSelection()).toEqual({ modelId: limited.id, mcpServerIds: [] });
  });

  it('draws each server its brand mark, and bi-plug for everything else', async () => {
    const menu = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await loadServers([
      mcpFixture({ name: 'Microsoft Learn', iconKey: 'microsoft' }),
      mcpFixture({ name: 'Context7', iconKey: 'context7' }),
      mcpFixture({ name: 'Jira Cloud', iconKey: null }),
      // A key a newer client wrote. It degrades to the generic glyph rather than
      // rendering an empty box, which is what an unknown `<use>` target would.
      mcpFixture({ name: 'Something New', iconKey: 'future-brand' }),
    ]);
    await menu.open();

    expect(menu.rows().map((row) => iconOf(row))).toEqual([
      'brand-microsoft',
      'brand-context7',
      'bi-plug',
      'bi-plug',
    ]);
  });

  it('explains an empty tool-server list rather than rendering a bare panel', async () => {
    const menu = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await loadServers([]);
    await menu.open();

    const [status, ...rest] = menuItems(menu.panel()!);
    expect(status?.getAttribute('aria-disabled')).toBe('true');
    expect(status?.textContent).toContain('No tool servers are available to you.');
    expect(rest).toHaveLength(0);
  });

  it('renders a server awaiting the user’s key as a menuitem, not a checkbox', async () => {
    const menu = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await loadServers([
      mcpFixture({ name: 'GitHub', requiresUserApiKey: true, hasUserApiKey: false }),
    ]);
    await menu.open();

    const row = menu.rows()[0]!;
    // A checkbox role would promise a checked state the click does not change.
    expect(row.getAttribute('role')).toBe('menuitem');
    expect(row.hasAttribute('aria-checked')).toBe(false);
    expect(row.textContent).toContain('Key required');
    expect(menu.switches()).toHaveLength(0);
  });

  it('opens the key dialog instead of selecting a server that has no key', async () => {
    const menu = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await loadServers([
      mcpFixture({ name: 'GitHub', requiresUserApiKey: true, hasUserApiKey: false }),
    ]);
    await menu.open();
    await menu.toggle(0);

    expect(TestBed.inject(McpCredentialStore).target()?.name).toBe('GitHub');
    expect(TestBed.inject(TurnSettingsStore).effectiveMcpServerIds()).toEqual([]);
  });

  it('toggles a server whose key is stored, and shows the hint as the way to replace it', async () => {
    const menu = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    const server = mcpFixture({
      name: 'GitHub',
      requiresUserApiKey: true,
      hasUserApiKey: true,
      apiKeyHint: 'wxyz',
    });
    await loadServers([server]);
    await menu.open();

    expect(menu.rows()[0]!.getAttribute('role')).toBe('menuitemcheckbox');

    await menu.toggle(0);
    expect(TestBed.inject(TurnSettingsStore).effectiveMcpServerIds()).toEqual([server.id]);

    const manage = getByRole(menu.panel()!, 'menuitem', {
      name: /^Change your API key for GitHub/,
    });
    expect(manage.textContent?.trim()).toBe('••••wxyz');
    // Its own menu item, so the arrow keys reach it — a plain nested button would be
    // invalid HTML and invisible to the menu's roving focus.
    expect(manage.getAttribute('role')).toBe('menuitem');
    expect(manage.getAttribute('tabindex')).toBe('-1');

    manage.click();
    await menu.fixture.whenStable();

    // The dialog opened and the row stayed selected: this item is a sibling of the
    // toggle, not nested inside it.
    expect(TestBed.inject(McpCredentialStore).target()?.id).toBe(server.id);
    expect(TestBed.inject(TurnSettingsStore).effectiveMcpServerIds()).toEqual([server.id]);
  });
});
