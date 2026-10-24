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
import { Subject, exhaustMap, filter, merge, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ConversationDto } from '@shared/models/api/conversation.model';
import { PaginatedResponseDto } from '@shared/models/api/paginated-response.model';
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

/** The panel is a plain list, so it pages at the library's size. */
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
 * Never root-provided: one per detail route, so the tab strip can read the count, and one per
 * expanded sidebar project.
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
     * Cancels a page whose project no longer matches; it runs on its own `rxMethod`, out of
     * `switchMap`'s reach, and would append another project's rows.
     */
    _querySuperseded$: new Subject<void>(),
  })),
  withComputed(({ entities, isFulfilled, isPending, totalCount }) => ({
    /** Nothing on screen yet, so skeletons are the honest thing to show. */
    isFirstLoad: computed(() => isPending() && entities().length === 0),
    /** Null until the list is read: "Conversations 0" on a project with four is worse than none. */
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
              // A toast rather than `setError`: the rows already loaded are still usable.
              error: (cause: unknown) =>
                void store._toasts.fromError(toAppError(cause, { url: store._url })),
              finalize: () => patchState(store, { loadingMore: false }),
            }),
          );
      }),
    );

    return {
      /**
       * Also cancels an in-flight page: it carries the pre-removal offset, so once `skip` moves
       * the row that slid into the gap would be fetched by neither request.
       */
      _dropRow(id: string): void {
        store._querySuperseded$.next();
        patchState(store, removeEntity(id), shiftCountersDown(1), { loadingMore: false });
      },

      /** A computation, so a project change re-runs it and `switchMap` cancels the old load. */
      bindProject: _load,

      retry(): void {
        _load(store.projectId());
      },

      /**
       * `isPending()` guards what `_querySuperseded$` can't: a page started while a new query is
       * already in flight would carry the old `skip`.
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
          // Moved into this project, e.g. from the sidebar kebab. Re-read rather than insert:
          // the row's slot in `dateCreated desc` is the server's; `setPending` keeps rows shown.
          if (payload.projectId === store.projectId()) {
            store.retry();
          }

          return;
        }

        // Moved out, possibly from this panel's own kebab: the row no longer belongs here.
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
