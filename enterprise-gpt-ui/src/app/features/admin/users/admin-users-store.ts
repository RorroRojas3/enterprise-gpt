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
import { removeEntity, setAllEntities, updateEntity, withEntities } from '@ngrx/signals/entities';
import { Events, withEventHandlers } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { pipe, switchMap, takeUntil, tap } from 'rxjs';
import { PaginatedResponseDto } from '@domain/api/paginated-response';
import { UserDto } from '@domain/api/user';
import { toAppError } from '@core/errors/to-app-error';
import { adminEvents } from '@core/events/admin-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import {
  setError,
  setFulfilled,
  setPending,
  withRequestStatus,
} from '@core/state/with-request-status';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';
import { DEFAULT_USER_PAGE_SIZE } from './admin-users-route';

/** Everything the URL says about which page of the directory to fetch (US-1201). */
export interface AdminUsersQuery {
  readonly name: string;
  readonly page: number;
  readonly pageSize: number;
}

interface AdminUsersState {
  /**
   * The `name=` filter the rows on screen belong to.
   *
   * Written when the request starts rather than when it commits, so the empty state
   * names the term that produced it and not the one still being typed.
   */
  readonly name: string;
  /** The 1-based page those rows belong to, on the same terms. */
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
  /**
   * Bumped by every query, so an optimistic removal knows which result set it came out
   * of. Restoring a row into a page the reader has since replaced would splice one
   * search's row into another's.
   */
  readonly _queryGeneration: number;
}

/** What {@link AdminUsersStore} keeps in order to undo an optimistic removal. */
export interface RemovedUserRow {
  readonly user: UserDto;
  readonly index: number;
  readonly generation: number;
}

/**
 * The administration user directory, from `GET api/users` (US-1201).
 *
 * **It does not compose `withOffsetPagination`, and that is the store's one structural
 * decision.** That feature models a running offset — `skip` is how many rows have been
 * loaded, `setPage` advances it by what came back, and `hasMore` compares the two — which
 * is the shape a "Load more" list needs. A numbered pager is a different model: `skip` is
 * `(page − 1) × pageSize`, and a page *replaces* the rows rather than appending to them.
 * Reinterpreting `skip` to mean the request offset would break `hasMore` and
 * `isFullyLoaded` for the four stores that read them as the feature defines them, so the
 * page window lives here instead — inline rather than as a sixth `core/state/` feature,
 * on the rule `ConversationListStore` already states: two stores sharing a shape is a
 * coincidence, three is a feature. This is the first.
 *
 * Everything else copies `ConversationLibraryStore`, including the reasons: `rxMethod` +
 * `switchMap` because typing drives it and a slow `"an"` must not paint over a fast
 * `"anders"`; no `debounceTime`, because `SearchInput` already debounces and emits only
 * committed values; and `takeUntil(signedOut$)` on the inner request, because
 * `withResetOnSignOut` clears state and does not cancel work.
 *
 * Rows keep the server's order — last name, then first name. No sort control is offered
 * here, but that is now the screen's own gap rather than the API's: US-706 put `sort=` and
 * `dir=` on `GET api/users`, so wiring one is a route-and-template change on this tab.
 */
