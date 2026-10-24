import { HttpClient, HttpParams } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { tapResponse } from '@ngrx/operators';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { removeEntity, setAllEntities, updateEntity, withEntities } from '@ngrx/signals/entities';
import { Events, withEventHandlers } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { filter, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ApiUrl } from '@core/api/api-url';
import { PaginatedResponseDto } from '@core/api/paginated-response.model';
import { UserDto } from '@core/api/user.model';
import {
  setError,
  setFulfilled,
  setPending,
  withRequestStatus,
} from '@core/api/with-request-status';
import { toAppError } from '@core/errors/to-app-error';
import { adminEvents } from '@core/events/admin-events';
import { injectSignedOut } from '@core/events/session-events';
import { withResetOnSignOut } from '@core/session/with-reset-on-sign-out';
import { DEFAULT_USER_PAGE_SIZE } from './admin-users-route';

/** Everything the URL says about which page of the directory to fetch. */
export interface AdminUsersQuery {
  readonly name: string;
  readonly page: number;
  readonly pageSize: number;
  /** Already checked against the catalog by the caller; `null` for the whole directory. */
  readonly permissionId: string | null;
}

interface AdminUsersState {
  /** Written when the request starts, so the empty state names the term that produced it. */
  readonly name: string;
  /** The 1-based page those rows belong to, on the same terms. */
  readonly page: number;
  readonly pageSize: number;
  /** The `permissionId=` filter those rows belong to, on the same terms. */
  readonly permissionId: string | null;
  readonly totalCount: number;
  /** Bumped per query, so an undo never splices one search's row into another's results. */
  readonly _queryGeneration: number;
}

/** What {@link AdminUsersStore} keeps in order to undo an optimistic removal. */
export interface RemovedUserRow {
  readonly user: UserDto;
  readonly index: number;
  readonly generation: number;
}

/**
 * Not `withOffsetPagination`, which models a "Load more" running offset; a numbered pager
 * replaces the rows per page. No `debounceTime`: `SearchInput` already emits committed values.
 */
