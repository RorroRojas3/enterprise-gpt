import { HttpClient, HttpParams } from '@angular/common/http';
import { computed, inject } from '@angular/core';
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
import { tapResponse } from '@ngrx/operators';
import { Subject, exhaustMap, filter, merge, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ConversationDto } from '@domain/api/conversation';
import { PaginatedResponseDto } from '@domain/api/paginated-response';
import { toAppError } from '@core/errors/to-app-error';
import { projectEvents } from '@core/events/project-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
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

/**
 * How many rows the sidebar asks for at a time.
 *
 * Below the server's 1–100 clamp and high enough that most users never reach the
 * "Load more" row; EP-7's full library pages properly.
 */
export const SIDEBAR_PAGE_SIZE = 50;

interface ConversationListState {
  /** The `name=` filter currently in force. Empty means unfiltered. */
  readonly name: string;
  /**
   * Whether `ensureLoaded` has released the query. See the `onInit` hook.
   *
   * Private: it is a release sentinel, not something a screen should render or reason
   * about, and `@ngrx/signals` strips `_`-prefixed members from the public store type.
   */
  readonly _started: boolean;
  /**
   * A page beyond the first is in flight. Separate from `requestStatus` so the loaded
   * rows stay on screen instead of collapsing back to a skeleton.
   */
  readonly loadingMore: boolean;
  /**
   * Which result set the entities belong to; every query bump replaces it. Private:
   * it exists so a {@link RemovedRow} can prove it still describes the list on
   * screen — `restoreRow` must not splice a row into a result set its removal never
   * touched, nor advance counters that never lost it.
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
 * Narrows a value to the fields the list holds.
 *
 * Callers legitimately hand this store a `ConversationDetailDto` — `ConversationStore`
 * does, after `GET api/conversations/{id}` — and spreading one straight into an entity
 * would leave `mcpServerIds` on some rows and not others, so `entities()` would stop
 * being uniformly `ConversationDto`-shaped.
 */
function listFields(conversation: ConversationDto): ConversationDto {
  const { id, projectId, modelId, name, isFavorite, dateCreated, dateModified } = conversation;

  return { id, projectId, modelId, name, isFavorite, dateCreated, dateModified };
}

/**
 * The signed-in user's conversations, from `GET api/conversations/search`.
 *
 * **This is the reference list store.** Fifteen more are written against its shape,
 * so four things in it are decisions rather than details:
 *
 * 1. **`rxMethod` + `switchMap`, not the promise-and-generation-counter shape
 *    `SessionStore` and `ModelCatalogStore` use.** Those two exist to be awaited by a
 *    guard. This one is driven by typing, where a slow response to `"ang"` must not
 *    paint over a fast response to `"angular"` — and cancellation is what `switchMap`
 *    is for.
 * 2. **Loading is declarative.** The query is wired once, in `onInit`, to a
 *    computation over `started` and `name`; changing either refetches with no second
 *    call site. `ensureLoaded` therefore only flips a flag — it is called from
 *    `SessionBootstrap`, where there is no injection context to bind a signal-valued
 *    reactive method to.
 * 3. **No `debounceTime` here.** `SearchInput` already debounces 300ms and emits only
 *    the committed value; a second debounce would add 300ms of latency for nothing.
 *    Add one here only if a caller ever drives `search` from raw keystrokes.
 * 4. **`takeUntil(signedOut$)` on the inner request.** `withResetOnSignOut` clears
 *    state and nothing else — a response already on the wire would otherwise write
 *    the previous user's rows back over an emptied store.
 *
 * Rows keep the server's order (`dateCreated` descending) and this store sends no
 * `sort=`, even though US-706 put one on the route: the sidebar is a fixed recency list
 * with no control to offer, and the reader who wants another order has `/conversations`,
 * where the select lives.
 *
 * `withPendingIds` arrived with US-304, the first per-row action. The mutation surface
 * around it — {@link renameRow}, {@link favoriteRow}, {@link setRowPending} — exists
 * because the store that drives renames, favourites and deletes
 * (`ConversationActionsStore`) cannot `patchState` a store whose state is protected;
 * entity writes stay methods here, decisions stay there.
 *
 * The `_started` + release-the-query trio (`ensureLoaded`, the `null` arm of
 * `_runQuery`, and the `onInit` computation) is the obvious candidate for a sixth
 * feature in `core/state/`. It stays inline here on the skill's own rule — two stores
 * sharing a shape is a coincidence, three is a feature — so the extraction trigger is
 * the *third* store that needs a bootstrap-released load, not this one.
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
     * Fires when the result set the list is paging through is replaced.
     *
     * `switchMap` cancels the *query* pipeline's own request, but a "Load more" is a
     * second pipeline and nothing in the first reaches it. Left running, its page —
     * rows from the previous result set — would be appended under the new one and its
     * `setPage` would advance `skip` past a window that no longer exists.
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

      // Omitted rather than sent empty: US-302's first criterion, and `name=` blank
      // would still take the server down its LIKE branch.
      if (trimmed !== '') {
        params = params.set('name', trimmed);
      }

      return { url: store._url, params };
    }

    // Deliberately no `distinctUntilChanged`. The signal computation below only emits
    // when `started` or `name` actually change, so it would be redundant for that path
    // — and it would break the other two: `retry()` pushes the same name a second time
    // on purpose, and after sign-out resets the store the very next query is the
    // identical one the previous user ran.
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
      // exhaustMap: a second press while a page is in flight is a double-click, not a
      // request for the page after next.
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
       * Releases the query. Idempotent, and called by `SessionBootstrap` after
       * `POST api/users/me` resolves — never alongside it, because the request would
       * otherwise go out before a token exists.
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
        // `isPending()` is the load-bearing half. `_querySuperseded$` cancels a page
        // that a *later* query overtook, but it cannot help a page started *after* a
        // query was already on the wire: that request carries the old `skip` against
        // the new result set, and when it lands after the query's `setFirstPage` it
        // appends a window from the middle of a list that no longer exists — leaving a
        // hole the user can never scroll to, with `hasMore()` reading false.
        if (!store.isPending() && !store.loadingMore() && store.hasMore()) {
          _loadNextPage();
        }
      },

      /**
       * Adopts a fresher copy of a row the list already holds — the title the server
       * generates out of band after a first turn (US-409), or an edit made from the
       * conversation header (US-304, US-305).
       *
       * A conversation the list does not hold is **ignored**, because it could be
       * anywhere in the server's order and inserting it at either end would be a
       * guess. A conversation that was just *created* is the one case where the
       * position is known — see {@link prependNewest}.
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
       * Optimistically renames a held row (US-304). The caller rolls a failure back
       * by calling it again with the previous name.
       *
       * A row the list does not hold is ignored for the same reason as
       * {@link refreshRow}. `dateModified` is deliberately untouched: the server's
       * value arrives via `refreshRow` when the request succeeds, and a local bump
       * would diverge from it on the next fetch.
       */
      renameRow(id: string, name: string): void {
        if (store.entityMap()[id] !== undefined) {
          patchState(store, updateEntity({ id, changes: { name } }));
        }
      },

