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
  removeEntities,
  setAllEntities,
  updateEntities,
  withEntities,
} from '@ngrx/signals/entities';
import { Events, withEventHandlers } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { Observable, Subject, exhaustMap, merge, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ApiUrl } from '@core/api/api-url';
import { GENERATED_DOCUMENT_TYPE, UserDocumentDto } from '@core/api/document.model';
import { PaginatedResponseDto } from '@core/api/paginated-response.model';
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
import { conversationEvents } from '@core/events/conversation-events';
import { injectSignedOut } from '@core/events/session-events';
import { ToastStore } from '@core/notifications/toast-store';
import { withResetOnSignOut } from '@core/session/with-reset-on-sign-out';

/** Matches the conversations library's page size, so paging feels the same on both screens. */
export const DOCUMENT_PAGE_SIZE = 25;

/** Everything the URL says about which documents to fetch. */
export interface DocumentLibraryQuery {
  readonly name: string;
  readonly generatedOnly: boolean;
}

interface DocumentLibraryState {
  /**
   * The `name=` the rows on screen belong to. Written when the request starts, so the empty state
   * names the term that produced it rather than the one still being typed.
   */
  readonly name: string;
  /**
   * Served rather than filtered here: a filter over the loaded window would miss generated files
   * past it, and `totalCount` would keep counting rows it removed.
   */
  readonly generatedOnly: boolean;
  /** A next page is in flight; unlike `isPending()`, it never blanks the rows already shown. */
  readonly loadingMore: boolean;
}

/** One conversation's slice of the grouped view. */
export interface DocumentGroup {
  readonly conversationId: string;
  readonly conversationName: string;
  /** `3 files`, or `3 of 8 files` where the conversation holds more than is loaded. */
  readonly countLabel: string;
  readonly documents: readonly UserDocumentDto[];
}

/**
 * The noun the counts use, which the served origin filter changes: a figure over generated
 * documents alone must not read as a figure over the library.
 */
function documentNoun(count: number, generatedOnly: boolean): string {
  const plural = count === 1 ? 'document' : 'documents';

  return generatedOnly ? `generated ${plural}` : plural;
}

/**
 * Moves the offset and the total together, for rows that left the set. The offset too, or the
 * next page would start short and skip the rows that slid into the gap.
 */
function shiftCounters(delta: number): PartialStateUpdater<OffsetPaginationState> {
  return ({ skip, totalCount }) => ({
    skip: Math.max(skip + delta, 0),
    totalCount: Math.max(totalCount + delta, 0),
  });
}

/**
 * Mirrors `ConversationLibraryStore`, for the same reasons. The name filter is served, since a
 * client-side one would search only the loaded page.
 */
