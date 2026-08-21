import { HttpClient, HttpParams } from '@angular/common/http';
import { computed, inject, linkedSignal } from '@angular/core';
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
import { tapResponse } from '@ngrx/operators';
import { Subject, exhaustMap, merge, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ConversationDto } from '@domain/api/conversation';
import { PaginatedResponseDto } from '@domain/api/paginated-response';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { conversationEvents } from '@core/events/conversation-events';
import { injectSignedOut } from '@core/events/session-events';
import { toAppError } from '@core/errors/to-app-error';
import { ApiUrl } from '@core/http/api-url';
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
import { selectRows } from '@shared/data/row-selection';
import { ConversationSortOption, DEFAULT_CONVERSATION_SORT } from './conversations-route';

/**
 * How many rows the library asks for at a time, matching frame `4a`'s own count.
 *
 * Below the server's 1–100 clamp. Separate from `SIDEBAR_PAGE_SIZE` deliberately —
 * that number is chosen so a sidebar rarely needs a second page, and this one is
 * chosen for a table with a Load more control beneath it (US-702).
 */
export const LIBRARY_PAGE_SIZE = 25;

/**
 * Everything the URL says about which conversations to fetch (US-701, US-703).
 *
 * One object rather than two `rxMethod`s: the two parameters go out on one request,
 * so binding them separately would fire a second query for every change to either.
 */
export interface ConversationLibraryQuery {
  readonly name: string;
  readonly favorites: boolean;
  readonly sort: ConversationSortOption;
}

/**
 * Takes the offset and the total down by `count`, for rows that have left the list.
 *
 * Both counters move together. The total because US-702's "Showing N of M" would
 * otherwise claim rows that are gone, and the **offset** because the server no longer
 * counts them either — leaving `skip` where it was would make the next page start
 * short, and the rows that slid into the gap would never be fetched.
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

/** Empties the selection. A new `Set`, because `patchState` compares by reference. */
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
   * The `name=` filter the rows on screen belong to.
   *
   * Written when the request starts, not when it commits, so the empty state can
   * name the term that produced it rather than the one still being typed.
   */
  readonly name: string;
  /** The favourites filter the rows on screen belong to, on the same terms. */
  readonly favorites: boolean;
  /** The order the rows on screen are in, on the same terms. */
  readonly sort: ConversationSortOption;
  /**
   * A *next page* is in flight (US-702), which is not the same as `isPending()`.
   *
   * A query replaces the rows and shows a skeleton; a page appends to them and only
   * marks the Load more control busy. Keeping them apart is what stops a second page
   * from blanking a table the reader is already looking at.
   */
  readonly loadingMore: boolean;
  /**
   * Bumped by every query, so an optimistic removal knows which result set it came
   * out of. Restoring rows into a set the reader has since replaced would splice the
   * old query's rows into the new one's.
   */
  readonly _queryGeneration: number;
}

