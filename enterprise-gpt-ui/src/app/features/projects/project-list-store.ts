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
import { EMPTY, Observable, Subject, expand, merge, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { PaginatedResponseDto } from '@domain/api/paginated-response';
import { ProjectSummaryDto, toProjectSummary } from '@domain/api/project';
import { toAppError } from '@core/errors/to-app-error';
import { projectEvents } from '@core/events/project-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import {
  PROJECT_LOAD_CEILING,
  PROJECT_PAGE_SIZE,
  matchesProjectTerm,
} from '@core/projects/project-load';
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

// Re-exported so this screen's own module still reads as one contract; they moved to
// `core/projects/` when US-307's root lookup store needed them, and `core` may not
// import `features`.
export { PROJECT_LOAD_CEILING, PROJECT_PAGE_SIZE } from '@core/projects/project-load';

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
   * Held for the same reason as {@link name}: the ceiling notice names it, and naming
   * the order still on the wire would describe a grid nobody is looking at yet.
   */
  readonly sort: ProjectSortOption;
  /** The drain stopped at {@link PROJECT_LOAD_CEILING} with more still on the server. */
  readonly truncated: boolean;
}

/**
 * Moves the offset and the total together, for a row that joined or left the set.
 *
 * Both, for the reason the conversations library documents: the total because the
 * ceiling notice would otherwise count rows that are gone, and the **offset** because
 * it is how many rows are held — and `hasMore` compares the two.
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
 * clears state and does not cancel work — with one structural difference: **it drains
 * rather than pages.**
 *
 * There is no Load more here and there never will be: the grid is a card grid, and one
 * that grew a page at a time would keep moving the reader's cursor. So each query walks
 * the pages itself, up to a stated ceiling, and {@link ProjectListState.truncated} is
 * what stops the shortfall being silent.
 *
 * **The order is the server's, not a comparator here (US-902).** `sort=` and `dir=` ride
 * every drained page, so the 500 the ceiling admits are the correct first 500 *in the
 * chosen order* — which is why the select is never disabled and why frame `4d`'s
 * "Sorting is unavailable…" line is not the one {@link ceilingNotice} renders.
 *
 * **`withClientQuery` is therefore not composed.** The store's doc block used to forecast
 * that it would be, over `entities` with `isAuthoritative: isFulfilled() &&
 * isFullyLoaded()`; that forecast was written when no endpoint accepted a sort parameter.
 * US-706 changed the premise, and filtering here was already server-side, so the feature
 * would contribute a second filter over an already-filtered set and a comparator nothing
 * would call.
 */
export const ProjectListStore = signalStore(
  withState<ProjectListState>({ name: '', sort: DEFAULT_PROJECT_SORT, truncated: false }),
  withEntities<ProjectSummaryDto>(),
  withRequestStatus(),
  withOffsetPagination(PROJECT_PAGE_SIZE),
  withProps(() => ({
    _http: inject(HttpClient),
    _url: inject(ApiUrl).build('projects'),
    _signedOut$: injectSignedOut(),
    /**
     * Cancels a drain whose result set no longer exists.
     *
     * `switchMap` cancels the one request in flight when a new query arrives, but the
     * drain is a *chain* of them: without this, a page fetched against the old term
     * lands after the new query's `setAllEntities` and appends rows from a list that
     * is gone.
     */
    _querySuperseded$: new Subject<void>(),
  })),
  withComputed(({ entities, isFulfilled, isPending, name, sort, totalCount }) => {
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
       * What the grid is showing when the drain stopped short, and in what order.
       *
       * Neither of frame `4d`'s two claims survives US-706: the sort is not unavailable
       * — the server applied it to the whole set before the ceiling cut it — and "the
       * 500 most recent" is only true under one of the four orders on offer. What is
       * left is a truncation, so the sentence states the truncation and names the order
       * that produced it.
       */
      ceilingNotice: computed(
        () =>
          `Showing the first ${entities().length} of ${totalCount()} projects, ${sort().order}.`,
      ),
    };
  }),
  withMethods((store) => {
    function pageParams(query: ProjectListQuery, skip: number): HttpParams {
      const trimmed = query.name.trim();
      // The order rides *every* page, not just the first: the drain is a sequence of
      // requests and the server orders each one independently, so a page fetched without
      // it would splice a differently-ordered run into the middle of the grid.
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

    /**
     * The next step of the drain, or its end.
     *
     * The ceiling is recorded here rather than in a `finalize`, so it is written only
     * when the drain genuinely stopped short — a `finalize` would also run on the
     * cancellation a new query or a sign-out causes, and stamp a partial load as a
     * truncated one.
     */
    function nextPage(
      query: ProjectListQuery,
    ): Observable<PaginatedResponseDto<ProjectSummaryDto>> {
      if (!store.hasMore()) {
        return EMPTY;
      }

      if (store.skip() >= PROJECT_LOAD_CEILING) {
        patchState(store, { truncated: true });

        return EMPTY;
      }

      return fetchPage(query, store.skip()).pipe(
        tap((page) => patchState(store, addEntities(page.items), setPage(page))),
      );
    }

    // No `distinctUntilChanged`: the signal source only emits on a real change, and
    // `retry()` pushes the same query again on purpose.
    const _runQuery = rxMethod<ProjectListQuery>(
      pipe(
        tap(({ name, sort }) => {
          store._querySuperseded$.next();
          patchState(store, { name, sort, truncated: false }, setPending());
        }),
        switchMap((query) =>
          fetchPage(query, 0).pipe(
            tap((first) =>
              patchState(store, setAllEntities(first.items), setFirstPage(first), setFulfilled()),
            ),
            // `expand` rather than a self-referential `concat`: each iteration completes
            // and is dropped, so a five-page drain does not retain five nested
            // subscribers — the same reason `UploadStatusClient.follow` uses it.
            expand(() => nextPage(query)),
            // On the whole chain, not the first request: `switchMap` cancels only what
            // is in flight when the next query arrives, and the drain is a sequence.
            takeUntil(merge(store._signedOut$, store._querySuperseded$)),
            tapResponse({
              // Each page is written by the taps above; this arm exists so that a
              // failure mid-drain does not tear down the rxMethod's own subscription.
              next: () => undefined,
              // Whichever page failed, the panel replaces the grid: a half-drained set
              // sorted or counted as if it were whole is the failure regime A exists to
              // avoid, and Retry re-runs the query from the first page.
              error: (cause: unknown) =>
                patchState(store, setError(toAppError(cause, { url: store._url }))),
            }),
          ),
        ),
      ),
    );

    return {
      /**
       * Binds the screen's `?name=` and `?sort=` to the query, once, from its constructor.
       *
       * Taking a computation rather than a value is what makes a deep link one drain:
       * `rxMethod` subscribes through an effect, so it reads the parameters the router
       * has already bound instead of firing unfiltered first and filtered second.
       */
      bindQuery: _runQuery,

      /** Re-runs the query the cards on screen belong to, after a failure. */
      retry(): void {
        _runQuery({ name: store.name(), sort: store.sort() });
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
        // alternative is re-draining up to five pages to move one card. The order settles
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
          patchState(store, removeEntity(payload.id), shiftCounters(-1));

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

        patchState(store, removeEntity(id), shiftCounters(-1));
      }),
    ),

    // Touches `isFavorite` and nothing else. Unlike `updated` above, a star can never
    // evict a card: this grid filters on `name=`, which a favourite does not change.
    //
    // It *can* change the order, under Favourites first — and the card still does not
    // move. Re-draining up to five pages because the reader starred one card would
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
