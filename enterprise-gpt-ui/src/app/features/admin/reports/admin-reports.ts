import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { ActivatedRoute, Params, Router } from '@angular/router';
import { UsageGroupDto } from '@shared/models/api/usage-report.model';
import { TableColumn } from '@shared/models/table-column.model';
import {
  MS_PER_DAY,
  NO_VALUE,
  deltaPercent,
  formatCost,
  formatCount,
  formatDateRange,
  formatTokens,
  groupingNoun,
} from '@shared/utils/format/usage-format';
import { MOBILE_VIEWPORT, TABLET_VIEWPORT } from '@shared/utils/layout/breakpoints';
import {
  DEFAULT_USAGE_GROUPING,
  DEFAULT_USAGE_RANGE,
  GROUPING_COLUMN_LABELS,
  GROUP_PARAM,
  RANGE_PARAM,
  USAGE_GROUPINGS,
  UsageGrouping,
  UsageRange,
  toUsageGrouping,
  toUsageRange,
} from '@shared/utils/navigation/admin-reports-route';
import { canRetry } from '@core/errors/error-message';
import { injectMediaQuery } from '@core/ui/media-query';
import { ReportsStore } from '@state/admin/admin-reports-store';
import { DataTable } from '@shared/components/data-table/data-table';
import { EmptyState } from '@shared/components/empty-state/empty-state';
import { ErrorPanel } from '@shared/components/error-panel/error-panel';
import { Icon } from '@shared/components/icon/icon';
import { Paginator } from '@shared/components/paginator/paginator';
import { Skeleton } from '@shared/components/skeleton/skeleton';
import { TableCell } from '@shared/directives/table-cell';
import { KpiTile } from '@components/admin/reports/kpi-tile';
import { OutcomeDonut } from '@components/admin/reports/outcome-donut';
import { RangeSegmented } from '@components/admin/reports/range-segmented';
import { UsageAreaChart } from '@components/admin/reports/usage-area-chart';
import { UsageBarList } from '@components/admin/reports/usage-bar-list';

/**
 * `?range=` and `?group=` are the source of truth: bound inputs drive the store and
 * {@link _applyQuery} is the only writer, so Back restores a selection with no code here.
 * `ReportsStore` is provided on this component so every visit refetches.
 */
@Component({
  selector: 'app-admin-reports',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DataTable,
    EmptyState,
    ErrorPanel,
    Icon,
    KpiTile,
    OutcomeDonut,
    Paginator,
    RangeSegmented,
    Skeleton,
    TableCell,
    UsageAreaChart,
    UsageBarList,
  ],
  providers: [ReportsStore],
  host: { class: 'd-flex flex-column flex-fill gap-3' },
  templateUrl: './admin-reports.html',
  styleUrl: './admin-reports.scss',
})
export class AdminReports {
  /** Bound from `?range=`; anything outside the offered set reads as the default. */
  readonly range = input(DEFAULT_USAGE_RANGE, { transform: toUsageRange });

  /** Bound from `?group=`, on the same terms. */
  readonly group = input(DEFAULT_USAGE_GROUPING, { transform: toUsageGrouping });

  protected readonly reports = inject(ReportsStore);
  protected readonly canRetry = canRetry;
  protected readonly groupings = USAGE_GROUPINGS;

  // A field, not a template expression: `provideCheckNoChangesConfig({ exhaustive: true })` fails
  // any array allocated during change detection, because it is a new value every pass.
  protected readonly skeletonTiles = [1, 2, 3, 4];

  private readonly _isTablet = injectMediaQuery(TABLET_VIEWPORT);
  private readonly _isMobile = injectMediaQuery(MOBILE_VIEWPORT);

  /**
   * Derived from the narrow queries, not a positive desktop query: the test fake answers `false`
   * to unset queries, so desktop must be what "nothing configured" means.
   */
  private readonly isDesktop = computed(() => !this._isTablet() && !this._isMobile());

  private readonly _router = inject(Router);
  private readonly _route = inject(ActivatedRoute);

  /**
   * The window the server measured, read off the response rather than `?range=` so it never
   * claims a window the figures were not taken from.
   */
  protected readonly dateRange = computed(() => {
    const report = this.reports.report();

    return report === null ? NO_VALUE : formatDateRange(report.from, report.to);
  });

  /**
   * Derived from the response, not `?range=`: until the next report lands the tiles still hold
   * the previous period's deltas, and a caption that lags beats one that is wrong.
   */
  protected readonly priorCaption = computed(() => {
    const report = this.reports.report();
    if (report === null) {
      return `prior ${this.range()} days`;
    }

    const days = Math.round(
      (new Date(report.to).getTime() - new Date(report.from).getTime()) / MS_PER_DAY,
    );

    return `prior ${days} days`;
  });

