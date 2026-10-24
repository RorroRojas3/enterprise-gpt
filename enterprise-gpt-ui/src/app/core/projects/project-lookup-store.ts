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
import {
  EMPTY,
  Observable,
  Subject,
  expand,
  filter,
  merge,
  pipe,
  switchMap,
  takeUntil,
  tap,
} from 'rxjs';
import { ApiUrl } from '@core/api/api-url';
import { PaginatedResponseDto } from '@core/api/paginated-response.model';
import { ProjectSummaryDto } from '@core/api/project.model';
import {
  OffsetPaginationState,
  setFirstPage,
  setPage,
  withOffsetPagination,
} from '@core/api/with-offset-pagination';
import {
  setError,
  setFulfilled,
  setPending,
  withRequestStatus,
} from '@core/api/with-request-status';
import { toAppError } from '@core/errors/to-app-error';
import { projectEvents } from '@core/events/project-events';
import { injectSignedOut } from '@core/events/session-events';
import { withResetOnSignOut } from '@core/session/with-reset-on-sign-out';
import { PROJECT_LOAD_CEILING, PROJECT_PAGE_SIZE } from './project-load';
import { toProjectSummary } from './project-summary';

interface ProjectLookupState {
  /** Whether {@link ProjectLookupStore.ensureLoaded} has released the drain; see `onInit`. */
  readonly _started: boolean;
  /** The drain stopped at {@link PROJECT_LOAD_CEILING} with more still on the server. */
  readonly truncated: boolean;
}

/** Moves the offset and the total together, for a row that joined or left the set. */
function shiftCounters(delta: number): PartialStateUpdater<OffsetPaginationState> {
  return ({ skip, totalCount }) => ({
    skip: Math.max(skip + delta, 0),
    totalCount: Math.max(totalCount + delta, 0),
  });
}

/**
 * Every project the user owns, drained whole for the picker's filter, the sidebar's favourites
 * (complete, as `sort=favorite` puts starred rows first) and chip names. Unlike the grid, it keeps
 * pages loaded before a failure, so `nameOf` still answers. Root: `SessionBootstrap` loads it.
 */
