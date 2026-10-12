import { ChangeDetectionStrategy, Component, inject, viewChild } from '@angular/core';
import { McpDto } from '@shared/models/api/mcp.model';
import { needsUserApiKey } from '@shared/utils/catalog/mcp-server';
import { MCP_FALLBACK_ICON, mcpBrandIcon } from '@shared/utils/icons/mcp-icon';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { McpCredentialStore } from '@state/mcp-credential-store';
import { TurnSettingsStore } from '@state/turn-settings-store';
import { BrandIcon } from '@shared/components/brand-icon/brand-icon';
import { Icon } from '@shared/components/icon/icon';
import { Menu } from '@shared/components/menu/menu';
import { StatusDot } from '@shared/components/status-dot/status-dot';
import { Switch } from '@shared/components/switch/switch';
import { MenuItem } from '@shared/directives/menu-item';
import { MenuSeparator } from '@shared/directives/menu-separator';
import { MenuTriggerContent } from '@shared/directives/menu-trigger-content';

/**
 * `<app-switch>` only draws each row's `aria-checked`: a native switch is not a valid `role="menu"`
 * child. A model without tools leaves the pill `aria-disabled`, so keyboard users still reach it.
 */
@Component({
  selector: 'app-tools-menu',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandIcon, Icon, Menu, MenuItem, MenuSeparator, MenuTriggerContent, StatusDot, Switch],
  templateUrl: './tools-menu.html',
  styleUrl: './tools-menu.scss',
})
export class ToolsMenu {
  protected readonly settings = inject(TurnSettingsStore);
  protected readonly mcps = inject(McpCatalogStore);
  protected readonly credentials = inject(McpCredentialStore);

  private readonly menu = viewChild.required(Menu);

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
