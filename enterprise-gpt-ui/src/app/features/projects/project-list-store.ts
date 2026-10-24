import { HttpClient, HttpParams } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { tapResponse } from '@ngrx/operators';
import {
  PartialStateUpdater,
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import {
  addEntities,
  removeEntity,
  setAllEntities,
  updateEntity,
  withEntities,
} from '@ngrx/signals/entities';
import { Events, withEventHandlers } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { Observable, Subject, exhaustMap, merge, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { PaginatedResponseDto } from '@shared/models/api/paginated-response.model';
import { ProjectSummaryDto } from '@shared/models/api/project.model';
import { DEFAULT_PROJECT_SORT, ProjectSortOption } from '@shared/utils/navigation/projects-route';
import { matchesProjectTerm } from '@shared/utils/projects/project-load';
import { toProjectSummary } from '@shared/utils/projects/project-summary';
import { toAppError } from '@core/errors/to-app-error';
import { projectEvents } from '@core/events/project-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url-service';
import { ToastStore } from '@core/notifications/toast-store';
import {
  OffsetPaginationState,
  setFirstPage,
  setPage,
  withOffsetPagination,
} from '@core/state/with-offset-pagination';
import {
  setError,
  setFulfilled,
  setPending,
  withRequestStatus,
} from '@core/state/with-request-status';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

/**
 * Matches the conversations library's page size. Not `PROJECT_PAGE_SIZE`, which is tuned for
 * `ProjectLookupStore`'s drain.
 */
export const PROJECT_LIST_PAGE_SIZE = 25;

/** What the URL says about which projects to fetch, and in what order. */
export interface ProjectListQuery {
  readonly name: string;
  readonly sort: ProjectSortOption;
}

interface ProjectListState {
  /** The cards' `name=` filter, written at request start so the empty state names its term. */
  readonly name: string;
  /** The cards' order, which an appended page continues rather than the one on the wire. */
  readonly sort: ProjectSortOption;
  /** A next page is in flight; unlike `isPending()` it never swaps the grid for skeletons. */
  readonly loadingMore: boolean;
}

/**
 * Moves the total and `skip` together: the server no longer counts a removed row, so a stale
 * `skip` would start the next page short and skip the cards that slid into the gap.
 */
function shiftCounters(delta: number): PartialStateUpdater<OffsetPaginationState> {
  return ({ skip, totalCount }) => ({
    skip: Math.max(skip + delta, 0),
    totalCount: Math.max(totalCount + delta, 0),
  });
}

/**
 * Mirrors `ConversationLibraryStore`: URL-driven, `switchMap` so a slow query cannot paint over a
 * fast one, `takeUntil(signedOut$)` because the sign-out reset cancels nothing. Order and filter
 * are the server's on every page, so `withClientQuery` is not composed.
 */
export const ProjectListStore = signalStore(
  withState<ProjectListState>({ name: '', sort: DEFAULT_PROJECT_SORT, loadingMore: false }),
  withEntities<ProjectSummaryDto>(),
  withRequestStatus(),
  withOffsetPagination(PROJECT_LIST_PAGE_SIZE),
  withProps(() => ({
    _http: inject(HttpClient),
    _toasts: inject(ToastStore),
    _url: inject(ApiUrl).build('projects'),
    _signedOut$: injectSignedOut(),
    /**
     * Cancels a page whose result set was replaced: pages run on their own `rxMethod`, which the
     * query's `switchMap` cannot reach.
     */
    _querySuperseded$: new Subject<void>(),
  })),
  withComputed(({ entities, isFulfilled, isPending, name, totalCount }) => {
    const isEmpty = computed(() => isFulfilled() && entities().length === 0);
    const hasTerm = computed(() => name().trim() !== '');

    return {
      isEmpty,
      hasTerm,
      /** Loaded, nothing matched, and a term is why. */
      hasNoMatches: computed(() => isEmpty() && hasTerm()),
      /** Loaded, nothing matched, nothing filtering: this user has no projects at all. */
      hasNoProjects: computed(() => isEmpty() && !hasTerm()),
      /** Skeletons only when nothing is on screen; a narrowing search keeps its cards. */
      isFirstLoad: computed(() => isPending() && entities().length === 0),
      /** Counts the cards on screen rather than `skip`, so every removal path stays honest. */
      countSummary: computed(() => {
        const total = totalCount();

        return `Showing ${entities().length} of ${total} ${total === 1 ? 'project' : 'projects'}`;
      }),
    };
  }),
  withMethods((store) => {
    function pageParams(query: ProjectListQuery, skip: number): HttpParams {
      const trimmed = query.name.trim();
      // The order rides every page: the server orders each request independently.
      let params = new HttpParams()
        .set('skip', String(skip))
        .set('take', String(store.take()))
        .set('sort', query.sort.wire.sort)
        .set('dir', query.sort.wire.dir);

      // Omitted rather than sent empty: a blank `name=` still takes the server's LIKE branch.
      if (trimmed !== '') {
        params = params.set('name', trimmed);
      }

      return params;
    }

    function fetchPage(
      query: ProjectListQuery,
      skip: number,
    ): Observable<PaginatedResponseDto<ProjectSummaryDto>> {
      return store._http.get<PaginatedResponseDto<ProjectSummaryDto>>(store._url, {
        params: pageParams(query, skip),
      });
    }

    /** The query the cards on screen belong to, for a retry or a next page. */
    function currentQuery(): ProjectListQuery {
      return { name: store.name(), sort: store.sort() };
    }

    // No `distinctUntilChanged`: the signal source only emits on a real change, and
    // `retry()` pushes the same query again on purpose.
    const _runQuery = rxMethod<ProjectListQuery>(
      pipe(
        tap(({ name, sort }) => {
          store._querySuperseded$.next();
          patchState(store, { name, sort, loadingMore: false }, setPending());
        }),
        switchMap((query) =>
          fetchPage(query, 0).pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (page) =>
                patchState(store, setAllEntities(page.items), setFirstPage(page), setFulfilled()),
              error: (cause: unknown) =>
                patchState(store, setError(toAppError(cause, { url: store._url }))),
            }),
          ),
        ),
      ),
    );

    const _loadNextPage = rxMethod<void>(
      // exhaustMap: a second press mid-page is a double-click, and with `skip` unmoved it would
      // fetch the same window twice.
      exhaustMap(() => {
        patchState(store, { loadingMore: true });

        return fetchPage(currentQuery(), store.skip()).pipe(
          takeUntil(merge(store._signedOut$, store._querySuperseded$)),
          tapResponse({
            // `setPage` advances `skip` by what came back, so a short page leaves no gap.
            next: (page) => patchState(store, addEntities(page.items), setPage(page)),
            error: (cause: unknown) =>
              // A toast rather than `setError`: the cards already loaded are still valid.
              void store._toasts.fromError(toAppError(cause, { url: store._url })),
            finalize: () => patchState(store, { loadingMore: false }),
          }),
        );
      }),
    );

    return {
      /**
       * Also drops any page in flight: built on the old offset, it would never fetch the card
       * that slid into the gap. The reader can press Load more again.
       */
      _dropCard(id: string): void {
        store._querySuperseded$.next();
        patchState(store, removeEntity(id), shiftCounters(-1), { loadingMore: false });
      },

      /**
       * Bound once to `?name=` and `?sort=` as a computation, so a deep link reads the router's
       * bound params and fires one request rather than an unfiltered one first.
       */
      bindQuery: _runQuery,

      /** Re-runs the query the cards on screen belong to, after a failure. */
      retry(): void {
        _runQuery(currentQuery());
      },

      /**
       * `isPending()` blocks a page started mid-query, whose old `skip` would land after
       * `setFirstPage`; `_querySuperseded$` only cancels pages a later query overtook.
       */
      loadMore(): void {
        if (!store.isPending() && !store.loadingMore() && store.hasMore()) {
          _loadNextPage();
        }
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // Server-confirmed facts, so edits made on a project's own screen reach the grid too.
    events.on(projectEvents.created).pipe(
      tap(({ payload }) => {
        // Only if it matches the active search, or the next refetch would remove it.
        if (!matchesProjectTerm(payload.name, store.name())) {
          return;
        }

        // The head, where either date order puts it; under other orders a deliberate guess that
        // settles on the next query rather than refetching every loaded page.
        patchState(
          store,
          setAllEntities([toProjectSummary(payload), ...store.entities()]),
          shiftCounters(1),
        );
      }),
    ),

    events.on(projectEvents.updated).pipe(
      tap(({ payload }) => {
        if (store.entityMap()[payload.id] === undefined) {
          return;
        }

        // Renamed out of the active search, so it leaves the filtered grid.
        if (!matchesProjectTerm(payload.name, store.name())) {
          store._dropCard(payload.id);

          return;
        }

        patchState(store, updateEntity({ id: payload.id, changes: toProjectSummary(payload) }));
      }),
    ),

    events.on(projectEvents.deleted).pipe(
      tap(({ payload: id }) => {
        if (store.entityMap()[id] === undefined) {
          return;
        }

        store._dropCard(id);
      }),
    ),

    // A star never evicts a card (the filter is `name=`), and under Favourites first the card
    // stays put: reordering under the cursor that pressed it is worse than settling next query.
    events.on(projectEvents.favorited).pipe(
      tap(({ payload: { id, isFavorite } }) => {
        if (store.entityMap()[id] !== undefined) {
          patchState(store, updateEntity({ id, changes: { isFavorite } }));
        }
      }),
    ),
  ]),
  withHooks({
    onDestroy(store) {
      store._querySuperseded$.complete();
    },
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
