import { HttpClient, HttpParams } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { tapResponse } from '@ngrx/operators';
import {
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
  prependEntity,
  removeEntity,
  setAllEntities,
  updateEntity,
  withEntities,
} from '@ngrx/signals/entities';
import { Events, withEventHandlers } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { Subject, exhaustMap, filter, merge, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ConversationDto } from '@shared/models/api/conversation.model';
import { PaginatedResponseDto } from '@shared/models/api/paginated-response.model';
import { toAppError } from '@core/errors/to-app-error';
import { projectEvents } from '@core/events/project-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url-service';
import { ToastStore } from '@core/notifications/toast-store';
import { setFirstPage, setPage, withOffsetPagination } from '@core/state/with-offset-pagination';
import { addPendingId, removePendingId, withPendingIds } from '@core/state/with-pending-ids';
import {
  setError,
  setFulfilled,
  setPending,
  withRequestStatus,
} from '@core/state/with-request-status';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

/** Within the server's 1–100 clamp, and high enough that most users never reach "Load more". */
export const SIDEBAR_PAGE_SIZE = 50;

interface ConversationListState {
  /** The `name=` filter currently in force. Empty means unfiltered. */
  readonly name: string;
  /** Whether `ensureLoaded` has released the query; a sentinel, so private. */
  readonly _started: boolean;
  /**
   * A page beyond the first is in flight. Separate from `requestStatus` so the loaded
   * rows stay on screen instead of collapsing back to a skeleton.
   */
  readonly loadingMore: boolean;
  /**
   * Bumped per query, so `restoreRow` can refuse a {@link RemovedRow} from a result set no
   * longer on screen.
   */
  readonly _queryGeneration: number;
}

const initialState: ConversationListState = {
  name: '',
  _started: false,
  loadingMore: false,
  _queryGeneration: 0,
};

/** What {@link ConversationListStore.removeRow} captured, so `restoreRow` can undo it. */
export interface RemovedRow {
  readonly conversation: ConversationDto;
  readonly index: number;
  /** The result set the row was removed from; a restore into any other is refused. */
  readonly generation: number;
}

/**
 * Strips a `ConversationDetailDto`, which `ConversationStore` passes in, to list fields so no
 * row carries `mcpServerIds`.
 */
function listFields(conversation: ConversationDto): ConversationDto {
  const { id, projectId, modelId, name, isFavorite, dateCreated, dateModified } = conversation;

  return { id, projectId, modelId, name, isFavorite, dateCreated, dateModified };
}

/**
 * The signed-in user's conversations, newest first; the reference list store. `switchMap` so a
 * slow response cannot paint over a newer search, no `debounceTime` as `SearchInput` debounces,
 * `takeUntil(signedOut$)` so a late response cannot refill a reset store, and no `sort=` as the
 * sidebar is a fixed recency list. Row writes are methods because `ConversationActionsStore`
 * cannot patch protected state.
 */