      /**
       * Optimistically sets a held row's favourite flag (US-305). The caller rolls a
       * failure back by calling it again with the previous value.
       *
       * A row the list does not hold is ignored for the same reason as
       * {@link refreshRow}. Only `isFavorite` moves: the server does not bump
       * `dateModified` for a favourite, so unlike a rename there is never a server
       * value to adopt afterwards, and a local bump would be pure divergence.
       */
      favoriteRow(id: string, isFavorite: boolean): void {
        if (store.entityMap()[id] !== undefined) {
          patchState(store, updateEntity({ id, changes: { isFavorite } }));
        }
      },

      /**
       * Optimistically moves a held row into a project, or out of every project when
       * `projectId` is null (US-307). The caller rolls a failure back by calling it
       * again with the previous value.
       *
       * A row the list does not hold is ignored for the same reason as
       * {@link refreshRow}. `dateModified` is left alone here even though the server
       * *does* bump it for a move: the server's value arrives via `refreshRow` on the
       * 200, and a local guess would diverge from it in the meantime — the same
       * bargain {@link renameRow} makes.
       */
      moveRow(id: string, projectId: string | null): void {
        if (store.entityMap()[id] !== undefined) {
          patchState(store, updateEntity({ id, changes: { projectId } }));
        }
      },

