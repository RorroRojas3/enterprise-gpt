import { ChangeDetectionStrategy, Component, inject, viewChild } from '@angular/core';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { McpCredentialStore } from '@core/catalog/mcp-credential-store';
import { TurnSettingsStore } from '@core/chat/turn-settings-store';
import { McpDto, needsUserApiKey } from '@domain/api/mcp';
import { StatusDot } from '@shared/badge/status-dot/status-dot';
import { BrandIcon } from '@shared/icon/brand-icon';
import { Icon } from '@shared/icon/icon';
import { MCP_FALLBACK_ICON, mcpBrandIcon } from '@shared/icon/mcp-icon';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';
import { MenuSeparator } from '@shared/overlay/menu/menu-separator';
import { MenuTriggerContent } from '@shared/overlay/menu/menu-trigger-content';
import { Switch } from '@shared/switch/switch';

/**
 * The composer's Tools pill and its multi-select picker (US-403, frame `2c`).
 *
 * The rows are `menuitemcheckbox` items that toggle in place — the panel stays
 * open while toggling, per the frame's own footnote. The switch they carry is
 * `<app-switch>`, which is deliberately decoration only: a native
 * `<input type="checkbox" role="switch">` is not a valid child of `role="menu"`,
 * so the row's button keeps the state in `aria-checked` and the switch just
 * draws it (US-417).
 *
 * A model that cannot call tools disables the whole pill (frame `2d`) —
 * `aria-disabled` and dimmed, never natively disabled, so keyboard users can
 * still reach it and hear why the composer's warning line names the model.
 *
 * Each row leads with the server's brand mark where an administrator picked one
 * (US-418); everything else gets `bi-plug`, the same glyph the admin area's MCP
 * tab uses.
 *
 * A server that needs the user's own API key renders as a plain `menuitem` that
 * opens the key dialog: it cannot be toggled until a key is stored, and a row
 * announcing a checked state its click does not change would be a lie. The
 * design's mono key column now has something to show — the stored key's last
 * four characters, which double as the control for replacing it.
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
   * Moves focus to the pill before the dialog opens, so closing it returns focus somewhere
   * that still exists.
   *
   * The row would otherwise be the focus origin `Modal` captures, and it does not survive:
   * the first pointerdown inside the dialog is outside this menu's host, which dismisses the
   * panel and unmounts the row — leaving `restoreFocus` with a disconnected element and
   * focus on `<body>`. The pill is the same anchor US-307's "Move to project" hands its
   * picker.
   */
  protected openKeyDialog(server: McpDto): void {
    this.menu().triggerElement().focus();
    this.credentials.open(server);
  }
}