export const ProjectLookupStore = signalStore(
  { providedIn: 'root' },
  withState<ProjectLookupState>({ _started: false, truncated: false }),
  withEntities<ProjectSummaryDto>(),
  withRequestStatus(),
  withOffsetPagination(PROJECT_PAGE_SIZE),
  withProps(() => ({
    _http: inject(HttpClient),
    _url: inject(ApiUrl).build('projects'),
    _signedOut$: injectSignedOut(),
    /**
     * Cancels a superseded drain: `switchMap` cancels only the request in flight, and a later
     * page of the old chain would append rows twice.
     */
    _querySuperseded$: new Subject<void>(),
  })),
  withComputed(({ entities, entityMap, isFulfilled, isPending, totalCount }) => ({
    /** In the last drain's order (starred, then name); later changes patch in place, not move. */
    projects: entities,
    /** Derived, so a toggle anywhere reaches the sidebar through the one `favorited` handler. */
    favorites: computed(() => entities().filter((project) => project.isFavorite)),
    /** Loaded, and this user owns no projects at all. */
    isEmpty: computed(() => isFulfilled() && entities().length === 0),
    /** Nothing loaded yet, so a panel shows skeletons rather than an empty list. */
    isFirstLoad: computed(() => isPending() && entities().length === 0),
    /** Without it the picker would silently omit the project the reader may be looking for. */
    ceilingNotice: computed(
      () => `Showing the first ${entities().length} of ${totalCount()} projects.`,
    ),
    /** The sidebar omits its whole favourites section when nothing is starred. */
    hasFavorites: computed(() => entities().some((project) => project.isFavorite)),
    /** Null for an unknown id or one past the ceiling; callers then render nothing. */
    nameOf: computed(() => {
      const byId = entityMap();

      return (projectId: string | null): string | null =>
        projectId === null ? null : (byId[projectId]?.name ?? null);
    }),
  })),
  withMethods((store) => {
    function fetchPage(skip: number): Observable<PaginatedResponseDto<ProjectSummaryDto>> {
      return store._http.get<PaginatedResponseDto<ProjectSummaryDto>>(store._url, {
        // Starred first, so the sidebar's favourites are complete despite the 500-row ceiling;
        // then by name, so the picker reads alphabetically within each group.
        params: new HttpParams()
          .set('skip', String(skip))
          .set('take', String(store.take()))
          .set('sort', 'favorite')
          .set('dir', 'desc'),
      });
    }

    function nextPage(): Observable<PaginatedResponseDto<ProjectSummaryDto>> {
      if (!store.hasMore()) {
        return EMPTY;
      }

      if (store.skip() >= PROJECT_LOAD_CEILING) {
        patchState(store, { truncated: true });

        return EMPTY;
      }

      return fetchPage(store.skip()).pipe(
        tap((page) => patchState(store, addEntities(page.items), setPage(page))),
      );
    }

    // Bound to the sentinel in `onInit`, since `SessionBootstrap` has no injection context to
    // bind a signal-valued rxMethod in; `ensureLoaded` only flips the flag.
    const _load = rxMethod<boolean>(
      pipe(
        // Nothing goes out until `SessionBootstrap` has a token; `retry()` pushes `true` again
        // on purpose.
        filter(Boolean),
        tap(() => {
          store._querySuperseded$.next();
          patchState(store, { truncated: false }, setPending());
        }),
        switchMap(() =>
          fetchPage(0).pipe(
            tap((first) =>
              patchState(store, setAllEntities(first.items), setFirstPage(first), setFulfilled()),
            ),
            expand(() => nextPage()),
            takeUntil(merge(store._signedOut$, store._querySuperseded$)),
            tapResponse({
              next: () => undefined,
              // Drained pages stay: `setError` writes only `requestStatus`, so `nameOf` answers.
              error: (cause: unknown) =>
                patchState(store, setError(toAppError(cause, { url: store._url }))),
            }),
          ),
        ),
      ),
    );

    return {
      _load,

      /** Idempotent; `SessionBootstrap` calls it after `POST api/users/me`, once a token exists. */
      ensureLoaded(): void {
        if (!store._started()) {
          patchState(store, { _started: true });
        }
      },

      retry(): void {
        _load(true);
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // Without these the picker would offer deleted projects or miss the one just created.
    events.on(projectEvents.created).pipe(
      tap(({ payload }) => {
        // Prepended, a deliberate guess shared with the grid: `sort=favorite` would file it
        // elsewhere, but the reader must find what they just created without a re-drain.
        patchState(
          store,
          setAllEntities([toProjectSummary(payload), ...store.entities()]),
          shiftCounters(1),
        );
      }),
    ),

    events.on(projectEvents.updated).pipe(
      tap(({ payload }) => {
        // No term filters this list, so a rename never evicts a row here.
        if (store.entityMap()[payload.id] !== undefined) {
          patchState(store, updateEntity({ id: payload.id, changes: toProjectSummary(payload) }));
        }
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

    // The 204 confirms only `isFavorite`. The row doesn't move: re-draining would rebuild the
    // sidebar under the cursor, and `favorites()` filters, so only its order waits.
    events.on(projectEvents.favorited).pipe(
      tap(({ payload: { id, isFavorite } }) => {
        if (store.entityMap()[id] !== undefined) {
          patchState(store, updateEntity({ id, changes: { isFavorite } }));
        }
      }),
    ),
  ]),
  withHooks({
    onInit(store) {
      // An injection context, which a signal-valued rxMethod argument requires.
      store._load(store._started);
    },
    onDestroy(store) {
      store._querySuperseded$.complete();
    },
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