/**
 * The conversations library's own list, from `GET api/conversations/search` (US-701).
 *
 * **A second store rather than the root `ConversationListStore`, and that is the
 * story's one architectural decision.** They query the same endpoint, but they are
 * not the same list: frame `4a` draws both search fields at once, so sharing state
 * would make typing here retype the sidebar's field and narrow its rows;
 * `withOffsetPagination` holds exactly one offset, which cannot serve a 50-row
 * sidebar and a 25-row table at once; US-703's favourites filter would narrow the
 * sidebar with no control there to undo it; and the URL is authoritative for *this
 * route*, while a root store outlives it — a deep link would leave the sidebar
 * filtered after navigating away.
 *
 * It copies the reference store's shape otherwise, including the reasons: `rxMethod`
 * + `switchMap` because typing drives it and a slow `"ang"` must not paint over a
 * fast `"angular"`; no `debounceTime`, because `SearchInput` already debounces and
 * emits only committed values; and `takeUntil(signedOut$)` on the inner request,
 * because `withResetOnSignOut` clears state and does not cancel work.
 *
 * What it does *not* copy is the `_started` release sentinel. The sidebar's list is
 * released by `SessionBootstrap` because it exists before any screen asks for it;
 * this one is created by the route that shows it and always has a term to run.
 *
 * **The order is the server's, and the screen chooses it (US-706).** `sort=` and `dir=`
 * ride the first page and every Load more alike, which is what makes an appended page a
 * continuation of the same run rather than a second sorted run underneath the first — the
 * failure that made a control dishonest here while the endpoint accepted no order, and
 * US-705's whole subject until it did.
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
    /**
     * Cancels a page whose result set no longer exists.
     *
     * `switchMap` already cancels one *query* for the next, but a page is a separate
     * request on a separate `rxMethod`: without this, a page fetched against the old
     * term lands after the new query's `setAllEntities` and appends a window from a
     * list that is gone.
     */
    _querySuperseded$: new Subject<void>(),
    /**
     * Owns the bulk delete request, because it outlives this route and this store.
     * `features → core` is the permitted direction; the reverse is why the rollback
     * travels back as a callback rather than as an injected dependency.
     */
    _actions: inject(ConversationActionsStore),
  })),
  withLinkedState(({ ids }) => ({
    /**
     * The rows checked for a bulk action (US-704), pruned to what is still loaded.
     *
     * Linked rather than plain state, because every way a row can leave — a new
     * query, a delete from anywhere, an unstar under the favourites filter — would
     * otherwise need its own line to prune the set, and the one that got forgotten
     * would leave the bulk bar counting rows that are not there. Reconciling against
     * the previous value is what keeps a selection alive across Load more, where the
     * id list grows rather than being replaced.
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
      /** Loaded, and there is genuinely nothing — distinct from "not loaded yet". */
      isEmpty,
      /**
       * Loaded, nothing matched, and a *term* is why — frame `4b`, which names it.
       *
       * Deliberately not `hasQuery`: a favourites-only filter with nothing behind it
       * is a different situation with a different action, and `4b`'s heading has no
       * term to quote.
       */
      hasNoMatches: computed(() => isEmpty() && hasTerm()),
      /** Loaded, nothing matched, and only the favourites filter is on. */
      hasNoFavorites: computed(() => isEmpty() && !hasTerm() && favorites()),
      /**
       * Nothing on screen yet, so a skeleton is the honest thing to show. A search
       * that *narrows* an existing list keeps its rows and marks the field busy
       * instead, which is the distinction `Sidebar` draws for the same reason.
       */
      isFirstLoad: computed(() => isPending() && entities().length === 0),
      /**
       * Frame `4a`'s toolbar counter (US-702).
       *
       * The loaded half counts the rows actually on screen rather than reading `skip`:
       * the two agree after a page, but a row deleted from anywhere decrements both
       * counters, and counting rows keeps this honest whichever path moved them.
       */
      countSummary: computed(() => {
        const total = totalCount();
        // `DataTable.summary` replaces its own count rather than adding to it, so the
        // singular it would otherwise have handled has to be handled here.
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
      /**
       * US-704's required copy, with the count substituted.
       *
       * Select-all reaches the loaded rows and no further — there is no endpoint that
       * deletes a whole search — so the bar says so rather than letting the reader
       * assume the filter's full result set is going.
       */
      selectAllNote: computed(() => `Select-all covers the ${entities().length} loaded rows only`),
    };
  }),
  withMethods((store) => {
    function searchUrl(
      query: ConversationLibraryQuery,
      skip: number,
    ): { url: string; params: HttpParams } {
      const trimmed = query.name.trim();
      // `take` comes from `withOffsetPagination`, which clamps it to 1–100 — the
      // server's own range, so US-702's fourth criterion cannot be violated by
      // changing `LIBRARY_PAGE_SIZE`.
      // The order rides every request, the first page and each Load more alike. That is
      // what makes an appended page a continuation of the same run rather than a second
      // sorted run underneath the first — the failure US-705 refused a control over.
      let params = new HttpParams()
        .set('skip', String(skip))
        .set('take', String(store.take()))
        .set('sort', query.sort.wire.sort)
        .set('dir', query.sort.wire.dir);

      // Omitted rather than sent empty, and never `filter=`: US-701's first
      // criterion, and a blank `name=` would still take the server down its LIKE
      // branch.
      if (trimmed !== '') {
        params = params.set('name', trimmed);
      }

      // Only ever `true`. The parameter is a nullable bool server-side, so
      // `isFavorite=false` would mean "only the ones I have *not* starred" — a third
      // state this screen has no control for and no way back out of.
      if (query.favorites) {
        params = params.set('isFavorite', 'true');
      }

      return { url: store._url, params };
    }

    /** The query the rows on screen belong to, for a retry or a next page. */
    function currentQuery(): ConversationLibraryQuery {
      return { name: store.name(), favorites: store.favorites(), sort: store.sort() };
    }

    /**
     * Removes rows optimistically, keeping enough to put them back where they were.
     *
     * The generation is the guard on that: a restore into a result set the reader has
     * since replaced would splice rows from the old query into the new one.
     */
    function takeRows(ids: readonly string[]): RemovedRows {
      const loaded = store.entities();
      const rows = loaded
        .map((conversation, index) => ({ conversation, index }))
        .filter(({ conversation }) => ids.includes(conversation.id));

      // The same cancellation `_dropRow` performs, for the same reason: a page on the
      // wire carries the pre-removal offset.
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
      // Ascending, so each splice lands against indices the earlier ones have already
      // restored — descending would place every row after the first one short.
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
      // exhaustMap: a second press while a page is on the wire is a double-click, not
      // a request for the page after it. `skip` has not moved yet, so letting it
      // through would fetch the same window twice.
      exhaustMap(() => {
        const { url, params } = searchUrl(currentQuery(), store.skip());
        patchState(store, { loadingMore: true });

        return store._http.get<PaginatedResponseDto<ConversationDto>>(url, { params }).pipe(
          takeUntil(merge(store._signedOut$, store._querySuperseded$)),
          tapResponse({
            // addEntities, not setAllEntities: this page joins the rows already on
            // screen. `setPage` advances `skip` by what came back rather than by
            // `take`, so a short page cannot leave a gap.
            next: (page) => patchState(store, addEntities(page.items), setPage(page)),
            error: (cause: unknown) =>
              // A toast rather than `setError`: the rows already loaded are still
              // valid, and swapping the table for an error panel would throw away a
              // result set the reader can still use.
              void store._toasts.fromError(toAppError(cause, { url })),
            finalize: () => patchState(store, { loadingMore: false }),
          }),
        );
      }),
    );

    return {
      /**
       * Removes a row that has left the result set, and keeps paging coherent.
       *
       * The cancellation is the subtle half. A page already on the wire had its URL
       * built against the pre-removal offset, so once `skip` moves down beneath it
       * that response appends the *next* window and the row that slid into the gap is
       * fetched by neither request — a permanent hole, with the counter reading as if
       * nothing were missing. Dropping the page is the cheaper loss: the reader can
       * press Load more again.
       */
      _dropRow(id: string): void {
        store._querySuperseded$.next();
        patchState(store, removeEntity(id), shiftCountersDown(1), { loadingMore: false });
      },

      /**
       * Binds the screen's `?name=` and `?favorites=` to the query, once, from its
       * constructor.
       *
       * Taking a computation rather than a value is what makes a deep link one
       * request: `rxMethod` subscribes through an effect, so it reads the parameters
       * the router has already bound instead of firing unfiltered first and filtered
       * second. It re-runs when either input changes, and both go out together.
       */
      bindQuery: _runQuery,

      /** Re-runs the query the rows on screen belong to, after a failure. */
      retry(): void {
        _runQuery(currentQuery());
      },

      /**
       * Appends the next page in server order (US-702).
       *
       * `isPending()` is the load-bearing half of the guard. `_querySuperseded$`
       * cancels a page a *later* query overtook, but it cannot help a page started
       * *after* a query was already on the wire: that request carries the old `skip`
       * against the new result set, and when it lands after `setFirstPage` it appends
       * a window from the middle of a list that no longer exists — leaving a hole the
       * reader can never scroll to, with `hasMore()` reading false.
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

      /**
       * Frame `4a`'s toolbar select-all, which covers the **loaded** rows and no
       * further — there is no endpoint that acts on a whole search.
       */
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

      /**
       * Deletes every selected row in one request (US-704).
       *
       * The rows go at once and the request settles behind them: a 204 raises one
       * success toast and announces each deletion, a failure puts every row back where
       * it was, re-selects them, and raises one error toast.
       *
       * The request itself belongs to `ConversationActionsStore`, not here. This store
       * dies with its route, and `rxMethod` binds its subscription to that injector —
       * so a reader who confirmed a delete and then clicked a sidebar row would have
       * had the request torn down mid-flight, with nothing deleted and nothing said.
       * What stays here is the part only this screen can do: its own optimistic
       * removal, and the rollback handed over to undo it.
       */
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
    // Server-confirmed facts only, which is the whole contract of this event group.
    // A rename or a favourite performed from the sidebar has to land here too, or
    // the two lists disagree about a row they both hold.
    events.on(conversationEvents.updated).pipe(
      tap(({ payload }) => {
        if (store.entityMap()[payload.id] === undefined) {
          return;
        }

        // Unstarred while the favourites filter is on: the row no longer matches the
        // query that produced it, so it leaves (US-703). Patching it in place would
        // leave a hollow star sitting inside a favourites-only list, with the counter
        // already disagreeing — and refetching instead would throw away every page
        // the reader had loaded.
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