export const DocumentLibraryStore = signalStore(
  withState<DocumentLibraryState>({ name: '', generatedOnly: false, loadingMore: false }),
  withEntities<UserDocumentDto>(),
  withRequestStatus(),
  withOffsetPagination(DOCUMENT_PAGE_SIZE),
  withProps(() => ({
    _http: inject(HttpClient),
    _toasts: inject(ToastStore),
    _url: inject(ApiUrl).build('documents'),
    _signedOut$: injectSignedOut(),
    /** Cancels an in-flight page once the result set or offset it was built against changes. */
    _querySuperseded$: new Subject<void>(),
  })),
  withComputed(({ entities, generatedOnly, isFulfilled, isPending, name, totalCount }) => {
    const hasQuery = computed(() => name().trim() !== '');
    const isEmpty = computed(() => isFulfilled() && entities().length === 0);

    return {
      hasQuery,
      isEmpty,
      hasNoMatches: computed(() => isEmpty() && hasQuery()),
      /** Only an empty list gets skeletons; a narrowing search keeps its rows on screen. */
      isFirstLoad: computed(() => isPending() && entities().length === 0),
      /** Counts rows on screen, not `skip`, so it stays right whichever path removed a row. */
      countSummary: computed(() => {
        const total = totalCount();
        const noun = documentNoun(total, generatedOnly());

        if (total === 0) {
          return `No ${noun}`;
        }

        return `Showing ${entities().length} of ${total} ${noun}`;
      }),
      /**
       * Groups the loaded rows in first-seen order, which, over the server's newest-first list,
       * orders groups by their newest document.
       */
      groups: computed<readonly DocumentGroup[]>(() => {
        const byConversation = new Map<string, { name: string; documents: UserDocumentDto[] }>();

        for (const document of entities()) {
          const group = byConversation.get(document.conversationId);
          if (group === undefined) {
            byConversation.set(document.conversationId, {
              name: document.conversationName,
              documents: [document],
            });
          } else {
            group.documents.push(document);
          }
        }

        return [...byConversation.entries()].map(([conversationId, group]) => {
          const loaded = group.documents.length;
          // The server's count of the conversation's matching documents. Compared, not checked
          // for presence: equal means the group is whole, and a missing field falls through.
          const matching = group.documents[0]?.conversationDocumentCount;

          return {
            conversationId,
            conversationName: group.name,
            countLabel:
              matching !== undefined && matching > loaded
                ? `${loaded} of ${matching} files`
                : `${loaded} ${loaded === 1 ? 'file' : 'files'}`,
            documents: group.documents,
          };
        });
      }),
    };
  }),
  withMethods((store) => {
    function pageParams(query: DocumentLibraryQuery, skip: number): HttpParams {
      const trimmed = query.name.trim();
      let params = new HttpParams().set('skip', String(skip)).set('take', String(store.take()));

      if (query.generatedOnly) {
        params = params.set('type', GENERATED_DOCUMENT_TYPE);
      }

      // Omitted rather than sent blank, which would still take the server down its LIKE branch.
      if (trimmed !== '') {
        params = params.set('name', trimmed);
      }

      return params;
    }

    function fetchPage(
      query: DocumentLibraryQuery,
      skip: number,
    ): Observable<PaginatedResponseDto<UserDocumentDto>> {
      return store._http.get<PaginatedResponseDto<UserDocumentDto>>(store._url, {
        params: pageParams(query, skip),
      });
    }

    /** The query the rows on screen belong to, for a retry or a next page. */
    function currentQuery(): DocumentLibraryQuery {
      return { name: store.name(), generatedOnly: store.generatedOnly() };
    }

    // No `distinctUntilChanged`: the signal source only emits on a real change, and
    // `retry()` pushes the same query again on purpose.
    const _runQuery = rxMethod<DocumentLibraryQuery>(
      pipe(
        tap(({ name, generatedOnly }) => {
          store._querySuperseded$.next();
          patchState(store, { name, generatedOnly, loadingMore: false }, setPending());
        }),
        switchMap((query) =>
          fetchPage(query, 0).pipe(
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
      // exhaustMap: a second press while a page is in flight is a double-click, and `skip` has
      // not moved yet, so it would fetch the same window twice.
      exhaustMap(() => {
        patchState(store, { loadingMore: true });

        return fetchPage(currentQuery(), store.skip()).pipe(
          takeUntil(merge(store._signedOut$, store._querySuperseded$)),
          tapResponse({
            // `setPage` advances `skip` by what came back rather than by `take`, so a short page
            // cannot leave a gap.
            next: (page) => patchState(store, addEntities(page.items), setPage(page)),
            error: (cause: unknown) =>
              // A toast, not `setError`: the loaded rows are still valid and stay on screen.
              void store._toasts.fromError(toAppError(cause, { url: store._url })),
            finalize: () => patchState(store, { loadingMore: false }),
          }),
        );
      }),
    );

    return {
      /**
       * Binds the screen's `?name=` and `?source=` once, from its constructor. A computation, not
       * a value, so a deep link fires one filtered request rather than an unfiltered one first.
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
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // Server-confirmed changes only.
    events.on(conversationEvents.updated).pipe(
      tap(({ payload }) => {
        // The filter is on document names, so a conversation rename never evicts a row here.
        patchState(
          store,
          updateEntities({
            predicate: (document) => document.conversationId === payload.id,
            changes: { conversationName: payload.name },
          }),
        );
      }),
    ),

    events.on(conversationEvents.deleted).pipe(
      tap(({ payload: conversationId }) => {
        const removed = store
          .entities()
          .filter((document) => document.conversationId === conversationId).length;

        if (removed === 0) {
          return;
        }

        // The server deletes a conversation's documents with it, so their rows go too, along
        // with any page in flight, whose offset predates the removal.
        store._querySuperseded$.next();
        patchState(
          store,
          removeEntities((document: UserDocumentDto) => document.conversationId === conversationId),
          shiftCounters(-removed),
          { loadingMore: false },
        );
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
