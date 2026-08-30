import { HttpClient, HttpParams } from '@angular/common/http';
import { computed, inject } from '@angular/core';
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
import { tapResponse } from '@ngrx/operators';
import { Observable, Subject, exhaustMap, merge, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { PaginatedResponseDto } from '@domain/api/paginated-response';
import { ProjectSummaryDto, toProjectSummary } from '@domain/api/project';
import { toAppError } from '@core/errors/to-app-error';
import { projectEvents } from '@core/events/project-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import { ToastStore } from '@core/notifications/toast-store';
import { matchesProjectTerm } from '@core/projects/project-load';
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
import { DEFAULT_PROJECT_SORT, ProjectSortOption } from './projects-route';

/**
 * How many cards the grid asks for at a time, matching the conversations library's own
 * page size so paging feels the same on both screens.
 *
 * Its own constant rather than `core/projects/project-load`'s `PROJECT_PAGE_SIZE`: that
 * one is chosen so `ProjectLookupStore`'s drain costs the fewest round trips, and this
 * one is chosen for a grid with a Load more control beneath it.
 */
export const PROJECT_LIST_PAGE_SIZE = 25;

/** What the URL says about which projects to fetch, and in what order (US-901, US-902). */
export interface ProjectListQuery {
  readonly name: string;
  readonly sort: ProjectSortOption;
}

interface ProjectListState {
  /**
   * The `name=` filter the cards on screen belong to.
   *
   * Written when the request starts, not when it commits, so the empty state can name
   * the term that produced it rather than the one still being typed.
   */
  readonly name: string;
  /**
   * The order the cards on screen are in.
   *
   * Held for the same reason as {@link name}: an appended page continues the order the
   * cards on screen are already in, not the one still on the wire.
   */
  readonly sort: ProjectSortOption;
  /**
   * A *next page* is in flight, which is not the same as `isPending()`.
   *
   * A query replaces the cards and shows skeletons; a page appends to them and only
   * marks the Load more control busy. Keeping them apart is what stops a second page
   * from blanking a grid the reader is already looking at.
   */
  readonly loadingMore: boolean;
}

/**
 * Moves the offset and the total together, for a row that joined or left the set.
 *
 * Both, for the reason the conversations library documents: the total because "Showing
 * N of M" would otherwise count rows that are gone, and the **offset** because the
 * server no longer counts them either — leaving `skip` where it was would make the next
 * page start short, and the cards that slid into the gap would never be fetched.
 */
function shiftCounters(delta: number): PartialStateUpdater<OffsetPaginationState> {
  return ({ skip, totalCount }) => ({
    skip: Math.max(skip + delta, 0),
    totalCount: Math.max(totalCount + delta, 0),
  });
}

/**
 * The projects grid's list, from `GET api/projects` (US-901, frame `4c`).
 *
 * It copies `ConversationLibraryStore` — route-scoped, URL-driven, `rxMethod` +
 * `switchMap` so a slow `"data"` cannot paint over a fast `"data platform"`, no
 * `debounceTime` because `SearchInput` already debounces and emits only committed
 * values, and `takeUntil(signedOut$)` on the request because `withResetOnSignOut`
 * clears state and does not cancel work.
 *
 * **This store used to drain to a 500-project ceiling, and this comment used to say a
 * Load more control would never exist here — a card grid growing a page at a time would
 * keep moving the reader's cursor.** The product owner reversed that on 2026-08-29: the
 * cursor cost is real but bounded, while the drain rendered up to 500 cards nobody had
 * asked for and hid every project past the ceiling behind a sentence with no way out of
 * it. The record stays because the reasoning was sound at the time, not because it still
 * decides anything.
 *
 * **The order is the server's, not a comparator here (US-902).** `sort=` and `dir=` ride
 * the first page and every appended one alike, which is what makes a Load more page a
 * continuation of the same run rather than a second sorted run underneath the first.
 *
 * **`withClientQuery` is not composed.** Filtering here is already server-side, so the
 * feature would contribute a second filter over an already-filtered set and a comparator
 * nothing would call — and under paging it would narrow only the loaded window.
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
     * Cancels a page whose result set no longer exists.
     *
     * `switchMap` already cancels one *query* for the next, but a page is a separate
     * request on a separate `rxMethod`: without this, a page fetched against the old
     * term lands after the new query's `setAllEntities` and appends a window from a
     * grid that is gone.
     */
    _querySuperseded$: new Subject<void>(),
  })),
  withComputed(({ entities, isFulfilled, isPending, name, totalCount }) => {
    const isEmpty = computed(() => isFulfilled() && entities().length === 0);
    const hasTerm = computed(() => name().trim() !== '');

    return {
      isEmpty,
      hasTerm,
      /** Loaded, nothing matched, and a term is why — frame `4b`'s shape. */
      hasNoMatches: computed(() => isEmpty() && hasTerm()),
      /** Loaded, nothing matched, nothing filtering: this user has no projects at all. */
      hasNoProjects: computed(() => isEmpty() && !hasTerm()),
      /**
       * Nothing on screen yet, so skeletons are the honest thing to show. A search that
       * narrows an existing grid keeps its cards and marks the field busy instead — the
       * distinction `Sidebar` and the conversations library both draw.
       */
      isFirstLoad: computed(() => isPending() && entities().length === 0),
      /**
       * The loaded half counts the cards actually on screen rather than reading `skip`:
       * the two agree after a page, but a card deleted from anywhere decrements both
       * counters, and counting cards keeps this honest whichever path moved them.
       */
      countSummary: computed(() => {
        const total = totalCount();

        return `Showing ${entities().length} of ${total} ${total === 1 ? 'project' : 'projects'}`;
      }),
    };
  }),
  withMethods((store) => {
    function pageParams(query: ProjectListQuery, skip: number): HttpParams {
      const trimmed = query.name.trim();
      // The order rides *every* page, not just the first: the server orders each request
      // independently, so an appended page fetched without it would splice a
      // differently-ordered run underneath the first.
      let params = new HttpParams()
        .set('skip', String(skip))
        .set('take', String(store.take()))
        .set('sort', query.sort.wire.sort)
        .set('dir', query.sort.wire.dir);

      // Omitted rather than sent empty: a blank `name=` still takes the server down its
      // LIKE branch, and US-901's second criterion is about a term that exists.
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
      // exhaustMap: a second press while a page is on the wire is a double-click, not a
      // request for the page after it. `skip` has not moved yet, so letting it through
      // would fetch the same window twice.
      exhaustMap(() => {
        patchState(store, { loadingMore: true });

        return fetchPage(currentQuery(), store.skip()).pipe(
          takeUntil(merge(store._signedOut$, store._querySuperseded$)),
          tapResponse({
            // addEntities, not setAllEntities: this page joins the cards already on
            // screen. `setPage` advances `skip` by what came back rather than by `take`,
            // so a short page cannot leave a gap.
            next: (page) => patchState(store, addEntities(page.items), setPage(page)),
            error: (cause: unknown) =>
              // A toast rather than `setError`: the cards already loaded are still valid,
              // and swapping the grid for an error panel would throw away a result set
              // the reader can still use.
              void store._toasts.fromError(toAppError(cause, { url: store._url })),
            finalize: () => patchState(store, { loadingMore: false }),
          }),
        );
      }),
    );

    return {
      /**
       * Removes a card that has left the result set, and keeps paging coherent.
       *
       * The cancellation is the subtle half. A page already on the wire had its URL built
       * against the pre-removal offset, so once `skip` moves down beneath it that response
       * appends the *next* window and the card that slid into the gap is fetched by
       * neither request — a permanent hole, with the counter reading as if nothing were
       * missing. Dropping the page is the cheaper loss: the reader can press Load more
       * again.
       */
      _dropCard(id: string): void {
        store._querySuperseded$.next();
        patchState(store, removeEntity(id), shiftCounters(-1), { loadingMore: false });
      },

      /**
       * Binds the screen's `?name=` and `?sort=` to the query, once, from its constructor.
       *
       * Taking a computation rather than a value is what makes a deep link one request:
       * `rxMethod` subscribes through an effect, so it reads the parameters the router
       * has already bound instead of firing unfiltered first and filtered second.
       */
      bindQuery: _runQuery,

      /** Re-runs the query the cards on screen belong to, after a failure. */
      retry(): void {
        _runQuery(currentQuery());
      },

      /**
       * Appends the next page in server order.
       *
       * `isPending()` is the load-bearing half of the guard. `_querySuperseded$` cancels
       * a page a *later* query overtook, but it cannot help a page started *after* a
       * query was already on the wire: that request carries the old `skip` against the
       * new result set, and when it lands after `setFirstPage` it appends a window from
       * the middle of a grid that no longer exists.
       */
      loadMore(): void {
        if (!store.isPending() && !store.loadingMore() && store.hasMore()) {
          _loadNextPage();
        }
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // Server-confirmed facts only, which is the whole contract of this event group.
    // A project created or renamed from its own detail screen has to land here too, or
    // the grid disagrees with the row the user just changed.
    events.on(projectEvents.created).pipe(
      tap(({ payload }) => {
        // Only when it belongs to what is on screen. The server filters by a substring
        // of `name`, so a project created under an active search that does not match it
        // would be a row the very next refetch removes.
        if (!matchesProjectTerm(payload.name, store.name())) {
          return;
        }

        // The head of the list, which is where a refetch would put it under either date
        // order. Under Alphabetical or Favourites first it is a guess, and deliberately
        // the same guess: the reader just made this project and wants to see it, and the
        // alternative is refetching every loaded page to move one card. The order settles
        // on the next query.
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

        // Renamed out of the active search: the card no longer belongs to the query
        // that produced it, so it leaves rather than sitting in a filtered grid it does
        // not match — the rule US-703 applies to an unstarred conversation.
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

    // Touches `isFavorite` and nothing else. Unlike `updated` above, a star can never
    // evict a card: this grid filters on `name=`, which a favourite does not change.
    //
    // It *can* change the order, under Favourites first — and the card still does not
    // move. Refetching every loaded page because the reader starred one card would
    // rearrange the grid under the cursor that pressed the star, which is a worse answer
    // than an order that settles on the next query. The other three orders are unaffected:
    // the server does not bump `dateModified` for a favourite either.
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
