import { HttpClient, HttpParams } from '@angular/common/http';
import { computed, inject, linkedSignal } from '@angular/core';
import { tapResponse } from '@ngrx/operators';
import {
  PartialStateUpdater,
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withLinkedState,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import {
  EntityId,
  addEntities,
  removeEntities,
  removeEntity,
  setAllEntities,
  updateEntity,
  withEntities,
} from '@ngrx/signals/entities';
import { Events, withEventHandlers } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { Subject, exhaustMap, merge, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ConversationDto } from '@shared/models/api/conversation.model';
import { PaginatedResponseDto } from '@shared/models/api/paginated-response.model';
import {
  ConversationSortOption,
  DEFAULT_CONVERSATION_SORT,
} from '@shared/utils/navigation/conversations-route';
import { selectRows } from '@shared/utils/selection/row-selection';
import { toAppError } from '@core/errors/to-app-error';
import { conversationEvents } from '@core/events/conversation-events';
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
import { ConversationActionsStore } from '@state/conversation-actions-store';

/** Sized for a table with Load more; `SIDEBAR_PAGE_SIZE` is sized so a sidebar rarely pages. */
export const LIBRARY_PAGE_SIZE = 25;

/** The URL's query, as one object so a change to any field fires one request, not several. */
export interface ConversationLibraryQuery {
  readonly name: string;
  readonly favorites: boolean;
  readonly sort: ConversationSortOption;
}

/**
 * Takes the offset and the total down by `count`, for rows that have left the list. The offset
 * too, or the next page would start short and skip the rows that slid into the gap.
 */
function shiftCountersDown(count: number): PartialStateUpdater<OffsetPaginationState> {
  return (state) => ({
    skip: Math.max(state.skip - count, 0),
    totalCount: Math.max(state.totalCount - count, 0),
  });
}

/** The inverse, for rows put back after a failed delete. */
function shiftCountersUp(count: number): PartialStateUpdater<OffsetPaginationState> {
  return (state) => ({ skip: state.skip + count, totalCount: state.totalCount + count });
}

function clearSelection(): SelectionState {
  return { selectedIds: new Set<string>() };
}

/** Puts ids back in the selection, so a failed bulk delete can simply be retried. */
function selectRowsUpdater(ids: readonly string[]): PartialStateUpdater<SelectionState> {
  return ({ selectedIds }) => ({ selectedIds: selectRows(selectedIds, [...ids]) });
}

/** What {@link ConversationLibraryStore} keeps in order to undo an optimistic removal. */
interface RemovedRows {
  readonly rows: readonly { readonly conversation: ConversationDto; readonly index: number }[];
  /** The result set the rows came out of; a restore into any other is refused. */
  readonly generation: number;
}

interface SelectionState {
  readonly selectedIds: ReadonlySet<string>;
}

interface ConversationLibraryState {
  /**
   * The `name=` the rows on screen belong to. Written when the request starts, so the empty state
   * names the term that produced it rather than the one still being typed.
   */
  readonly name: string;
  /** The favourites filter the rows on screen belong to, on the same terms. */
  readonly favorites: boolean;
  /** The order the rows on screen are in, on the same terms. */
  readonly sort: ConversationSortOption;
  /** A next page is in flight; unlike `isPending()`, it never blanks the rows already shown. */
  readonly loadingMore: boolean;
  /** Bumped by every query, so a rollback never splices old rows into a newer result set. */
  readonly _queryGeneration: number;
}

/**
 * Separate from the root `ConversationListStore`: the two lists show different filters and page
 * sizes side by side, and this one's URL-driven state must not outlive the route.
 */
export const ConversationLibraryStore = signalStore(
  withState<ConversationLibraryState>({
    name: '',
    favorites: false,
    sort: DEFAULT_CONVERSATION_SORT,
    loadingMore: false,
    _queryGeneration: 0,
  }),
  withEntities<ConversationDto>(),
  withRequestStatus(),
  withOffsetPagination(LIBRARY_PAGE_SIZE),
  withProps(() => ({
    _http: inject(HttpClient),
    _toasts: inject(ToastStore),
    _url: inject(ApiUrl).build('conversations/search'),
    _signedOut$: injectSignedOut(),
    /** Cancels an in-flight page once the result set or offset it was built against changes. */
    _querySuperseded$: new Subject<void>(),
    /**
     * Owns the bulk delete, which outlives this route. A root store cannot inject this
     * route-scoped one, so the rollback travels back as a callback.
     */
    _actions: inject(ConversationActionsStore),
  })),
  withLinkedState(({ ids }) => ({
    /**
     * The rows checked for a bulk action, pruned to what is still loaded. Linked, so every way a
     * row can leave prunes it, while Load more keeps the selection as the id list grows.
     */
    selectedIds: linkedSignal<readonly EntityId[], ReadonlySet<string>>({
      source: ids,
      computation: (loaded, previous) => {
        if (previous === undefined || previous.value.size === 0) {
          return new Set<string>();
        }

        const kept = new Set<string>();
        for (const id of loaded) {
          const key = String(id);
          if (previous.value.has(key)) {
            kept.add(key);
          }
        }

        return kept;
      },
    }),
  })),
  withComputed(({ entities, name, favorites, totalCount, isFulfilled, isPending, selectedIds }) => {
    const hasTerm = computed(() => name().trim() !== '');
    const hasQuery = computed(() => hasTerm() || favorites());
    const isEmpty = computed(() => isFulfilled() && entities().length === 0);

    return {
      /** A `name=` is narrowing the list, whatever the favourites filter is doing. */
      hasTerm,
      hasQuery,
      isEmpty,
      /**
       * Empty because of a term. Not `hasQuery`: an empty favourites-only filter is a different
       * state with a different action.
       */
      hasNoMatches: computed(() => isEmpty() && hasTerm()),
      hasNoFavorites: computed(() => isEmpty() && !hasTerm() && favorites()),
      /** Only an empty table gets a skeleton; a narrowing search keeps its rows on screen. */
      isFirstLoad: computed(() => isPending() && entities().length === 0),
      /** Counts rows on screen, not `skip`, so it stays right whichever path removed a row. */
      countSummary: computed(() => {
        const total = totalCount();
        // `DataTable.summary` replaces the table's own count, so the singular is handled here.
        return `Showing ${entities().length} of ${total} ${total === 1 ? 'conversation' : 'conversations'}`;
      }),

      selectedCount: computed(() => selectedIds().size),
      hasSelection: computed(() => selectedIds().size > 0),
      allLoadedSelected: computed(
        () => entities().length > 0 && selectedIds().size === entities().length,
      ),
      someLoadedSelected: computed(
        () => selectedIds().size > 0 && selectedIds().size < entities().length,
      ),
      /** No endpoint deletes a whole search, so the bar says select-all stops at loaded rows. */
      selectAllNote: computed(() => `Select-all covers the ${entities().length} loaded rows only`),
    };
  }),
  withMethods((store) => {
    function searchUrl(
      query: ConversationLibraryQuery,
      skip: number,
    ): { url: string; params: HttpParams } {
      const trimmed = query.name.trim();
      // `withOffsetPagination` clamps `take` to the server's 1–100. The order rides every page,
      // so Load more continues the same sorted run rather than starting a second one beneath it.
      let params = new HttpParams()
        .set('skip', String(skip))
        .set('take', String(store.take()))
        .set('sort', query.sort.wire.sort)
        .set('dir', query.sort.wire.dir);

      // Omitted rather than sent blank, which would still take the server down its LIKE branch.
      if (trimmed !== '') {
        params = params.set('name', trimmed);
      }

      // Only ever `true`: the server reads `isFavorite=false` as "only unstarred", a third state
      // this screen has no control for.
      if (query.favorites) {
        params = params.set('isFavorite', 'true');
      }

      return { url: store._url, params };
    }

    /** The query the rows on screen belong to, for a retry or a next page. */
    function currentQuery(): ConversationLibraryQuery {
      return { name: store.name(), favorites: store.favorites(), sort: store.sort() };
    }

    /** Removes rows optimistically, keeping enough to put them back where they were. */
    function takeRows(ids: readonly string[]): RemovedRows {
      const loaded = store.entities();
      const rows = loaded
        .map((conversation, index) => ({ conversation, index }))
        .filter(({ conversation }) => ids.includes(conversation.id));

      // As in `_dropRow`: a page on the wire carries the pre-removal offset.
      store._querySuperseded$.next();
      patchState(
        store,
        removeEntities([...ids]),
        shiftCountersDown(rows.length),
        { loadingMore: false },
        clearSelection(),
      );

      return { rows, generation: store._queryGeneration() };
    }

    /** Puts an optimistic removal back, and re-selects it so Retry means something. */
    function restoreRows({ rows, generation }: RemovedRows): void {
      if (rows.length === 0 || generation !== store._queryGeneration()) {
        return;
      }

      const next = [...store.entities()];
      // Ascending, so each splice's index already accounts for the rows restored before it.
      for (const { conversation, index } of rows) {
        next.splice(Math.min(index, next.length), 0, conversation);
      }

      patchState(
        store,
        setAllEntities(next),
        shiftCountersUp(rows.length),
        selectRowsUpdater(rows.map(({ conversation }) => conversation.id)),
      );
    }

    // No `distinctUntilChanged`: the signal source only emits on a real change, and
    // `retry()` pushes the same query again on purpose.
    const _runQuery = rxMethod<ConversationLibraryQuery>(
      pipe(
        tap(({ name, favorites, sort }) => {
          store._querySuperseded$.next();
          patchState(
            store,
            (state) => ({
              name,
              favorites,
              sort,
              loadingMore: false,
              _queryGeneration: state._queryGeneration + 1,
            }),
            setPending(),
          );
        }),
        switchMap((query) => {
          const { url, params } = searchUrl(query, 0);

          return store._http.get<PaginatedResponseDto<ConversationDto>>(url, { params }).pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (page) =>
                patchState(store, setAllEntities(page.items), setFirstPage(page), setFulfilled()),
              error: (cause: unknown) => patchState(store, setError(toAppError(cause, { url }))),
            }),
          );
        }),
      ),
    );

    const _loadNextPage = rxMethod<void>(
      // exhaustMap: a second press while a page is in flight is a double-click, and `skip` has
      // not moved yet, so it would fetch the same window twice.
      exhaustMap(() => {
        const { url, params } = searchUrl(currentQuery(), store.skip());
        patchState(store, { loadingMore: true });

        return store._http.get<PaginatedResponseDto<ConversationDto>>(url, { params }).pipe(
          takeUntil(merge(store._signedOut$, store._querySuperseded$)),
          tapResponse({
            // `setPage` advances `skip` by what came back rather than by `take`, so a short page
            // cannot leave a gap.
            next: (page) => patchState(store, addEntities(page.items), setPage(page)),
            error: (cause: unknown) =>
              // A toast, not `setError`: the loaded rows are still valid and stay on screen.
              void store._toasts.fromError(toAppError(cause, { url })),
            finalize: () => patchState(store, { loadingMore: false }),
          }),
        );
      }),
    );

    return {
      /**
       * Removes a row that has left the result set. Cancels any page on the wire, whose offset
       * predates the removal and would skip the row that slid into the gap.
       */
      _dropRow(id: string): void {
        store._querySuperseded$.next();
        patchState(store, removeEntity(id), shiftCountersDown(1), { loadingMore: false });
      },

      /**
       * Binds the screen's URL query once, from its constructor. A computation rather than a
       * value, so a deep link fires one filtered request rather than an unfiltered one first.
       */
      bindQuery: _runQuery,

      /** Re-runs the query the rows on screen belong to, after a failure. */
      retry(): void {
        _runQuery(currentQuery());
      },

      /**
       * Refused while a query is pending: a page started then would carry the old `skip` into the
       * new result set, which `_querySuperseded$` cannot cancel.
       */
      loadMore(): void {
        if (!store.isPending() && !store.loadingMore() && store.hasMore()) {
          _loadNextPage();
        }
      },

      /** Adopts the table's selection. The table owns the checkboxes; this owns the set. */
      setSelection(selectedIds: ReadonlySet<string>): void {
        patchState(store, { selectedIds });
      },

      /** Covers the loaded rows only: no endpoint acts on a whole search. */
      toggleSelectAll(): void {
        patchState(store, {
          selectedIds: store.allLoadedSelected()
            ? new Set<string>()
            : new Set(store.entities().map((conversation) => conversation.id)),
        });
      },

      clearSelection(): void {
        patchState(store, clearSelection());
      },

      /** Removes the selection optimistically; a failed request puts it back, re-selected. */
      deleteSelected(): void {
        const ids = [...store.selectedIds()];
        if (ids.length === 0) {
          return;
        }

        const removed = takeRows(ids);
        store._actions.deleteMany(ids, () => restoreRows(removed));
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // Server-confirmed changes from anywhere, e.g. a sidebar rename, so the two lists agree.
    events.on(conversationEvents.updated).pipe(
      tap(({ payload }) => {
        if (store.entityMap()[payload.id] === undefined) {
          return;
        }

        // Unstarred under the favourites filter: the row no longer matches, so it is dropped
        // rather than refetching every loaded page.
        if (store.favorites() && !payload.isFavorite) {
          store._dropRow(payload.id);
          return;
        }

        patchState(store, updateEntity({ id: payload.id, changes: payload }));
      }),
    ),

    events.on(conversationEvents.deleted).pipe(
      tap(({ payload: id }) => {
        if (store.entityMap()[id] === undefined) {
          return;
        }

        store._dropRow(id);
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
