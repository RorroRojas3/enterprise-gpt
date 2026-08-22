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

/** The parameters one report is fetched with. They travel together because they go out together. */
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
 * The administration usage report, from `GET api/reports/usage` (US-1302).
 *
 * **Provided on the screen, never on the layout**, which is what makes FR-41 a property of the
 * dependency graph rather than of any code here: a component-level provider is destroyed and
 * rebuilt on every entry to the route, so a back-navigation gets a new instance holding no report
 * and its constructor-time {@link bindQuery} issues a fresh request. Nothing can serve a cached
 * one because nothing outlives the route to hold it.
 *
 * Three features the sibling admin stores compose are deliberately absent:
 *
 * - **No `withEntities`.** The response is one envelope with five differently-shaped row sets in
 *   it, not a keyed collection, and there is no id to key on.
 * - **No `withOffsetPagination`.** That feature models a running offset for a "Load more" list.
 *   The grouped table is a numbered window over a set already in hand, so the page lives in state
 *   here — the same call `AdminUsersStore` made, and the same reasoning: two stores sharing a
 *   shape is a coincidence, three is a feature.
 * - **No `withEventHandlers`.** Nothing in this application mutates usage, so no event could
 *   invalidate a report; the range and grouping controls are the only things that refetch it.
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
      /** Nothing on screen yet, so a skeleton is the honest thing to show. */
      isFirstLoad: computed(() => isPending() && report() === null),
      /**
       * The range genuinely holds nothing.
       *
       * A property of the report rather than of the request status. Gated on `isFulfilled` it
       * would go false the moment a re-range put the store back into `pending`, and the template
       * would fall through to the dashboard branch and paint four zero tiles, a flat ridgeline and
       * an arc-less donut for the length of the request — which is exactly the chart-of-zeroes the
       * empty state exists to prevent. `isFirstLoad` still covers the null case.
       */
      isEmpty: computed(() => {
        const loaded = report();

        return loaded !== null && loaded.totals.turns === 0;
      }),
      /** The rows the table shows, as one page of the breakdown. */
      pagedGroups: computed(() => {
        const start = (page() - 1) * USAGE_PAGE_SIZE;

        return groups().slice(start, start + USAGE_PAGE_SIZE);
      }),
      /** What `Paginator` needs, so the footer is the shared control rather than a second one. */
      totalCount: computed(() => groups().length),
      pageSize: computed(() => USAGE_PAGE_SIZE),
      /**
       * The same sentence `Paginator` renders, handed to `DataTable` for its live region.
       *
       * Duplicated deliberately and kept identical: the paginator's copy is visible and this one
       * is announced, which is why the footer sets `liveSummary` false — two live regions carrying
       * one string make a screen reader read the count twice on every page.
       */
      countSummary: computed(() => {
        const total = groups().length;
        // Always the plural, which is what `Paginator.itemLabel` is: it has no count to agree
        // with, so a singular here would make the two sentences differ on a one-row breakdown —
        // the visible footer and the announced one saying different things.
        const noun = groupingNoun(toUsageGrouping(report()?.groupBy ?? grouping()), 2);
        if (total === 0) {
          return `No ${noun}`;
        }

        const first = (page() - 1) * USAGE_PAGE_SIZE + 1;
        const last = Math.min(page() * USAGE_PAGE_SIZE, total);

        return `Showing ${first}–${last} of ${total} ${noun}`;
      }),
      /**
       * Why the breakdown may not add up to the totals above it.
       *
       * Rendered as the table's notice rather than folded into the count, because it is a fact
       * about the *server's* answer rather than about the page on screen — and a reader comparing
       * the rows against the period totals needs the reason, not a parenthetical.
       */
      truncationNotice: computed(() => {
        const loaded = report();

        return loaded?.groupsTruncated === true
          ? `Only the ${loaded.groupLimit} heaviest ${groupingNoun(toUsageGrouping(loaded.groupBy), loaded.groupLimit)} are listed. The totals above cover the whole range.`
          : null;
      }),
      /**
       * Which grouping the rows on screen belong to, which is not always what the control reads.
       *
       * Narrowed through the same transform the URL uses rather than cast: the server echoes a
       * token it validated, but a response is still input, and a value neither side recognises
       * would otherwise index the label tables with `undefined`.
       */
      renderedGrouping: computed(() => toUsageGrouping(report()?.groupBy ?? grouping())),
    };
  }),
  withMethods((store) => {
    function currentQuery(): UsageReportQuery {
      return { range: store.range(), grouping: store.grouping() };
    }

    // No `debounceTime`: neither control is a text field, so every emission is a deliberate
    // choice. `switchMap` because a reader can change the range and the grouping faster than the
    // server answers, and only the newest report may reach the screen.
    const _load = rxMethod<UsageReportQuery>(
      pipe(
        tap((query) => patchState(store, query, setPending())),
        switchMap(({ range, grouping }) => {
          // Only `from` goes on the wire. The end of the window is the server's own clock, which
          // is the clock the rows were written by — sending a `to` from here would let a browser
          // whose time is wrong decide which of them count. The start is a span back from now
          // because the URL carries a span, and that is what keeps a shared link meaning the last
          // 30 days rather than one particular fortnight in August.
          const params = new HttpParams()
            .set('from', new Date(Date.now() - range * MS_PER_DAY).toISOString())
            .set('groupBy', grouping);

          return store._http.get<UsageReportDto>(store._url, { params }).pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (report) =>
                // Back to the first page on every answer: a new range or grouping is a new table,
                // and page 3 of the old one names rows that are no longer there.
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
       * Binds the screen's `?range=` and `?group=` to the request, once, from its constructor.
       *
       * Taking a computation rather than values is what makes a deep link one request: `rxMethod`
       * subscribes through an effect, so it reads the parameters the router has already bound
       * instead of firing a default request and then the real one. Both travel in one object
       * because they go out on one request — bound separately, changing either would fire two.
       */
      bindQuery: _load,

      /** Re-reads the report after a failure, from `ErrorPanel`'s retry. */
      retry(): void {
        _load(currentQuery());
      },

      /**
       * Moves the grouped table's window.
       *
       * Screen state rather than a URL parameter, unlike the user directory's `?page=`: the table
       * is one panel of a dashboard whose address already names what it is showing, and a link
       * that restored page 3 of a breakdown while the charts above it started from scratch would
       * be restoring half a view.
       */
      setPage(page: number): void {
        patchState(store, { page: Math.min(Math.max(page, 1), store.totalPages()) });
      },
    };
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
