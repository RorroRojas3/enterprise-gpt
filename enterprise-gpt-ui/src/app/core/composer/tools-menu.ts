import { ChangeDetectionStrategy, Component, computed, inject, viewChild } from '@angular/core';
import { McpDto } from '@core/api/mcp.model';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { McpCredentialStore } from '@core/catalog/mcp-credential-store';
import { needsUserApiKey } from '@core/catalog/mcp-server';
import { StatusDot } from '@shared/badges/status-dot';
import { Switch } from '@shared/forms/switch';
import { BrandIcon } from '@shared/icons/brand-icon';
import { Icon } from '@shared/icons/icon';
import { MCP_FALLBACK_ICON, mcpBrandIcon } from '@shared/icons/mcp-icon';
import { Menu } from '@shared/menu/menu';
import { MenuItem } from '@shared/menu/menu-item';
import { MenuSeparator } from '@shared/menu/menu-separator';
import { MenuTriggerContent } from '@shared/menu/menu-trigger-content';
import { TurnSettingsStore } from './turn-settings-store';

/**
 * `<app-switch>` only draws each row's `aria-checked`: a native switch is not a valid `role="menu"`
 * child. A model without tools leaves the pill `aria-disabled`, so keyboard users still reach it.
 */
@Component({
  selector: 'app-tools-menu',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandIcon, Icon, Menu, MenuItem, MenuSeparator, MenuTriggerContent, StatusDot, Switch],
  templateUrl: './tools-menu.html',
  host: { class: 'd-inline-flex' },
})
export class ToolsMenu {
  protected readonly settings = inject(TurnSettingsStore);
  protected readonly mcps = inject(McpCatalogStore);
  protected readonly credentials = inject(McpCredentialStore);

  private readonly menu = viewChild.required(Menu);

  /**
   * The label is a count, the only visible sign that servers are armed, so below md it hides only
   * when it has nothing to report.
   */
  protected readonly labelClass = computed(() =>
    this.settings.effectiveMcpServerIds().length > 0 ? '' : 'd-none d-md-inline',
  );

  /** A field, not a template literal: `checkNoChanges` runs exhaustively here. */
  protected readonly fallbackIcon = MCP_FALLBACK_ICON;

  protected brandIcon(server: McpDto) {
    return mcpBrandIcon(server.iconKey);
  }

  protected needsKey(server: McpDto): boolean {
    return needsUserApiKey(server);
  }

  /**
   * Focuses the pill first: the row `Modal` would capture as its focus origin unmounts when the
   * dialog's first pointerdown dismisses the panel, leaving focus on `<body>` at close.
   */
  protected openKeyDialog(server: McpDto): void {
    this.menu().triggerElement().focus();
    this.credentials.open(server);
  }
}