export const AdminUsersStore = signalStore(
  withState<AdminUsersState>({
    name: '',
    page: 1,
    pageSize: DEFAULT_USER_PAGE_SIZE,
    permissionId: null,
    totalCount: 0,
    _queryGeneration: 0,
  }),
  withEntities<UserDto>(),
  withRequestStatus(),
  withProps(() => ({
    _http: inject(HttpClient),
    _url: inject(ApiUrl).build('users'),
    _signedOut$: injectSignedOut(),
  })),
  withComputed(
    ({ entities, name, page, pageSize, permissionId, totalCount, isFulfilled, isPending }) => {
      const isEmpty = computed(() => isFulfilled() && entities().length === 0);

      return {
        /** Loaded, and there is genuinely nothing — distinct from "not loaded yet". */
        isEmpty,
        /** Loaded, nothing matched, and either filter is why, so the empty state can name it. */
        hasNoMatches: computed(
          () => isEmpty() && (name().trim() !== '' || permissionId() !== null),
        ),
        /**
         * An empty page past the end, whose way out is a page change; the template tests it
         * before `hasNoMatches`. An empty page 1 is a failed search instead.
         */
        isPastEnd: computed(() => isEmpty() && totalCount() > 0 && page() > 1),
        /** Nothing on screen yet; a search over existing rows keeps them and marks busy. */
        isFirstLoad: computed(() => isPending() && entities().length === 0),
        /**
         * Derived rather than the server's `totalPages`, so the pager follows a size change
         * before its response arrives.
         */
        totalPages: computed(() => Math.max(1, Math.ceil(totalCount() / pageSize()))),
        /**
         * Also handed to `DataTable.summary`, which is why the paginator's own copy does not
         * announce: two live regions would read it twice.
         */
        countSummary: computed(() => {
          const total = totalCount();
          const noun = total === 1 ? 'user' : 'users';
          if (total === 0) {
            return 'No users';
          }

          const first = (page() - 1) * pageSize() + 1;

          // A page past the end has no range; unclamped it would read "Showing 2451–312 of 312".
          if (first > total) {
            return `No users on this page — ${total} ${noun} in total`;
          }

          return `Showing ${first}–${Math.min(page() * pageSize(), total)} of ${total} ${noun}`;
        }),
      };
    },
  ),
  withMethods((store) => {
    function searchUrl({ name, page, pageSize, permissionId }: AdminUsersQuery): {
      url: string;
      params: HttpParams;
    } {
      const trimmed = name.trim();
      let params = new HttpParams()
        .set('skip', String((page - 1) * pageSize))
        .set('take', String(pageSize));

      // Omitted rather than sent empty: a blank `name=` still takes the server down its
      // LIKE branch, which is a table scan over every active user for no filter at all.
      if (trimmed !== '') {
        params = params.set('name', trimmed);
      }

      // The API's own spelling, which its validation problem is keyed on; only the URL renames.
      if (permissionId !== null) {
        params = params.set('permissionId', permissionId);
      }

      return { url: store._url, params };
    }

    /** The query the rows on screen belong to, for a retry. */
    function currentQuery(): AdminUsersQuery {
      return {
        name: store.name(),
        page: store.page(),
        pageSize: store.pageSize(),
        permissionId: store.permissionId(),
      };
    }

    // No `distinctUntilChanged`: the signal source only emits on a real change, and
    // `retry()` pushes the same query again on purpose.
    const _runQuery = rxMethod<AdminUsersQuery | null>(
      pipe(
        // `null` means "not yet": sending unfiltered first would flash the whole directory
        // before narrowing it to the linked permission.
        filter((query): query is AdminUsersQuery => query !== null),
        tap((query) =>
          patchState(
            store,
            query,
            (state) => ({ _queryGeneration: state._queryGeneration + 1 }),
            setPending(),
          ),
        ),
        switchMap((query) => {
          const { url, params } = searchUrl(query);

          return store._http.get<PaginatedResponseDto<UserDto>>(url, { params }).pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              // The echoed `pageSize` is not adopted: every size sent is inside the clamp, and
              // adopting it could overwrite a size the URL has since changed.
              next: (response) =>
                patchState(
                  store,
                  setAllEntities(response.items),
                  { totalCount: response.totalCount },
                  setFulfilled(),
                ),
              error: (cause: unknown) => patchState(store, setError(toAppError(cause, { url }))),
            }),
          );
        }),
      ),
    );

    return {
      /** Re-reads the page; a created user's slot and page are the server's to decide. */
      refresh(): void {
        _runQuery(currentQuery());
      },

      /**
       * Bound once to one computed query, so a deep link is one request. `null` withholds the
       * query until the permission catalog confirms `?permission=`.
       */
      bindQuery: _runQuery,

      /** Re-runs the query the rows on screen belong to, after a failure. */
      retry(): void {
        _runQuery(currentQuery());
      },

      /**
       * Leaves the page one row short rather than refetching, which would pull up an unrelated
       * row and read as if the deactivation touched two people.
       */
      takeRow(id: string): RemovedUserRow | null {
        const index = store.entities().findIndex((user) => user.id === id);
        const user = store.entityMap()[id];
        if (user === undefined || index < 0) {
          return null;
        }

        patchState(store, removeEntity(id), (state) => ({
          totalCount: Math.max(state.totalCount - 1, 0),
        }));

        return { user, index, generation: store._queryGeneration() };
      },

      /** Puts an optimistically removed row back, if the page it came from is still up. */
      restoreRow(removed: RemovedUserRow | null): void {
        if (removed === null || removed.generation !== store._queryGeneration()) {
          return;
        }

        const next = [...store.entities()];
        next.splice(Math.min(removed.index, next.length), 0, removed.user);

        patchState(store, setAllEntities(next), (state) => ({
          totalCount: state.totalCount + 1,
        }));
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    events.on(adminEvents.userCreated).pipe(tap(() => store.refresh())),

    // Patched in place: the edit panel changes permissions only, so the sort key cannot move.
    events.on(adminEvents.userUpdated).pipe(
      tap(({ payload }) => {
        if (store.entityMap()[payload.id] === undefined) {
          return;
        }

        patchState(store, updateEntity({ id: payload.id, changes: payload }));
      }),
    ),

    // This screen's own removal was optimistic; this covers a deactivation made elsewhere.
    events.on(adminEvents.userDeactivated).pipe(
      tap(({ payload: id }) => {
        if (store.entityMap()[id] === undefined) {
          return;
        }

        store.takeRow(id);
      }),
    ),
  ]),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
