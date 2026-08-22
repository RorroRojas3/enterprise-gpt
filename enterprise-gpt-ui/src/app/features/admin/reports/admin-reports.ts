import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { ActivatedRoute, Params, Router } from '@angular/router';
import { UsageGroupDto } from '@domain/api/usage-report';
import { canRetry } from '@core/errors/error-message';
import { DataTable } from '@shared/data/data-table/data-table';
import { TableCell } from '@shared/data/data-table/table-cell';
import { TableColumn } from '@shared/data/data-table/table-column';
import { Paginator } from '@shared/data/paginator/paginator';
import { EmptyState } from '@shared/feedback/empty-state/empty-state';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { MOBILE_VIEWPORT, TABLET_VIEWPORT } from '@shared/layout/breakpoints';
import { injectMediaQuery } from '@shared/layout/media-query';
import { Icon } from '@shared/icon/icon';
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
} from './admin-reports-route';
import { KpiTile } from './kpi-tile';
import { OutcomeDonut } from './outcome-donut';
import { RangeSegmented } from './range-segmented';
import { ReportsStore } from './admin-reports-store';
import { UsageAreaChart } from './usage-area-chart';
import { UsageBarList } from './usage-bar-list';
import {
  MS_PER_DAY,
  NO_VALUE,
  deltaPercent,
  formatCost,
  formatCount,
  formatDateRange,
  formatTokens,
  groupingNoun,
} from './usage-format';

/**
 * The administration usage reports tab, frames `5j` and `5k` (US-1302).
 *
 * **The URL is the source of truth for what is on screen** — `?range=` and `?group=`. Both
 * controls are views of it, and the only writer is {@link _applyQuery}: `withComponentInputBinding()`
 * binds the parameters to inputs, the inputs drive the store, and the back button restoring the
 * previous selection is then a property of the router rather than code here.
 *
 * Neither parameter can feed the loop back on itself. Both bind one-way, both are repaired
 * synchronously in their own transform — the sets are compile-time constants, so unlike the user
 * directory's `?permission=` there is nothing to ask the server and no repairing effect to write —
 * and nothing navigates except a reader's own gesture.
 *
 * **The route, the rail entry, the page title and the `<h1>` are all inherited from US-1209** and
 * none of them changes here. What changed is what fills the pane: `UnavailablePanel` is gone,
 * because `GET api/reports/usage` now exists.
 *
 * `ReportsStore` is provided **on this component**, which is what makes FR-41 structural — see
 * the store's own doc.
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
   * Drives the table's track set; the rest of the dashboard stacks in CSS.
   *
   * Derived from the two narrow queries rather than asserted with a positive
   * `(min-width: 1024px)`, which is `breakpoints.ts`'s own rule: the test fake answers `false` for
   * every query a spec has not set, so desktop has to be what "nothing configured" means or every
   * existing spec would silently run against the narrow layout.
   */
  private readonly isDesktop = computed(() => !this._isTablet() && !this._isMobile());

  private readonly _router = inject(Router);
  private readonly _route = inject(ActivatedRoute);

  /**
   * The chip beside the range control: the window the server actually measured.
   *
   * Read off the response rather than recomputed from `?range=`, so it can never claim a window
   * the figures below it were not taken from.
   */
  protected readonly dateRange = computed(() => {
    const report = this.reports.report();

    return report === null ? NO_VALUE : formatDateRange(report.from, report.to);
  });

  /**
   * "prior 30 days" — what the tiles' deltas are measured against.
   *
   * Derived from the window the response reports rather than from `?range=`, for the reason
   * `renderedGrouping` exists: the control flips the instant it is clicked, and until the next
   * report lands the tiles below still hold the previous period's deltas. A caption naming a range
   * the figures were not taken from is worse than one that lags by a request.
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
        // Null costs compare as zero rather than as "no data": a period nobody could price and a
        // period that cost nothing are the same *change* from a prior period that was also
        // unpriced, and the tile's own value already says which of the two this was.
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

  /**
   * The table's columns, with the first one named after the grouping the rows belong to.
   *
   * A `computed` rather than a field because the header changes, and reading the *rendered*
   * grouping rather than the control's keeps the header honest while the next report is on the
   * wire — the rows on screen still belong to the previous one.
   */
  protected readonly columns = computed<readonly TableColumn<UsageGroupDto>[]>(() => {
    // Frame `5j`'s own track set at desktop. Below it the six fixed columns alone are wider than
    // the content pane, which pushes the name column to nothing and clips every row — so they
    // become proportional and shrink together instead. Hiding columns was the other option and is
    // worse: every one of them is a figure the table exists to compare.
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

  /**
   * Whether the bar chart offers a way to see every model.
   *
   * Only when the table is showing something else. Under `group=model` the table already lists
   * every model the range touched, so the link would be an affordance that changes nothing.
   */
  protected readonly canViewAllModels = computed(
    () => this.reports.renderedGrouping() !== DEFAULT_USAGE_GROUPING,
  );

  /**
   * The empty state names the range, which is criterion 5: a dashboard of zeroes is
   * indistinguishable from a dashboard of a quiet month.
   *
   * Joined with "and" rather than the chip's en dash, because this one reads mid-sentence.
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
    // The constructor is an injection context, which a signal-valued reactive method requires.
    // Handing over a computation rather than the values is what makes a deep link issue one
    // request carrying both parameters, rather than a default request followed by the real one.
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
      // The users tab's rule, and the whole of it: turning a grouping on or off is a transition and
      // pushes, swapping one for another is a refinement and replaces. A closed `<select>` commits
      // on every arrow key, so replacing the swaps costs a keyboard reader one entry for the whole
      // walk instead of one per grouping passed — while the push at either end is what keeps Back
      // able to undo the change at all.
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
   * The single writer. Every control change is a navigation and nothing writes the store directly,
   * which is what keeps the URL authoritative and the back button working without a line of code.
   *
   * `replaceUrl` has no default, so every call site has to decide: a new control that silently
   * pushed would be the arrow-key flood again, and one that silently replaced would be a change
   * Back cannot undo.
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
