import { HttpClient, HttpParams } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { pipe, switchMap, takeUntil, tap } from 'rxjs';
import { UsageReportDto } from '@domain/api/usage-report';
import { MS_PER_DAY, groupingNoun } from './usage-format';
import { toAppError } from '@core/errors/to-app-error';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import {
  setError,
  setFulfilled,
  setPending,
  withRequestStatus,
} from '@core/state/with-request-status';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';
import {
  DEFAULT_USAGE_GROUPING,
  DEFAULT_USAGE_RANGE,
  USAGE_PAGE_SIZE,
  UsageGrouping,
  UsageRange,
  toUsageGrouping,
} from './admin-reports-route';

/** One object because both go out on one request; bound separately, a change would fire twice. */
export interface UsageReportQuery {
  readonly range: UsageRange;
  readonly grouping: UsageGrouping;
}

interface ReportsState {
  readonly report: UsageReportDto | null;
  readonly range: UsageRange;
  readonly grouping: UsageGrouping;
  /** The 1-based page of the grouped table. Screen state, not a URL contract. */
  readonly page: number;
}

const initialState: ReportsState = {
  report: null,
  range: DEFAULT_USAGE_RANGE,
  grouping: DEFAULT_USAGE_GROUPING,
  page: 1,
};

/**
 * Provided on the screen, never the layout, so every entry to the route gets a fresh instance and
 * request, never a cached report. No `withEntities` (one envelope, no ids), no
 * `withOffsetPagination` (a numbered window over rows in hand), and no event handlers (nothing in
 * the app mutates usage).
 */
export const ReportsStore = signalStore(
  withState(initialState),
  withRequestStatus(),
  withProps(() => ({
    _http: inject(HttpClient),
    _url: inject(ApiUrl).build('reports/usage'),
    _signedOut$: injectSignedOut(),
  })),
  withComputed(({ report, grouping, page, isPending }) => {
    const groups = computed(() => report()?.groups ?? []);
    const totalPages = computed(() => Math.max(1, Math.ceil(groups().length / USAGE_PAGE_SIZE)));

    return {
      groups,
      totalPages,
      isFirstLoad: computed(() => isPending() && report() === null),
      /**
       * Read off the report, not `isFulfilled`, so a re-range in flight keeps the empty state
       * instead of painting a chart of zeroes.
       */
      isEmpty: computed(() => {
        const loaded = report();

        return loaded !== null && loaded.totals.turns === 0;
      }),
      pagedGroups: computed(() => {
        const start = (page() - 1) * USAGE_PAGE_SIZE;

        return groups().slice(start, start + USAGE_PAGE_SIZE);
      }),
      /** What `Paginator` needs, so the footer is the shared control rather than a second one. */
      totalCount: computed(() => groups().length),
      pageSize: computed(() => USAGE_PAGE_SIZE),
      /**
       * `Paginator`'s sentence again, for `DataTable`'s live region; the footer sets `liveSummary`
       * false so a screen reader does not read the count twice.
       */
      countSummary: computed(() => {
        const total = groups().length;
        // Always plural, like `Paginator.itemLabel`, so the announced and visible sentences
        // match on a one-row breakdown.
        const noun = groupingNoun(toUsageGrouping(report()?.groupBy ?? grouping()), 2);
        if (total === 0) {
          return `No ${noun}`;
        }

        const first = (page() - 1) * USAGE_PAGE_SIZE + 1;
        const last = Math.min(page() * USAGE_PAGE_SIZE, total);

        return `Showing ${first}–${last} of ${total} ${noun}`;
      }),
      /**
       * Why the rows may not sum to the totals: a fact about the server's answer, so a table
       * notice rather than part of the count.
       */
      truncationNotice: computed(() => {
        const loaded = report();

        return loaded?.groupsTruncated === true
          ? `Only the ${loaded.groupLimit} heaviest ${groupingNoun(toUsageGrouping(loaded.groupBy), loaded.groupLimit)} are listed. The totals above cover the whole range.`
          : null;
      }),
      /**
       * The grouping of the rows on screen, which can lag the control. Narrowed, not cast, so an
       * unrecognised server token cannot index the label tables with `undefined`.
       */
      renderedGrouping: computed(() => toUsageGrouping(report()?.groupBy ?? grouping())),
    };
  }),
  withMethods((store) => {
    function currentQuery(): UsageReportQuery {
      return { range: store.range(), grouping: store.grouping() };
    }

    // No `debounceTime`: neither control is a text field. `switchMap` so only the newest report
    // reaches the screen.
    const _load = rxMethod<UsageReportQuery>(
      pipe(
        tap((query) => patchState(store, query, setPending())),
        switchMap(({ range, grouping }) => {
          // Only `from` is sent: the window ends on the server's clock, which wrote the rows, so
          // a wrong browser clock cannot decide which of them count.
          const params = new HttpParams()
            .set('from', new Date(Date.now() - range * MS_PER_DAY).toISOString())
            .set('groupBy', grouping);

          return store._http.get<UsageReportDto>(store._url, { params }).pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (report) =>
                // Back to page 1 on every answer: the old page may name rows no longer there.
                patchState(store, { report, page: 1 }, setFulfilled()),
              error: (cause: unknown) =>
                patchState(store, setError(toAppError(cause, { url: store._url }))),
            }),
          );
        }),
      ),
    );

    return {
      /**
       * Called once from the constructor with a computation, not values, so a deep link makes one
       * request with the router-bound parameters instead of a default one first.
       */
      bindQuery: _load,

      /** Re-reads the report after a failure, from `ErrorPanel`'s retry. */
      retry(): void {
        _load(currentQuery());
      },

      /**
       * Screen state, not a URL parameter: a link restoring page 3 while the charts above start
       * fresh would restore half a view.
       */
      setPage(page: number): void {
        patchState(store, { page: Math.min(Math.max(page, 1), store.totalPages()) });
      },
    };
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
