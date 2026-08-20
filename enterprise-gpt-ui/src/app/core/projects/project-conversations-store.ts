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
import { Subject, exhaustMap, filter, merge, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ConversationDto } from '@domain/api/conversation';
import { PaginatedResponseDto } from '@domain/api/paginated-response';
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

/** Frame `4g`'s panel is a plain list, so it pages at the library's size. */
export const PROJECT_CONVERSATIONS_PAGE_SIZE = 25;

interface ProjectConversationsState {
  /** The project the rows on screen belong to. Null before the route resolves. */
  readonly projectId: string | null;
  /** A page beyond the first is in flight; the loaded rows stay on screen. */
  readonly loadingMore: boolean;
}

function shiftCountersDown(count: number): PartialStateUpdater<OffsetPaginationState> {
  return ({ skip, totalCount }) => ({
    skip: Math.max(skip - count, 0),
    totalCount: Math.max(totalCount - count, 0),
  });
}

/**
 * A project's conversations, from `GET api/conversations/search?projectId=` (US-908).
 *
 * **It issues one filtered request.** US-908's first criterion describes an interim
 * client-side drain to a 500-item ceiling, with a visible notice; that criterion applied
 * only while US-907 was outstanding. The enabler landed with this story, so the panel
 * ships the second criterion instead — one request, no drain, and no ceiling notice.
 *
 * It copies `ConversationLibraryStore` minus the two things frame `4g` does not draw: a
 * search field and row selection. What it keeps is the paging shape, the
 * `_querySuperseded$` guard on a page that a project change overtook, `takeUntil` on the
 * request because `withResetOnSignOut` clears state and does not cancel work, and that
 * feature composed last.
 *
 * **Never root-provided.** The detail route provides one instance for the whole screen —
 * rather than the panel providing it — so the tab strip's count is readable from the
 * Instructions tab, the same reason `ProjectDocumentsStore` is. US-910's sidebar provides
 * one *per expanded project node*, so a node's rows arrive when it opens and go when it
 * closes; several instances coexisting is the intended shape, not an accident of it.
 *
 * It lives in `core/` for that second consumer: `features/shell` may not import
 * `features/projects`, and this store injects nothing above `core/`.
 */
