import { ChangeDetectionStrategy, Component, computed, inject, viewChild } from '@angular/core';
import { McpServerDto, isMcpServerRetired } from '@domain/api/mcp';
import { McpServerActionsStore } from '@core/catalog/mcp-server-actions-store';
import { authTypeLabel } from '@core/catalog/mcp-server-form';
import { canRetry } from '@core/errors/error-message';
import { PermissionBadge } from '@shared/badge/permission-badge/permission-badge';
import { StatusDot } from '@shared/badge/status-dot/status-dot';
import { DataTable } from '@shared/data/data-table/data-table';
import { TableCell } from '@shared/data/data-table/table-cell';
import { TableColumn } from '@shared/data/data-table/table-column';
import { EmptyState } from '@shared/feedback/empty-state/empty-state';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { SearchInput } from '@shared/form/search-input/search-input';
import { BrandIcon } from '@shared/icon/brand-icon';
import { Icon } from '@shared/icon/icon';
import { mcpBrandIcon } from '@shared/icon/mcp-icon';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';
import { AdminMcpsStore } from './admin-mcps-store';
import { DeactivateMcpServerDialog } from './deactivate-mcp-server-dialog';
import { McpServerFormDialog } from './mcp-server-form-dialog';

/**
 * The admin MCP server registry. Deactivated (soft-deleted) servers stay listed, muted, because
 * hiding them would make Deactivate look inert.
 */
@Component({
  selector: 'app-admin-mcps',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    BrandIcon,
    DataTable,
    DeactivateMcpServerDialog,
    EmptyState,
    ErrorPanel,
    Icon,
    McpServerFormDialog,
    Menu,
    MenuItem,
    PermissionBadge,
    SearchInput,
    StatusDot,
    TableCell,
  ],
  providers: [AdminMcpsStore],
  templateUrl: './admin-mcps.html',
  styleUrl: './admin-mcps.scss',
})
export class AdminMcps {
  protected readonly servers = inject(AdminMcpsStore);
  protected readonly actions = inject(McpServerActionsStore);

  private readonly _search = viewChild.required(SearchInput);

  /** A 403 answers the same however often it is asked; a 502 does not. */
  protected readonly canRetry = canRetry;

  protected readonly authTypeLabel = authTypeLabel;
  protected readonly isRetired = isMcpServerRetired;

  protected brandIcon(server: McpServerDto) {
    return mcpBrandIcon(server.iconKey);
  }

  protected readonly noMatchHeading = computed(
    () => `No servers match “${this.servers.query().trim()}”`,
  );

  /** Fields, not template literals: exhaustive no-changes checks see those as new values. */
  protected readonly columns: readonly TableColumn<McpServerDto>[] = [
    { key: 'server', header: 'Server', width: 'minmax(0, 1.1fr)', mobile: 'title' },
    {
      key: 'description',
      header: 'Description',
      width: 'minmax(0, 1.7fr)',
      mobile: 'subtitle',
      hideOnTablet: true,
    },
    { key: 'url', header: 'URL', width: 'minmax(0, 1.7fr)', mobile: 'meta' },
    // A narrower track would permanently ellipsise "Entra ID (on behalf of)".
    { key: 'auth', header: 'Auth', width: '150px', mobile: 'meta', hideOnTablet: true },
    {
      key: 'scope',
      header: 'Scope',
      width: 'minmax(0, 0.9fr)',
      mobile: 'meta',
      hideOnTablet: true,
    },
    { key: 'permission', header: 'Permission', width: 'minmax(0, 1fr)', mobile: 'badges' },
    { key: 'status', header: 'Status', width: '100px', mobile: 'badges' },
    // headerHidden rather than empty: screen readers still need the column's name.
    {
      key: 'actions',
      header: 'Actions',
      headerHidden: true,
      width: '44px',
      align: 'end',
      mobile: 'trailing',
    },
  ];

  protected readonly trackKey = (server: McpServerDto): string => server.id;
  protected readonly rowLabel = (server: McpServerDto): string => server.name;

  constructor() {
    this.servers.load();

    // The root-scoped actions store outlives this route, so a dialog left open on a previous
    // visit would otherwise reopen. `formBusy` tracks a request, so it is left alone.
    this.actions.cancelForm();
    this.actions.cancelDeactivate();
  }

  protected onSearched(term: string): void {
    this.servers.search(term);
  }

  protected clearSearch(): void {
    this.onSearched('');
    // The button removes itself once rows return; without this, focus falls to `<body>`.
    this._search().focusField();
  }
}
