import { ChangeDetectionStrategy, Component, computed, inject, viewChild } from '@angular/core';
import { ModelDto, isRetired } from '@domain/api/model';
import { ProviderMeta, providerMeta } from '@domain/api/provider';
import { ModelActionsStore } from '@core/catalog/model-actions-store';
import { canRetry } from '@core/errors/error-message';
import { StatusDot } from '@shared/badge/status-dot/status-dot';
import { DataTable } from '@shared/data/data-table/data-table';
import { TableCell } from '@shared/data/data-table/table-cell';
import { TableColumn } from '@shared/data/data-table/table-column';
import { EmptyState } from '@shared/feedback/empty-state/empty-state';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { SearchInput } from '@shared/form/search-input/search-input';
import { Icon } from '@shared/icon/icon';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';
import { AdminModelsStore } from './admin-models-store';
import { DeactivateModelDialog } from './deactivate-model-dialog';
import { ModelFormDialog } from './model-form-dialog';
import { formatContext, formatTokens } from './model-format';

/**
 * The administration model catalog, frame `5e` (US-1207).
 *
 * **Regime C, so there is no URL contract.** `GET api/models/all` is unpaginated, so the
 * screen holds the whole catalog and the search field filters it in memory. That is why
 * this screen — unlike `AdminUsers` next door — has no route inputs, no `_applyQuery`
 * and no pager: a client-side filter is screen state, and putting it in the address bar
 * would promise a link that reproduces a server result it never asked the server for.
 *
 * **Retired models are rendered, not hidden.** `DELETE api/models/{id}` is a soft delete
 * and this route is the only one that returns the rows it produces, so a table that
 * dropped them would make Delete look like it did nothing — and the model would then be
 * invisible everywhere in the app. They render muted with a Status marker borrowed from
 * frame `5g`, which frame `5e` does not draw, and their kebab offers no items at all:
 * there is no reactivate route, and an affordance that is present and inert is the thing
 * US-203 already rejected.
 *
 * Frame `5e` draws no sort control and none is offered. The comparators are wired in the
 * store, so US-902's sibling story on this tab is a template change.
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

  /**
   * Fields rather than template literals. `provideCheckNoChangesConfig({ exhaustive:
   * true })` compares bindings between passes, and an array or an arrow allocated in the
   * template is a new value on every check.
   */
  protected readonly columns: readonly TableColumn<ModelDto>[] = [
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
    // Not in frame `5e`, which draws no status at all. It is here because this route
    // returns retired models and the frame's table has no other way to tell one apart.
    { key: 'status', header: 'Status', width: '100px', mobile: 'badges' },
    // headerHidden rather than an empty header: the column still needs a name for a
    // screen reader listing the table's columns, and the board draws no label here.
    {
      key: 'actions',
      header: 'Actions',
      headerHidden: true,
      width: '44px',
      align: 'end',
      mobile: 'trailing',
    },
  ];

  protected readonly trackKey = (model: ModelDto): string => model.id;
  protected readonly rowLabel = (model: ModelDto): string => model.name;

  constructor() {
    this.models.load();

    // `ModelActionsStore` is root-scoped and outlives this route, while an open dialog is
    // about one visit to it. Without these two, opening Add model, leaving for another
    // admin tab and coming back would re-mount the dialog with `open()` still true and
    // `Modal` would `showModal()` a surface the reader never asked for — the same leak
    // `AdminUsers` closes for its own standing refusal.
    //
    // Neither clears `formBusy`, which tracks a request rather than a surface.
    this.actions.cancelForm();
    this.actions.cancelDeactivate();
  }

  /**
   * The provider's presentation, or null for an id this build does not know.
   *
   * There is no `GET api/providers` — no endpoint exposes provider names to any caller,
   * administrator included — so `providerMeta` is a mirror of the seeded rows. A
   * provider added server-side after this build degrades to a muted dot and its raw id,
   * which is honest, rather than breaking the row.
   */
  protected provider(model: ModelDto): ProviderMeta | null {
    return providerMeta(model.providerId);
  }

  protected onSearched(term: string): void {
    this.models.search(term);
  }

  protected clearSearch(): void {
    this.onSearched('');
    // The button removes itself the moment rows come back, and focus would fall to
    // `<body>` inside a table the reader then has to find again. The field is outside
    // every branch of the template, so it is always connected.
    this._search().focusField();
  }
}