export const ConversationListStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withEntities<ConversationDto>(),
  withRequestStatus(),
  withOffsetPagination(SIDEBAR_PAGE_SIZE),
  withPendingIds(),
  withProps(() => ({
    _http: inject(HttpClient),
    _toasts: inject(ToastStore),
    _url: inject(ApiUrl).build('conversations/search'),
    _signedOut$: injectSignedOut(),
    /**
     * Cancels an in-flight "Load more", which `switchMap` cannot reach, when the result set is
     * replaced; its stale page would land under the new set and skew `skip`.
     */
    _querySuperseded$: new Subject<void>(),
  })),
  withComputed(({ entities, name, isFulfilled }) => {
    const hasQuery = computed(() => name().trim() !== '');
    const isEmpty = computed(() => isFulfilled() && entities().length === 0);

    return {
      hasQuery,
      /** Loaded, and there is genuinely nothing — distinct from "not loaded yet". */
      isEmpty,
      /** Loaded, nothing matched, and a filter is why. A different empty state. */
      hasNoMatches: computed(() => isEmpty() && hasQuery()),
    };
  }),
  withMethods((store) => {
    function searchUrl(name: string, skip: number): { url: string; params: HttpParams } {
      const trimmed = name.trim();
      let params = new HttpParams().set('skip', String(skip)).set('take', String(store.take()));

      // Omitted rather than sent empty: a blank `name=` still takes the server's LIKE branch.
      if (trimmed !== '') {
        params = params.set('name', trimmed);
      }

      return { url: store._url, params };
    }

    // No `distinctUntilChanged`: `retry()` re-pushes the same name on purpose, and after a
    // sign-out reset the next query can be identical to the previous user's.
    const _runQuery = rxMethod<string | null>(
      pipe(
        filter((name): name is string => name !== null),
        tap(() => {
          store._querySuperseded$.next();
          patchState(store, setPending(), (state) => ({
            loadingMore: false,
            _queryGeneration: state._queryGeneration + 1,
          }));
        }),
        switchMap((name) => {
          const { url, params } = searchUrl(name, 0);

          return store._http.get<PaginatedResponseDto<ConversationDto>>(url, { params }).pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (page) =>
                patchState(
                  store,
                  // setFirstPage, not setPage: a narrowed search must discard the old
                  // offset, or the list comes back empty until the user pages back.
                  setAllEntities(page.items),
                  setFirstPage(page),
                  setFulfilled(),
                  { loadingMore: false },
                ),
              error: (cause: unknown) =>
                patchState(store, setError(toAppError(cause, { url })), { loadingMore: false }),
            }),
          );
        }),
      ),
    );

    const _loadNextPage = rxMethod<void>(
      // exhaustMap: a second press while a page is in flight is a double-click.
      exhaustMap(() => {
        const { url, params } = searchUrl(store.name(), store.skip());
        patchState(store, { loadingMore: true });

        return store._http.get<PaginatedResponseDto<ConversationDto>>(url, { params }).pipe(
          takeUntil(merge(store._signedOut$, store._querySuperseded$)),
          tapResponse({
            next: (page) =>
              patchState(
                store,
                // addEntities, not setEntities: a concurrent insert can shift the
                // window and repeat a row, and the copy already on screen wins.
                addEntities(page.items),
                setPage(page),
              ),
            error: (cause: unknown) =>
              // A toast rather than `setError`: the rows already loaded are still
              // valid, and swapping them for an error panel would lose them.
              void store._toasts.fromError(toAppError(cause, { url })),
            // Runs on cancellation too, which is the case the flag would otherwise
            // stick on: a superseded page never reaches `next` or `error`.
            finalize: () => patchState(store, { loadingMore: false }),
          }),
        );
      }),
    );

    return {
      _runQuery,

      /**
       * Releases the query. Idempotent; `SessionBootstrap` calls it after `POST api/users/me`
       * resolves, as no token exists before then.
       */
      ensureLoaded(): void {
        if (!store._started()) {
          patchState(store, { _started: true });
        }
      },

      /** Narrows the list. The wired query refetches itself; nothing else to call. */
      search(name: string): void {
        patchState(store, { name });
      },

      /** Re-issues the current query, which is what the error panel's Retry does. */
      retry(): void {
        // A static value rather than a state change: it needs no injection context,
        // and `switchMap` still cancels whatever the wired query had in flight.
        _runQuery(store.name());
      },

      loadMore(): void {
        // `isPending()` matters most: a page started while a query is on the wire carries the
        // old `skip`, and landing after `setFirstPage` it would leave an unreachable hole.
        if (!store.isPending() && !store.loadingMore() && store.hasMore()) {
          _loadNextPage();
        }
      },

      /**
       * Adopts a fresher copy of a held row, such as a server-generated title. Rows the list
       * does not hold are ignored: their position is unknown (see {@link prependNewest}).
       */
      refreshRow(conversation: ConversationDto): void {
        if (store.entityMap()[conversation.id] !== undefined) {
          patchState(
            store,
            updateEntity({ id: conversation.id, changes: listFields(conversation) }),
          );
        }
      },

      /**
       * Optimistic; roll back by calling again with the old name. `dateModified` is untouched
       * because the server's value arrives via `refreshRow`.
       */
      renameRow(id: string, name: string): void {
        if (store.entityMap()[id] !== undefined) {
          patchState(store, updateEntity({ id, changes: { name } }));
        }
      },

      /**
       * Optimistic; roll back by calling again with the old value. The server never bumps
       * `dateModified` for a favourite, so neither does this.
       */
      favoriteRow(id: string, isFavorite: boolean): void {
        if (store.entityMap()[id] !== undefined) {
          patchState(store, updateEntity({ id, changes: { isFavorite } }));
        }
      },

      /**
       * Optimistic; `null` unlinks, and a rollback calls again with the old value. The server's
       * bumped `dateModified` arrives via `refreshRow`, so there is no local guess.
       */
      moveRow(id: string, projectId: string | null): void {
        if (store.entityMap()[id] !== undefined) {
          patchState(store, updateEntity({ id, changes: { projectId } }));
        }
      },

      /**
       * Nulls `projectId` on rows of a deleted project, as the server does; otherwise the next
       * rename echoes the dead id through `toUpdateBody` and takes a 404.
       */
      releaseProject(projectId: string): void {
        for (const conversation of store.entities()) {
          if (conversation.projectId === projectId) {
            patchState(store, updateEntity({ id: conversation.id, changes: { projectId: null } }));
          }
        }
      },

      /**
       * Driven by `ConversationActionsStore`, always cleared in `finalize` so success, failure
       * and sign-out cancellation all release it.
       */
      setRowPending(id: string, pending: boolean): void {
        patchState(store, pending ? addPendingId(id) : removePendingId(id));
      },

      /**
       * Optimistic; returns what {@link restoreRow} needs, or null if the row is not held. Shifts
       * the paging window back (clamped, as a page may land in between) and cancels an
       * in-flight "Load more" built on the old offset, which would otherwise leave a hole.
       */
      removeRow(id: string): RemovedRow | null {
        const conversation = store.entityMap()[id];
        if (conversation === undefined) {
          return null;
        }

        store._querySuperseded$.next();
        const index = store.entities().findIndex((c) => c.id === id);
        patchState(store, removeEntity(id), (state) => ({
          skip: Math.max(0, state.skip - 1),
          totalCount: Math.max(0, state.totalCount - 1),
        }));

        return { conversation, index, generation: store._queryGeneration() };
      },

      /**
       * Rolls back a refused delete at the old index (clamped). Refused once the result set has
       * changed, as the row may not match the query; a no-op when the id is already back.
       */
      restoreRow({ conversation, index, generation }: RemovedRow): void {
        if (
          generation !== store._queryGeneration() ||
          store.entityMap()[conversation.id] !== undefined
        ) {
          return;
        }

        const next = [...store.entities()];
        next.splice(Math.min(index, next.length), 0, conversation);
        patchState(store, setAllEntities(next), (state) => ({
          skip: state.skip + 1,
          totalCount: state.totalCount + 1,
        }));
      },

      /**
       * Only for a conversation created this session: the server orders by `dateCreated`
       * descending, so it is at position 0. The counters advance so "Load more" still lines up.
       */
      prependNewest(conversation: ConversationDto): void {
        if (store.entityMap()[conversation.id] !== undefined) {
          return;
        }

        patchState(store, prependEntity(listFields(conversation)), (state) => ({
          skip: state.skip + 1,
          totalCount: state.totalCount + 1,
        }));
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // On the bus because `ProjectActionsStore` knows nothing about conversations, and this
    // list is the only holder of the stale ids.
    events.on(projectEvents.deleted).pipe(tap(({ payload: id }) => store.releaseProject(id))),
  ]),
  withHooks({
    onInit(store) {
      // A signal-valued rxMethod argument needs this injection context, which
      // `SessionBootstrap` lacks; hence `ensureLoaded` only sets a flag.
      store._runQuery(() => (store._started() ? store.name() : null));
    },
    onDestroy(store) {
      store._querySuperseded$.complete();
    },
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
