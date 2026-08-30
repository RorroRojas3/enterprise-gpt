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
  removeEntities,
  setAllEntities,
  updateEntities,
  withEntities,
} from '@ngrx/signals/entities';
import { Events, withEventHandlers } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { Observable, Subject, exhaustMap, merge, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { GENERATED_DOCUMENT_TYPE, UserDocumentDto } from '@domain/api/document';
import { PaginatedResponseDto } from '@domain/api/paginated-response';
import { toAppError } from '@core/errors/to-app-error';
import { conversationEvents } from '@core/events/conversation-events';
import { injectSignedOut } from '@core/events/session-events';
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

/**
 * How many rows the library asks for at a time, matching the conversations library's own
 * page size so paging feels the same on both screens.
 */
export const DOCUMENT_PAGE_SIZE = 25;

/** Everything the URL says about which documents to fetch. */
export interface DocumentLibraryQuery {
  readonly name: string;
  readonly generatedOnly: boolean;
}

interface DocumentLibraryState {
  /**
   * The `name=` filter the rows on screen belong to.
   *
   * Written when the request starts, not when it commits, so the empty state can name the
   * term that produced it rather than the one still being typed.
   */
  readonly name: string;
  /**
   * Narrow the listing to what the assistant produced.
   *
   * Served rather than filtered here, for the same reason the name term now is: a filter
   * over the loaded window would report "no generated files" to any reader whose generated
   * files sit past it, and `totalCount` would keep counting rows the filter removed.
   */
  readonly generatedOnly: boolean;
  /**
   * A *next page* is in flight, which is not the same as `isPending()`.
   *
   * A query replaces the rows and shows skeletons; a page appends to them and only marks
   * the Load more control busy. Keeping them apart is what stops a second page from
   * blanking a list the reader is already looking at.
   */
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
 * Moves the offset and the total together, for rows that left the set.
 *
 * Both, for the reason the conversations library documents: the total because "Showing N
 * of M" would otherwise count rows that are gone, and the **offset** because the server no
 * longer counts them either — leaving `skip` where it was would make the next page start
 * short, and the rows that slid into the gap would never be fetched.
 */
function shiftCounters(delta: number): PartialStateUpdater<OffsetPaginationState> {
  return ({ skip, totalCount }) => ({
    skip: Math.max(skip + delta, 0),
    totalCount: Math.max(totalCount + delta, 0),
  });
}

/**
 * Every document the signed-in user holds in a conversation, from `GET api/documents` —
 * uploaded or assistant-produced, flat or grouped by conversation.
 *
 * It copies `ConversationLibraryStore`, including the reasons: `rxMethod` + `switchMap`
 * because typing drives it and a slow `"quart"` must not paint over a fast `"quarterly"`;
 * no `debounceTime`, because `SearchInput` already debounces and emits only committed
 * values; `exhaustMap` on the next page so a double press is one request; and
 * `takeUntil(signedOut$)` on both, because `withResetOnSignOut` clears state and does not
 * cancel work.
 *
 * **The name filter is the server's.** It used to be `withClientQuery` over a set drained
 * to a 500-row ceiling, which meant a document ranked past that ceiling was unreachable by
 * any control this screen offered. Under paging a client-side filter would have been
 * strictly worse — it would search only the loaded page — so the endpoint took the
 * parameter and the feature came out.
 *
 * The store holds **zero seeded records by construction**: nothing is fetched until the
 * screen binds its query, and no placeholder row ever ships in state, so a user with
 * nothing in the library lands on the empty state. The unavailable panel this screen was
 * once specified to show first was never built — the endpoint landed ahead of it, so no
 * client ever shipped without one (product-owner decision 2026-08-19).
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
    /**
     * Cancels a page whose result set no longer exists.
     *
     * `switchMap` already cancels one *query* for the next, but a page is a separate
     * request on a separate `rxMethod`: without this, a page fetched against the old term
     * lands after the new query's `setAllEntities` and appends a window from a list that
     * is gone.
     */
    _querySuperseded$: new Subject<void>(),
  })),
  withComputed(({ entities, generatedOnly, isFulfilled, isPending, name, totalCount }) => {
    const hasQuery = computed(() => name().trim() !== '');
    const isEmpty = computed(() => isFulfilled() && entities().length === 0);

    return {
      hasQuery,
      /** Loaded, and there is genuinely nothing — distinct from "not loaded yet". */
      isEmpty,
      /** Loaded, nothing matched, and a term is why. */
      hasNoMatches: computed(() => isEmpty() && hasQuery()),
      /**
       * Nothing on screen yet, so skeleton rows are the honest thing to show. A search
       * that narrows an existing list keeps its rows and marks the field busy instead.
       */
      isFirstLoad: computed(() => isPending() && entities().length === 0),
      /**
       * The loaded half counts the rows actually on screen rather than reading `skip`: the
       * two agree after a page, but a row removed by a conversation delete decrements both
       * counters, and counting rows keeps this honest whichever path moved them.
       */
      countSummary: computed(() => {
        const total = totalCount();
        const noun = documentNoun(total, generatedOnly());

        if (total === 0) {
          return `No ${noun}`;
        }

        return `Showing ${entities().length} of ${total} ${noun}`;
      }),
      /**
       * The grouped view's shape, over the rows actually loaded, in first-seen order: the
       * server lists documents newest first, so groups order by their newest loaded
       * document, exactly where a refetch would put them.
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
          // The server's count of the conversation's documents matching the same filters.
          // A comparison, not a presence check: equal means the group is whole and "8 of 8"
          // would be noise, and a response without the field compares false and falls
          // through to the plain form rather than claiming an "of N" nobody sent. The
          // qualified branch is always plural, because it renders only where the total
          // exceeds a loaded count of at least one.
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

      // Omitted rather than sent empty: a blank `name=` would still take the server down
      // its LIKE branch.
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
      // exhaustMap: a second press while a page is on the wire is a double-click, not a
      // request for the page after it. `skip` has not moved yet, so letting it through
      // would fetch the same window twice.
      exhaustMap(() => {
        patchState(store, { loadingMore: true });

        return fetchPage(currentQuery(), store.skip()).pipe(
          takeUntil(merge(store._signedOut$, store._querySuperseded$)),
          tapResponse({
            // addEntities, not setAllEntities: this page joins the rows already on screen.
            // `setPage` advances `skip` by what came back rather than by `take`, so a short
            // page cannot leave a gap.
            next: (page) => patchState(store, addEntities(page.items), setPage(page)),
            error: (cause: unknown) =>
              // A toast rather than `setError`: the rows already loaded are still valid, and
              // swapping the list for an error panel would throw away a result set the
              // reader can still use.
              void store._toasts.fromError(toAppError(cause, { url: store._url })),
            finalize: () => patchState(store, { loadingMore: false }),
          }),
        );
      }),
    );

    return {
      /**
       * Binds the screen's `?name=` and `?source=` to the query, once, from its constructor.
       *
       * Taking a computation rather than a value is what makes a deep link one request:
       * `rxMethod` subscribes through an effect, so it reads the parameters the router has
       * already bound instead of firing unfiltered first and filtered second. It re-runs
       * when either input changes, and both go out together.
       */
      bindQuery: _runQuery,

      /** Re-runs the query the rows on screen belong to, after a failure. */
      retry(): void {
        _runQuery(currentQuery());
      },

      /**
       * Appends the next page in server order.
       *
       * `isPending()` is the load-bearing half of the guard. `_querySuperseded$` cancels a
       * page a *later* query overtook, but it cannot help a page started *after* a query
       * was already on the wire: that request carries the old `skip` against the new result
       * set, and when it lands after `setFirstPage` it appends a window from the middle of
       * a list that no longer exists.
       */
      loadMore(): void {
        if (!store.isPending() && !store.loadingMore() && store.hasMore()) {
          _loadNextPage();
        }
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // Server-confirmed facts only, which is the whole contract of this event group.
    events.on(conversationEvents.updated).pipe(
      tap(({ payload }) => {
        // The filter is on document names only, so a rename never evicts a row here — it
        // only has to keep the link column and the group header honest.
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

        // Deleting a conversation takes its documents with it server-side, so their rows go
        // here too rather than 404-ing on the next download. The page in flight goes with
        // them: its URL was built against the pre-removal offset, so letting it land would
        // skip the rows that slid into the gap.
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