  protected readonly kpis = computed(() => {
    const report = this.reports.report();
    if (report === null) {
      return [];
    }

    const { totals, priorTotals } = report;

    return [
      {
        label: 'Total tokens',
        value: formatTokens(totals.totalTokens),
        delta: deltaPercent(totals.totalTokens, priorTotals.totalTokens),
      },
      {
        label: 'Estimated cost',
        value: formatCost(totals.estimatedCost),
        // Null costs compare as zero: unpriced and free are the same change from an unpriced
        // prior, and the tile's value already says which.
        delta: deltaPercent(totals.estimatedCost ?? 0, priorTotals.estimatedCost ?? 0),
      },
      {
        label: 'Active users',
        value: formatCount(totals.activeUsers),
        delta: deltaPercent(totals.activeUsers, priorTotals.activeUsers),
      },
      {
        label: 'Conversations',
        value: formatCount(totals.conversations),
        delta: deltaPercent(totals.conversations, priorTotals.conversations),
      },
    ];
  });

  /** Headed by the rendered grouping, not the control's, while the next report is in flight. */
  protected readonly columns = computed<readonly TableColumn<UsageGroupDto>[]>(() => {
    // Below desktop the fixed tracks overflow the pane and crush the name column, so they go
    // proportional; hiding columns would drop figures the table exists to compare.
    const numeric = this.isDesktop() ? ['70px', '90px', '90px', '70px', '90px', '90px'] : null;
    const at = (index: number): string => numeric?.[index] ?? 'minmax(0, 1fr)';

    return [
      {
        key: 'group',
        header: GROUPING_COLUMN_LABELS[this.reports.renderedGrouping()],
        width: this.isDesktop() ? 'minmax(0, 1.4fr)' : 'minmax(0, 1.6fr)',
        mobile: 'title',
      },
      { key: 'turns', header: 'Turns', width: at(0), align: 'end', mobile: 'meta' },
      { key: 'completed', header: 'Completed', width: at(1), align: 'end', mobile: 'meta' },
      { key: 'cancelled', header: 'Cancelled', width: at(2), align: 'end', mobile: 'meta' },
      { key: 'failed', header: 'Failed', width: at(3), align: 'end', mobile: 'meta' },
      { key: 'tokens', header: 'Tokens', width: at(4), align: 'end', mobile: 'meta' },
      { key: 'cost', header: 'Est. cost', width: at(5), align: 'end', mobile: 'meta' },
    ];
  });

  /** Under `group=model` the table already lists every model, so "View all" would do nothing. */
  protected readonly canViewAllModels = computed(
    () => this.reports.renderedGrouping() !== DEFAULT_USAGE_GROUPING,
  );

  /**
   * Names the range, since a dashboard of zeroes looks like a quiet month. Joined with "and", not
   * the chip's en dash, because it reads mid-sentence.
   */
  protected readonly emptyHeading = computed(() => {
    const report = this.reports.report();

    return report === null
      ? 'No usage in this range'
      : `No usage between ${formatDateRange(report.from, report.to, 'and')}`;
  });

  /** What the paginator calls the rows it is windowing — "models", "days", "users". */
  protected readonly itemLabel = computed(() => groupingNoun(this.reports.renderedGrouping(), 2));

  protected readonly tableLabel = computed(
    () => `Usage by ${GROUPING_COLUMN_LABELS[this.reports.renderedGrouping()].toLowerCase()}`,
  );

  protected readonly trackKey = (group: UsageGroupDto): string => group.key;
  protected readonly rowLabel = (group: UsageGroupDto): string => group.label;

  constructor() {
    // A computation, not values, so a deep link issues one request carrying both parameters.
    this.reports.bindQuery(() => ({ range: this.range(), grouping: this.group() }));
  }

  protected onRangeSelected(range: UsageRange): void {
    // Pushes. One press is one deliberate choice, and Back should return to the previous range.
    this._applyQuery({ [RANGE_PARAM]: range === DEFAULT_USAGE_RANGE ? null : range }, false);
  }

  protected onGroupingChanged(event: Event): void {
    const value = toUsageGrouping((event.target as HTMLSelectElement).value);

    this._applyQuery(
      { [GROUP_PARAM]: value === DEFAULT_USAGE_GROUPING ? null : value },
      // Turning a grouping on or off pushes; swapping one for another replaces, since a closed
      // `<select>` commits on every arrow key and would flood history.
      value !== DEFAULT_USAGE_GROUPING && this.group() !== DEFAULT_USAGE_GROUPING,
    );
  }

  /** The bar chart's "View all", which is a request to list every model rather than the top ten. */
  protected viewAllModels(): void {
    this._applyQuery({ [GROUP_PARAM]: null }, false);
  }

  protected columnLabel(grouping: UsageGrouping): string {
    return GROUPING_COLUMN_LABELS[grouping];
  }

  protected tokens(value: number): string {
    return formatTokens(value);
  }

  protected count(value: number): string {
    return formatCount(value);
  }

  protected cost(value: number | null): string {
    return formatCost(value);
  }

  /**
   * The single writer. `replaceUrl` has no default so each call site decides: a silent push floods
   * history on arrow keys, and a silent replace makes a change Back cannot undo.
   */
  private _applyQuery(queryParams: Params, replaceUrl: boolean): void {
    void this._router.navigate([], {
      relativeTo: this._route,
      queryParams,
      // The range has to survive a grouping change and the grouping a range change.
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }
}
