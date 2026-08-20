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
import { PaginatedResponseDto } from '@domain/api/paginated-response';
import { ProjectSummaryDto, toProjectSummary } from '@domain/api/project';
import { toAppError } from '@core/errors/to-app-error';
import { projectEvents } from '@core/events/project-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
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
import { PROJECT_LOAD_CEILING, PROJECT_PAGE_SIZE } from './project-load';

interface ProjectLookupState {
  /**
   * Whether {@link ProjectLookupStore.ensureLoaded} has released the drain. See the
   * `onInit` hook.
   *
   * Private: a release sentinel, not something a surface should render, and
   * `@ngrx/signals` strips `_`-prefixed members from the public store type.
   */
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
 * Every project the signed-in user owns, held once at the root (US-307).
 *
 * This is the root project list US-202's fourth criterion asked for and nothing had a
 * use for until now. It serves two things at once, and both need the *whole* set rather
 * than a page:
 *
 * - **The project picker's list**, which filters client-side as the reader types. A
 *   server round trip per keystroke would be the wrong shape for a 320px popover, and
 *   `GET api/projects` accepts no sort parameter, so a page in hand is not a stable
 *   window to filter either.
 * - **The sidebar's favourite projects** (US-910), which is a filter over the same set
 *   rather than a second request: a dedicated `?isFavorite=true` store would be a second
 *   copy of rows already held here, to be kept in step on every rename, delete and
 *   toggle. It inherits this store's ceiling — a favourite past
 *   {@link PROJECT_LOAD_CEILING} is not held and so is not listed.
 * - **`projectId` → name**, which is the only way a project chip can be rendered:
 *   `ConversationDto` carries the id and no name. This mirrors exactly how the
 *   conversations library resolves a `modelId` against `ModelCatalogStore`, down to
 *   rendering nothing rather than a guess when the id is unknown.
 *
 * It drains for the reason `ProjectListStore` does, with three of the same load-bearing
 * details — `expand` rather than a self-referential `concat`, `_querySuperseded$`
 * cancelling the whole chain, and `truncated` written where the drain stops and never in
 * a `finalize`. What it does *not* have is a term: this list is never filtered on the
 * server, so a page is only ever superseded by a retry or a sign-out.
 *
 * It also differs from the grid on what a mid-drain failure leaves behind. The grid
 * replaces itself with an error panel, because a half-drained set sorted or counted as
 * if it were whole is the failure regime A exists to avoid. Here the pages already
 * loaded are **kept** alongside the error: `nameOf` is a lookup, not a listing, and a
 * chip that can still name 200 of 500 projects is strictly better than one that names
 * none. The picker is what renders the error, and it hides the list behind it.
 *
 * Root-scoped, and therefore on the initial bundle graph, because `SessionBootstrap`
 * releases it — the same cost `ConversationListStore` already pays for the same reason.
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
     * Cancels a drain whose result set no longer exists. `switchMap` cancels the one
     * request in flight when a retry arrives; the drain is a *chain* of them, and a
     * page landing after the retry's `setAllEntities` would append rows twice.
     */
    _querySuperseded$: new Subject<void>(),
  })),
  withComputed(({ entities, entityMap, isFulfilled, isPending, totalCount }) => ({
    /** Every loaded project, in the server's order — `dateCreated` descending. */
    projects: entities,
    /**
     * The starred projects, in the same order (US-910).
     *
     * Derived rather than fetched, so a toggle anywhere in the app reaches the sidebar
     * through the one `favorited` handler below and nothing has to be refetched.
     */
    favorites: computed(() => entities().filter((project) => project.isFavorite)),
    /** Loaded, and this user owns no projects at all. */
    isEmpty: computed(() => isFulfilled() && entities().length === 0),
    /** Nothing loaded yet, so a panel shows skeletons rather than an empty list. */
    isFirstLoad: computed(() => isPending() && entities().length === 0),
    /**
     * What the picker states when the drain stopped short. Frame `2e` draws no such
     * line, but a list that silently omits projects is the failure the grid's own
     * ceiling notice exists to prevent — and here it would silently omit the project
     * the reader is looking for.
     */
    ceilingNotice: computed(
      () => `Showing the ${entities().length} most recent of ${totalCount()} projects.`,
    ),
    /**
     * Whether anything is starred. The sidebar's whole section is absent when nothing is
     * — the repo's rule for an affordance with nothing to act on.
     */
    hasFavorites: computed(() => entities().some((project) => project.isFavorite)),
    /**
     * A project's name by id, or null when it is not held — an unknown id, or one past
     * the ceiling. Callers render nothing for a null rather than an id nobody can read.
     */
    nameOf: computed(() => {
      const byId = entityMap();

      return (projectId: string | null): string | null =>
        projectId === null ? null : (byId[projectId]?.name ?? null);
    }),
  })),
  withMethods((store) => {
    function fetchPage(skip: number): Observable<PaginatedResponseDto<ProjectSummaryDto>> {
      return store._http.get<PaginatedResponseDto<ProjectSummaryDto>>(store._url, {
        params: new HttpParams().set('skip', String(skip)).set('take', String(store.take())),
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

    // Takes the release sentinel rather than a void trigger, so the drain is wired
    // declaratively in `onInit` and `ensureLoaded` only flips a flag — there is no
    // injection context in `SessionBootstrap` to bind a signal-valued rxMethod to.
    const _load = rxMethod<boolean>(
      pipe(
        // The sentinel gates the drain, exactly as `ConversationListStore`'s null does:
        // `onInit` binds a computation over it, so nothing goes out until
        // `SessionBootstrap` has a token, and `retry()` pushes `true` again on purpose.
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
              // The pages already drained stay: `setError` writes only `requestStatus`,
              // and that is deliberate here — see the class doc. The picker renders the
              // error and hides the partial list; `nameOf` keeps answering for the rows
              // it does hold.
              error: (cause: unknown) =>
                patchState(store, setError(toAppError(cause, { url: store._url }))),
            }),
          ),
        ),
      ),
    );

    return {
      _load,

      /**
       * Releases the drain. Idempotent, and called by `SessionBootstrap` after
       * `POST api/users/me` resolves — never alongside it, because the request would
       * otherwise go out before a token exists.
       */
      ensureLoaded(): void {
        if (!store._started()) {
          patchState(store, { _started: true });
        }
      },

      /** Re-runs the drain from the first page, after a failure. */
      retry(): void {
        _load(true);
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // Server-confirmed facts only, which is the whole contract of this event group.
    // Without these three the picker would offer a project that has been deleted, or
    // omit the one the reader just created from the panel they created it in.
    events.on(projectEvents.created).pipe(
      tap(({ payload }) => {
        // The head, because the server orders by `dateCreated` descending and a project
        // created now is the newest there is — where a refetch would put it.
        patchState(
          store,
          setAllEntities([toProjectSummary(payload), ...store.entities()]),
          shiftCounters(1),
        );
      }),
    ),

    events.on(projectEvents.updated).pipe(
      tap(({ payload }) => {
        // No term filters this list, so a rename never evicts a row here — unlike the
        // grid, where it can fall out of an active search.
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

    // Touches `isFavorite` and nothing else: the 204 confirms one field, and the
    // server does not bump `dateModified` for it, so there is no other value to adopt.
    // A row past the ceiling is not held and is ignored, exactly as `updated` does.
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
