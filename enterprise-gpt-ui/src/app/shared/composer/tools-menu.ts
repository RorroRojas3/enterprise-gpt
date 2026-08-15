import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { TurnSettingsStore } from '@core/chat/turn-settings-store';
import { Icon } from '@shared/icon/icon';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';
import { MenuSeparator } from '@shared/overlay/menu/menu-separator';
import { MenuTriggerContent } from '@shared/overlay/menu/menu-trigger-content';

/**
 * The composer's Tools pill and its multi-select picker (US-403, frame `2c`).
 *
 * The rows are `menuitemcheckbox` items that toggle in place — the panel stays
 * open while toggling, per the frame's own footnote. The check visual is an
 * icon glyph pair rather than a native `<input type="checkbox">`, which is not
 * a valid child of `role="menu"`.
 *
 * A model that cannot call tools disables the whole pill (frame `2d`) —
 * `aria-disabled` and dimmed, never natively disabled, so keyboard users can
 * still reach it and hear why the composer's warning line names the model.
 *
 * The rows omit the design's mono key column deliberately: `McpDto` carries
 * `{ id, name, description }` and no key exists to show.
 */
@Component({
  selector: 'app-tools-menu',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, Menu, MenuItem, MenuSeparator, MenuTriggerContent],
  templateUrl: './tools-menu.html',
  styleUrl: './tools-menu.scss',
})
export class ToolsMenu {
  protected readonly settings = inject(TurnSettingsStore);
  protected readonly mcps = inject(McpCatalogStore);
}