export const AdminUsersStore = signalStore(
  withState<AdminUsersState>({
    name: '',
    page: 1,
    pageSize: DEFAULT_USER_PAGE_SIZE,
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
  withComputed(({ entities, name, page, pageSize, totalCount, isFulfilled, isPending }) => {
    const isEmpty = computed(() => isFulfilled() && entities().length === 0);

    return {
      /** Loaded, and there is genuinely nothing — distinct from "not loaded yet". */
      isEmpty,
      /** Loaded, nothing matched, and a term is why — the empty state that can quote it. */
      hasNoMatches: computed(() => isEmpty() && name().trim() !== ''),
      /**
       * Loaded, nothing on this page, but there are rows on an earlier one.
       *
       * A deep link to `?page=99`, or a page emptied by deactivations since the link was
       * shared. It is its own state because the way out of it is a page change, not a
       * cleared search — and it is tested **before** `hasNoMatches` in the template, so a
       * filtered link past the end explains the page rather than blaming the term.
       *
       * `page() > 1` is what keeps the two apart: page 1 of a search with no results is a
       * failed search, whatever the unfiltered directory holds.
       */
      isPastEnd: computed(() => isEmpty() && totalCount() > 0 && page() > 1),
      /**
       * Nothing on screen yet, so a skeleton is the honest thing to show. A search that
       * narrows an existing list keeps its rows and marks the field busy instead.
       */
      isFirstLoad: computed(() => isPending() && entities().length === 0),
      /**
       * Derived rather than read from `PaginatedResponseDto.totalPages`.
       *
       * The server computes the same figure, but from the page size *it* used; deriving
       * it from the size this store is about to request next keeps the pager and the
       * request in step in the gap between a size change and its response.
       */
      totalPages: computed(() => Math.max(1, Math.ceil(totalCount() / pageSize()))),
      /**
       * Frame `5a`'s footer count, in the wording `Paginator` composes it in.
       *
       * Handed to `DataTable.summary` as well, which is why the paginator's own copy is
       * told not to announce: two live regions carrying one sentence read it twice.
       */
      countSummary: computed(() => {
        const total = totalCount();
        const noun = total === 1 ? 'user' : 'users';
        if (total === 0) {
          return 'No users';
        }

        const first = (page() - 1) * pageSize() + 1;

        // A page past the end has no range to state. Left unclamped this reads "Showing
        // 2451–312 of 312 users" — visibly, and in the table's live region, right beside
        // the empty state explaining that the page does not exist.
        if (first > total) {
          return `No users on this page — ${total} ${noun} in total`;
        }

        return `Showing ${first}–${Math.min(page() * pageSize(), total)} of ${total} ${noun}`;
      }),
    };
  }),
  withMethods((store) => {
    function searchUrl({ name, page, pageSize }: AdminUsersQuery): {
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

      return { url: store._url, params };
    }

    /** The query the rows on screen belong to, for a retry. */
    function currentQuery(): AdminUsersQuery {
      return { name: store.name(), page: store.page(), pageSize: store.pageSize() };
    }

    // No `distinctUntilChanged`: the signal source only emits on a real change, and
    // `retry()` pushes the same query again on purpose.
    const _runQuery = rxMethod<AdminUsersQuery>(
      pipe(
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
              // The envelope's `pageSize` is deliberately not adopted back into state.
              // Every size this screen can ask for is inside the server's 1–100 clamp,
              // so the echo is always what was sent — and adopting it would let a
              // response overwrite a size the URL has since changed.
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
      /**
       * Re-reads the page on screen.
       *
       * What a creation does to this list, rather than an insertion: rows are ordered by
       * last name then first name, so the new user's slot is the server's to decide — and
       * on any page but the one it belongs to, it must not appear at all.
       */
      refresh(): void {
        _runQuery(currentQuery());
      },

      /**
       * Binds the screen's `?name=`, `?page=` and `?size=` to the query, once, from its
       * constructor.
       *
       * Taking a computation rather than a value is what makes a deep link one request:
       * `rxMethod` subscribes through an effect, so it reads the parameters the router
       * has already bound instead of firing unfiltered first and filtered second. All
       * three travel in one object because they go out on one request — binding them
       * separately would fire a second query for every change to any of them.
       */
      bindQuery: _runQuery,

      /** Re-runs the query the rows on screen belong to, after a failure. */
      retry(): void {
        _runQuery(currentQuery());
      },

      /**
       * Removes a row optimistically, keeping enough to put it back where it was.
       *
       * The page is left one row short rather than refetched. A refetch would repaint a
       * table the administrator is looking at and pull an unrelated row up from the next
       * page, which reads as if the deactivation had touched two people.
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
    // Server-confirmed facts only, which is the whole contract of this event group. The
    // flows themselves live in the root `UserActionsStore`, which outlives this route and
    // cannot reach into it — `core/` may not import from `features/`.
    events.on(adminEvents.userCreated).pipe(tap(() => store.refresh())),

    // Patched in place rather than refetched: frame `5c` edits permissions only, so the
    // sort key — last name, then first name — cannot have moved.
    events.on(adminEvents.userUpdated).pipe(
      tap(({ payload }) => {
        if (store.entityMap()[payload.id] === undefined) {
          return;
        }

        patchState(store, updateEntity({ id: payload.id, changes: payload }));
      }),
    ),

    // The row is already gone — this screen removed it optimistically and handed the undo
    // to the request. What this covers is a deactivation performed from anywhere else,
    // and it is idempotent by the guard above.
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
