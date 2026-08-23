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
 * The administration MCP server registry, frame `5g` (US-1208).
 *
 * **Regime C, so there is no URL contract**, exactly as the model catalog next door:
 * `GET api/mcps/all` is unpaginated, so the screen holds the whole registry and the
 * search field filters it in memory. No route inputs, no `_applyQuery`, no pager.
 *
 * **Deactivated servers are rendered, not hidden.** `DELETE api/mcps/{id}` is a soft
 * delete and `mcps/all` is the only route that returns what it produces — the user-facing
 * `api/mcps` filters to active servers the caller holds a grant for. A table that dropped
 * them would make the action look inert. They render muted against frame `5g`'s own
 * active/inactive dot, and their kebab offers no items at all: there is no reactivate
 * route, so every item would be an affordance that cannot work.
 *
 * The linked-permission badge names the permission from the **server's own name**, with
 * no second request: `McpServerService` creates the permission with that name and
 * re-syncs it on every rename, and the two share one uniqueness space. The badge is
 * absent once `permissionId` is null, which only happens on a deactivated row — the
 * cascade takes the permission with the server.
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

  /**
   * Fields rather than template literals. `provideCheckNoChangesConfig({ exhaustive:
   * true })` compares bindings between passes, and an array or an arrow allocated in the
   * template is a new value on every check.
   */
  protected readonly columns: readonly TableColumn<McpServerDto>[] = [
    { key: 'server', header: 'Server', width: 'minmax(0, 1.1fr)', mobile: 'title' },
    { key: 'description', header: 'Description', width: 'minmax(0, 1.7fr)', mobile: 'subtitle' },
    { key: 'url', header: 'URL', width: 'minmax(0, 1.7fr)', mobile: 'meta' },
    // 150px, not frame `5g`'s 90: "Entra ID (on behalf of)" is one of only two values
    // this column can hold, and a narrower track ellipsises it permanently.
    { key: 'auth', header: 'Auth', width: '150px', mobile: 'meta' },
    { key: 'scope', header: 'Scope', width: 'minmax(0, 0.9fr)', mobile: 'meta' },
    { key: 'permission', header: 'Permission', width: 'minmax(0, 1fr)', mobile: 'badges' },
    { key: 'status', header: 'Status', width: '100px', mobile: 'badges' },
    // headerHidden rather than an empty header: the column still needs a name for a
    // screen reader listing the table's columns, and the board draws no label here.
    {
      key: 'actions',
      header: 'Actions',
      headerHidden: true,
      width: '44px',
      align: 'end',
      mobile: 'actions',
    },
  ];

  protected readonly trackKey = (server: McpServerDto): string => server.id;
  protected readonly rowLabel = (server: McpServerDto): string => server.name;

  constructor() {
    this.servers.load();

    // `McpServerActionsStore` is root-scoped and outlives this route, while an open dialog
    // is about one visit to it. Without these two, opening Add server, leaving for another
    // admin tab and coming back would re-mount the dialog with `open()` still true and
    // `Modal` would `showModal()` a surface the reader never asked for.
    //
    // Neither clears `formBusy`, which tracks a request rather than a surface.
    this.actions.cancelForm();
    this.actions.cancelDeactivate();
  }

  protected onSearched(term: string): void {
    this.servers.search(term);
  }

  protected clearSearch(): void {
    this.onSearched('');
    // The button removes itself the moment rows come back, and focus would fall to
    // `<body>` inside a table the reader then has to find again. The field is outside
    // every branch of the template, so it is always connected.
    this._search().focusField();
  }
}
