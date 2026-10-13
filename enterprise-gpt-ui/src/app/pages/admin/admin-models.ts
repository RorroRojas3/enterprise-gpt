import { ChangeDetectionStrategy, Component, computed, inject, viewChild } from '@angular/core';
import { ModelDto } from '@shared/models/api/chat-model.model';
import { ProviderMeta } from '@shared/models/api/provider.model';
import { isRetired } from '@shared/utils/catalog/model-status';
import { providerMeta } from '@shared/utils/catalog/provider-meta';
import { canRetry } from '@core/errors/error-message';
import { AdminModelsStore } from '@state/admin/admin-models-store';
import { ModelActionsStore } from '@state/model-actions-store';
import { DataTable } from '@shared/components/data-table/data-table';
import { EmptyState } from '@shared/components/empty-state/empty-state';
import { ErrorPanel } from '@shared/components/error-panel/error-panel';
import { Icon } from '@shared/components/icon/icon';
import { Menu } from '@shared/components/menu/menu';
import { SearchInput } from '@shared/components/search-input/search-input';
import { StatusDot } from '@shared/components/status-dot/status-dot';
import { MenuItem } from '@shared/directives/menu-item';
import { TableCell } from '@shared/directives/table-cell';
import { DeactivateModelDialog } from '@components/admin/models/deactivate-model-dialog';
import { ModelFormDialog } from '@components/admin/models/model-form-dialog';
import { injectAdminColumns } from './admin-columns';
import { formatContext, formatTokens } from './model-format';

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