export const ProjectConversationsStore = signalStore(
  withState<ProjectConversationsState>({ projectId: null, loadingMore: false }),
  withEntities<ConversationDto>(),
  withRequestStatus(),
  withOffsetPagination(PROJECT_CONVERSATIONS_PAGE_SIZE),
  withProps(() => ({
    _http: inject(HttpClient),
    _toasts: inject(ToastStore),
    _url: inject(ApiUrl).build('conversations/search'),
    _signedOut$: injectSignedOut(),
    /**
     * Cancels a page whose project no longer matches. `switchMap` cancels the one
     * request in flight when the route changes, but a page is a separate request on a
     * separate `rxMethod`: without this it would land after the new project's
     * `setAllEntities` and append another project's rows.
     */
    _querySuperseded$: new Subject<void>(),
  })),
  withComputed(({ entities, isFulfilled, isPending, totalCount }) => ({
    /** Nothing on screen yet, so skeletons are the honest thing to show. */
    isFirstLoad: computed(() => isPending() && entities().length === 0),
    /**
     * The tab strip's count, or null until the list has actually been read.
     *
     * "Conversations 0" on a project that has four is a worse answer than no count, and
     * the strip renders before the panel it counts — exactly the call the files count
     * makes.
     */
    count: computed(() => (isFulfilled() ? totalCount() : null)),
    countSummary: computed(() => {
      const total = totalCount();

      return `Showing ${entities().length} of ${total} ${total === 1 ? 'conversation' : 'conversations'}`;
    }),
  })),
  withMethods((store) => {
    function pageParams(projectId: string, skip: number): HttpParams {
      return new HttpParams()
        .set('skip', String(skip))
        .set('take', String(store.take()))
        .set('projectId', projectId);
    }

    const _load = rxMethod<string | null>(
      pipe(
        filter((projectId): projectId is string => projectId !== null),
        tap((projectId) => {
          store._querySuperseded$.next();
          patchState(store, { projectId, loadingMore: false }, setPending());
        }),
        switchMap((projectId) =>
          store._http
            .get<PaginatedResponseDto<ConversationDto>>(store._url, {
              params: pageParams(projectId, 0),
            })
            .pipe(
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
      // request for the page after it — `skip` has not moved yet.
      exhaustMap(() => {
        const projectId = store.projectId();
        if (projectId === null) {
          return [];
        }

        patchState(store, { loadingMore: true });

        return store._http
          .get<PaginatedResponseDto<ConversationDto>>(store._url, {
            params: pageParams(projectId, store.skip()),
          })
          .pipe(
            takeUntil(merge(store._signedOut$, store._querySuperseded$)),
            tapResponse({
              next: (page) => patchState(store, addEntities(page.items), setPage(page)),
              // A toast rather than `setError`: the rows already loaded are still valid,
              // and swapping the list for an error panel would throw away a result set
              // the reader can still use.
              error: (cause: unknown) =>
                void store._toasts.fromError(toAppError(cause, { url: store._url })),
              finalize: () => patchState(store, { loadingMore: false }),
            }),
          );
      }),
    );

    return {
      /**
       * Removes a row that has left this project, and keeps paging coherent.
       *
       * The cancellation is the subtle half, and it is the library's reasoning verbatim:
       * a page already on the wire carries the pre-removal offset, so once `skip` moves
       * down beneath it that response appends the *next* window and the row that slid
       * into the gap is fetched by neither request.
       */
      _dropRow(id: string): void {
        store._querySuperseded$.next();
        patchState(store, removeEntity(id), shiftCountersDown(1), { loadingMore: false });
      },

      /**
       * Binds the route's `:projectId`, once, from the detail screen's constructor.
       *
       * A computation rather than a value, so navigating between two projects re-runs it
       * and `switchMap` cancels what the reader left behind.
       */
      bindProject: _load,

      /** Re-runs the query the rows on screen belong to, after a failure. */
      retry(): void {
        _load(store.projectId());
      },

      /**
       * Appends the next page in server order.
       *
       * `isPending()` is the load-bearing half of the guard: `_querySuperseded$` cancels
       * a page a later query overtook, but not one started *after* a query was already
       * on the wire, which would carry the old `skip` against the new result set.
       */
      loadMore(): void {
        if (!store.isPending() && !store.loadingMore() && store.hasMore()) {
          _loadNextPage();
        }
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    events.on(conversationEvents.updated).pipe(
      tap(({ payload }) => {
        if (store.entityMap()[payload.id] === undefined) {
          // Moved *into* this project from somewhere else — the sidebar row kebab opens
          // the same picker while this screen is up, so it is reachable with both
          // surfaces visible. This store's whole predicate is `projectId`, and the
          // payload carries it, so unlike the library — where a rename cannot be known
          // to match a `name=` filter without asking — the membership question is
          // answerable here.
          //
          // A re-read rather than an insert: the row's slot in `dateCreated desc` is the
          // server's to decide, and guessing one would put it where a refetch does not.
          // `setPending` leaves the loaded rows on screen, so the panel does not blank.
          if (payload.projectId === store.projectId()) {
            store.retry();
          }

          return;
        }

        // Moved out of this project — which includes a move made from this very panel's
        // own kebab. The row leaves rather than sitting in a list it no longer belongs
        // to, the same rule US-703 applies to an unstarred conversation under the
        // favourites filter.
        if (payload.projectId !== store.projectId()) {
          store._dropRow(payload.id);
          return;
        }

        patchState(store, updateEntity({ id: payload.id, changes: payload }));
      }),
    ),

    events.on(conversationEvents.deleted).pipe(
      tap(({ payload: id }) => {
        if (store.entityMap()[id] !== undefined) {
          store._dropRow(id);
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