      /**
       * Clears `projectId` on every held row that belonged to a deleted project
       * (US-903).
       *
       * Not cosmetic, and not optional. `DeactivateProjectAsync` soft-deletes the
       * project but **releases** its conversations, setting their `ProjectId` to null —
       * so a held row still carrying that id is carrying a dead one, and `toUpdateBody`
       * echoes `projectId` on every rename. Left alone, the next rename of such a
       * conversation would PUT a project the server has deleted and take a 404 for a
       * request that has nothing to do with projects.
       *
       * Nothing renders `projectId` yet, which is exactly why this is easy to forget
       * and worth stating: the defect it prevents is invisible until US-307 or a
       * rename, whichever comes first.
       */
      releaseProject(projectId: string): void {
        for (const conversation of store.entities()) {
          if (conversation.projectId === projectId) {
            patchState(store, updateEntity({ id: conversation.id, changes: { projectId: null } }));
          }
        }
      },

      /**
       * Marks or clears a row's own in-flight action (US-304 rename; US-306 delete).
       *
       * Driven by `ConversationActionsStore` around its requests — always through
       * `tapResponse`'s `finalize`, so the flag clears on success, failure and
       * sign-out cancellation alike.
       */
      setRowPending(id: string, pending: boolean): void {
        patchState(store, pending ? addPendingId(id) : removePendingId(id));
      },

      /**
       * Optimistically removes a held row (US-306), returning what {@link restoreRow}
       * needs to undo it — null when the list does not hold the row.
       *
       * The inverse of {@link prependNewest}'s bookkeeping: the server no longer
       * counts this row, so the window the next "Load more" asks for must shift back
       * with it. Both counters clamp at zero because a page response landing between
       * remove and restore can already have moved them.
       *
       * A "Load more" page already on the wire is superseded here too: its URL was
       * built against the pre-removal offset, and if the server deletes first, the
       * row that slid into the last fetched position would fall between the windows
       * — a permanent one-row hole. Cancelling lets the next press use the
       * corrected offset.
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
       * Reinserts a removed row at its previous position — the rollback of a delete
       * the server refused. `setAllEntities` over a spliced copy, because the entity
       * catalogue has no insert-at and hand-patching `ids` would bypass it.
       *
       * Refused outright when the removal belonged to a different result set (the
       * user searched while the delete was in flight): the row may not match the
       * query on screen, and the counter bump would starve a genuine row out of the
       * next "Load more" window. The error toast has already reported the failed
       * delete; the row reappears with the next refetch of a set it belongs to.
       * Also a no-op when the id is already back, and index-clamped because pages
       * may have appended meanwhile.
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
       * Puts a just-created conversation at the head of the list.
       *
       * Correct only for a conversation created in this session, and only because the
       * server orders by `dateCreated` **descending**: a row created a moment ago is at
       * server position 0 by construction. `skip` and `totalCount` both advance with
       * it, so the window the next "Load more" asks for still lines up — and if the
       * server returns the same row in that page, `addEntities` no-ops on the id.
       *
       * US-401 calls this when `POST api/conversations` returns.
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
    // A project delete reaches across two features, which is what puts it on the event
    // bus rather than on a method call: `ProjectActionsStore` is root-scoped and knows
    // nothing about conversations, and this list is the only holder of the stale ids.
    events.on(projectEvents.deleted).pipe(tap(({ payload: id }) => store.releaseProject(id))),
  ]),
  withHooks({
    onInit(store) {
      // An injection context, which a signal-valued rxMethod argument requires — and
      // the reason `ensureLoaded` sets a flag instead of calling this itself, from
      // `SessionBootstrap`, where there is no injection context to bind the
      // subscription to.
      store._runQuery(() => (store._started() ? store.name() : null));
    },
    onDestroy(store) {
      store._querySuperseded$.complete();
    },
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
