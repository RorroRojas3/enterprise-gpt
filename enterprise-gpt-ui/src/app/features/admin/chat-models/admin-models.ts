import { ChangeDetectionStrategy, Component, computed, inject, viewChild } from '@angular/core';
import { ModelDto } from '@core/api/chat-model.model';
import { ProviderMeta } from '@core/api/provider.model';
import { providerMeta } from '@core/catalog/provider-meta';
import { canRetry } from '@core/errors/error-message';
import { ErrorPanel } from '@core/errors/error-panel';
import { StatusDot } from '@shared/badges/status-dot';
import { DataTable } from '@shared/data-table/data-table';
import { TableCell } from '@shared/data-table/table-cell';
import { EmptyState } from '@shared/empty-state/empty-state';
import { Icon } from '@shared/icons/icon';
import { Menu } from '@shared/menu/menu';
import { MenuItem } from '@shared/menu/menu-item';
import { SearchInput } from '@shared/search/search-input';
import { injectAdminColumns } from '@features/admin/admin-columns';
import { AdminModelsStore } from './admin-models-store';
import { DeactivateModelDialog } from './deactivate-model-dialog';
import { ModelActionsStore } from './model-actions-store';
import { ModelFormDialog } from './model-form-dialog';
import { formatContext, formatTokens } from './model-format';
import { isRetired } from './model-status';

/**
 * Retired models render muted, not hidden: Delete is a soft delete and this is the only route
 * that returns them. Their kebab offers nothing, since there is no reactivate route.
 */
@Component({
  selector: 'app-admin-models',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DataTable,
    DeactivateModelDialog,
    EmptyState,
    ErrorPanel,
    Icon,
    Menu,
    MenuItem,
    ModelFormDialog,
    SearchInput,
    StatusDot,
    TableCell,
  ],
  providers: [AdminModelsStore],
  host: { class: 'd-block' },
  templateUrl: './admin-models.html',
  styleUrl: './admin-models.scss',
})
export class AdminModels {
  protected readonly models = inject(AdminModelsStore);
  protected readonly actions = inject(ModelActionsStore);

  private readonly _search = viewChild.required(SearchInput);

  /** A 403 answers the same however often it is asked; a 502 does not. */
  protected readonly canRetry = canRetry;

  protected readonly formatContext = formatContext;
  protected readonly formatTokens = formatTokens;
  protected readonly isRetired = isRetired;

  protected readonly noMatchHeading = computed(
    () => `No models match “${this.models.query().trim()}”`,
  );

  /** A field, since an array allocated in the template is a new binding on every check. */
  protected readonly columns = injectAdminColumns<ModelDto>([
    { key: 'model', header: 'Model', width: 'minmax(0, 1.5fr)', mobile: 'title' },
    { key: 'provider', header: 'Provider', width: 'minmax(0, 1.1fr)', mobile: 'subtitle' },
    {
      key: 'deployment',
      header: 'Deployment',
      width: 'minmax(0, 1.5fr)',
      mobile: 'meta',
      hideOnTablet: true,
    },
    { key: 'context', header: 'Context', width: '90px', align: 'end', mobile: 'meta' },
    {
      key: 'output',
      header: 'Max output',
      width: '110px',
      align: 'end',
      mobile: 'meta',
      hideOnTablet: true,
    },
    {
      key: 'tools',
      header: 'Tools',
      width: '80px',
      align: 'center',
      mobile: 'badges',
      hideOnTablet: true,
    },
    // Not in the design, but nothing else tells a retired model apart.
    { key: 'status', header: 'Status', width: '100px', mobile: 'badges' },
    // Hidden rather than empty, so a screen reader can still name the column.
    {
      key: 'actions',
      header: 'Actions',
      headerHidden: true,
      width: '44px',
      align: 'end',
      mobile: 'trailing',
    },
  ]);

  protected readonly trackKey = (model: ModelDto): string => model.id;
  protected readonly rowLabel = (model: ModelDto): string => model.name;

  constructor() {
    this.models.load();

    // `ModelActionsStore` is root-scoped, so a dialog left open on a past visit would reopen
    // on return. Neither clears `formBusy`, which tracks a request rather than a surface.
    this.actions.cancelForm();
    this.actions.cancelDeactivate();
  }

  /** Null for a provider newer than this build, which degrades to a muted dot and its raw id. */
  protected provider(model: ModelDto): ProviderMeta | null {
    return providerMeta(model.providerId);
  }

  protected onSearched(term: string): void {
    this.models.search(term);
  }

  protected clearSearch(): void {
    this.onSearched('');
    // The button removes itself once rows return, so focus would fall to `<body>`; the field
    // sits outside every template branch and is always connected.
    this._search().focusField();
  }
}
