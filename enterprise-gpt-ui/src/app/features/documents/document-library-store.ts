import { HttpClient, HttpParams } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import {
  PartialStateUpdater,
  patchState,
  signalMethod,
  signalStore,
  withComputed,
  withFeature,
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
import { EMPTY, Observable, Subject, expand, merge, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { GENERATED_DOCUMENT_TYPE, UserDocumentDto } from '@domain/api/document';
import { PaginatedResponseDto } from '@domain/api/paginated-response';
import { toAppError } from '@core/errors/to-app-error';
import { conversationEvents } from '@core/events/conversation-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import { setQuery, withClientQuery } from '@core/state/with-client-query';
import {
  OffsetPaginationState,
  resetPagination,
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
 * How many documents each request asks for — the server's own clamp ceiling, so a
 * drain costs the fewest possible round trips.
 */
export const DOCUMENT_PAGE_SIZE = 100;

/**
 * How many documents a drain loads before it stops.
 *
 * The PRD's regime **A**, at the same figure as `PROJECT_LOAD_CEILING`: a fully
 * materialised set, so US-1003's client-side filter and grouping are honest over the
 * whole listing rather than over whichever page happened to be in hand. Five requests
 * at most. Past it the surface states what it is showing rather than letting the
 * shortfall pass for a complete list.
 */
export const DOCUMENT_LOAD_CEILING = 500;

interface DocumentLibraryState {
  /** The drain stopped at {@link DOCUMENT_LOAD_CEILING} with more still on the server. */
  readonly truncated: boolean;
  /**
   * Narrow the listing to what the assistant produced.
   *
   * Served rather than filtered here, unlike the name term: the ceiling would otherwise
   * decide the answer — a reader whose generated files all sit past the 500 most recent
   * documents would be told the assistant has made none — and `totalCount` would keep
   * counting rows the filter removed.
   */
  readonly generatedOnly: boolean;
}

/**
 * One conversation's slice of the grouped view (US-1003, frame `4j`).
 *
 * Built over the *filtered* results, so the same term narrows both view modes: a
 * group whose documents all fail the filter disappears, and {@link countLabel}
 * counts what is actually visible under it rather than what the conversation holds.
 */
export interface DocumentGroup {
  readonly conversationId: string;
  readonly conversationName: string;
  /** `1 file` / `N files`, over the visible documents. */
  readonly countLabel: string;
  readonly documents: readonly UserDocumentDto[];
}

/**
 * The noun the counts and notices use, which the served origin filter changes: a figure
 * over generated documents alone must not read as a figure over the library.
 */
function documentNoun(count: number, generatedOnly: boolean): string {
  const plural = count === 1 ? 'document' : 'documents';

  return generatedOnly ? `generated ${plural}` : plural;
}

/**
 * Moves the offset and the total together, for rows that left the set.
 *
 * Both, for the reason the conversations library documents: the total because the
 * ceiling notice would otherwise count rows that are gone, and the **offset** because
 * it is how many rows are held — and `hasMore` compares the two.
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
 * It copies `ProjectListStore`'s drain, with the same load-bearing details: `expand`
 * rather than a self-referential `concat`, `_querySuperseded$` cancelling the whole
 * chain, `truncated` written where the drain stops and never in a `finalize`, and
 * `takeUntil(signedOut$)` on the request because `withResetOnSignOut` clears state and
 * does not cancel work. A drain is superseded by a retry, a sign-out, or a change of
 * {@link DocumentLibraryState.generatedOnly} — the one filter this route serves.
 *
 * A mid-drain failure **replaces the listing with the error** — the listing regime, not
 * `ProjectLookupStore`'s keep-partial one: a half-drained set counted or grouped as if
 * it were whole is the failure regime A exists to avoid, and Retry re-runs the drain
 * from the first page.
 *
 * The store holds **zero seeded records by construction**: nothing is fetched until the
 * screen binds its origin filter, and no placeholder row ever ships in state, so a user
 * with nothing in the library drains an empty first page and lands on the empty state.
 * The unavailable panel this screen was once specified to show first was never built —
 * the endpoint landed ahead of it, so no client ever shipped without one (product-owner
 * decision 2026-08-19).
 *
 * **`withClientQuery` is composed here as of US-1003**, over `entities` with
 * `isAuthoritative: isFulfilled() && isFullyLoaded()`, exactly as the feature's own doc
 * example shows. The filter and the grouped view both derive from its `results`, so one
 * term narrows both shapes — and a set the drain truncated qualifies its counts through
 * `resultsPartialReason` rather than presenting a partial answer as the whole one.
 */
export const DocumentLibraryStore = signalStore(
  withState<DocumentLibraryState>({ truncated: false, generatedOnly: false }),
  withEntities<UserDocumentDto>(),
  withRequestStatus(),
  withOffsetPagination(DOCUMENT_PAGE_SIZE),
  withProps(() => ({
    _http: inject(HttpClient),
    _url: inject(ApiUrl).build('documents'),
    _signedOut$: injectSignedOut(),
    /**
     * Cancels a drain whose result set no longer exists.
     *
     * `switchMap` cancels the one request in flight when a retry arrives, but the
     * drain is a *chain* of them: without this, a page fetched by the old drain lands
     * after the retry's `setAllEntities` and appends rows twice.
     */
    _querySuperseded$: new Subject<void>(),
  })),
  withFeature(({ entities, generatedOnly, isFulfilled, isFullyLoaded }) =>
    withClientQuery(entities, {
      // The name and nothing else — never the conversation name the rows also carry,
      // which is what the empty state's "Filters cover file names only." promises.
      searchableText: (document: UserDocumentDto) => document.name,
      // No sort story for documents: the server's newest-first order is the only one,
      // so no key is offered and `results` stays in server order.
      comparators: {},
      // Both, not `isFullyLoaded` alone: it reads true before the first fetch.
      isAuthoritative: computed(() => isFulfilled() && isFullyLoaded()),
      // A signal, because the served filter changes which set fell short.
      incompleteSetReason: computed(
        () =>
          `Only the ${DOCUMENT_LOAD_CEILING} most recent ${documentNoun(DOCUMENT_LOAD_CEILING, generatedOnly())} have loaded.`,
      ),
    }),
  ),
  withComputed(
    ({
      entities,
      generatedOnly,
      hasQuery,
      isEmptyResult,
      isFulfilled,
      isPending,
      results,
      totalCount,
    }) => ({
      /**
       * Nothing on screen yet, so skeleton rows are the honest thing to show. Named for
       * symmetry with the other listings, though with the filter client-side every load
       * is the first one.
       */
      isFirstLoad: computed(() => isPending() && entities().length === 0),
      /**
       * Loaded, and the listing holds nothing — which under the served origin filter
       * means nothing *of that origin*, so the screen picks its wording from the filter.
       * Distinct from {@link hasNoMatches} and checked before it: with zero rows the
       * empty set is the truth whatever term a deep link carries.
       */
      isEmpty: computed(() => isFulfilled() && entities().length === 0),
      /** Loaded, documents exist, nothing matched, and a term is why (US-1003). */
      hasNoMatches: computed(
        () => isFulfilled() && entities().length > 0 && isEmptyResult() && hasQuery(),
      ),
      /**
       * The count, over the rows actually on screen.
       *
       * Unfiltered it states the **envelope's** total rather than the rows held, which
       * is what keeps it honest under the ceiling — the ceiling notice already
       * qualifies the shortfall. Filtered it pairs the visible figure with that same
       * total, because a client-side filter changes what the listing shows without
       * changing what the server holds.
       */
      countSummary: computed(() => {
        const total = totalCount();
        const noun = documentNoun(total, generatedOnly());

        if (total === 0) {
          return `No ${noun}`;
        }

        return hasQuery() ? `Showing ${results().length} of ${total} ${noun}` : `${total} ${noun}`;
      }),
      /**
       * The grouped view's shape, over the **filtered** results in first-seen order:
       * the server lists documents newest first, so groups order by their newest
       * visible document, exactly where a refetch would put them.
       */
      groups: computed<readonly DocumentGroup[]>(() => {
        const byConversation = new Map<string, { name: string; documents: UserDocumentDto[] }>();

        for (const document of results()) {
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

        return [...byConversation.entries()].map(([conversationId, group]) => ({
          conversationId,
          conversationName: group.name,
          countLabel: `${group.documents.length} ${group.documents.length === 1 ? 'file' : 'files'}`,
          documents: group.documents,
        }));
      }),
      /** What the screen states when the drain stopped at the ceiling. */
      ceilingNotice: computed(
        () =>
          `Showing the ${entities().length} most recent of ${totalCount()} ${documentNoun(totalCount(), generatedOnly())}.`,
      ),
    }),
  ),
  withMethods((store) => {
    function fetchPage(
      skip: number,
      generatedOnly: boolean,
    ): Observable<PaginatedResponseDto<UserDocumentDto>> {
      let params = new HttpParams().set('skip', String(skip)).set('take', String(store.take()));

      if (generatedOnly) {
        params = params.set('type', GENERATED_DOCUMENT_TYPE);
      }

      return store._http.get<PaginatedResponseDto<UserDocumentDto>>(store._url, { params });
    }

    /**
     * The next step of the drain, or its end.
     *
     * The ceiling is recorded here rather than in a `finalize`, so it is written only
     * when the drain genuinely stopped short — a `finalize` would also run on the
     * cancellation a retry or a sign-out causes, and stamp a partial load as a
     * truncated one.
     */
    function nextPage(generatedOnly: boolean): Observable<PaginatedResponseDto<UserDocumentDto>> {
      if (!store.hasMore()) {
        return EMPTY;
      }

      if (store.skip() >= DOCUMENT_LOAD_CEILING) {
        patchState(store, { truncated: true });

        return EMPTY;
      }

      // The filter is carried through the drain rather than read back off state, so every
      // page of one drain asks for the same set even if the next drain has already written
      // its own filter.
      return fetchPage(store.skip(), generatedOnly).pipe(
        tap((page) => patchState(store, addEntities(page.items), setPage(page))),
      );
    }

    const _drain = rxMethod<boolean>(
      pipe(
        tap(() => {
          store._querySuperseded$.next();
          // The rows go with the request that fetched them. A re-drain under a changed
          // origin would otherwise leave the previous set on screen under the new
          // filter's switch, counted and captioned as if it were that set — and the
          // count region would announce the wrong figure, then announce it again.
          patchState(
            store,
            setAllEntities([] as UserDocumentDto[]),
            resetPagination(),
            { truncated: false },
            setPending(),
          );
        }),
        switchMap((generatedOnly) =>
          fetchPage(0, generatedOnly).pipe(
            tap((first) =>
              patchState(store, setAllEntities(first.items), setFirstPage(first), setFulfilled()),
            ),
            // `expand` rather than a self-referential `concat`: each iteration completes
            // and is dropped, so a five-page drain does not retain five nested
            // subscribers — the same reason `ProjectListStore` uses it.
            expand(() => nextPage(generatedOnly)),
            // On the whole chain, not the first request: `switchMap` cancels only what
            // is in flight when the next trigger arrives, and the drain is a sequence.
            takeUntil(merge(store._signedOut$, store._querySuperseded$)),
            tapResponse({
              // Each page is written by the taps above; this arm exists so that a
              // failure mid-drain does not tear down the rxMethod's own subscription.
              next: () => undefined,
              // Whichever page failed, the panel replaces the listing — see the class
              // doc — and Retry re-runs the drain from the first page. The dead pages
              // go with it: `setPending` alone would re-show a half-drained set for
              // Retry's first round trip, and the event handlers would keep patching
              // rows that are already condemned.
              error: (cause: unknown) =>
                patchState(
                  store,
                  setAllEntities([] as UserDocumentDto[]),
                  resetPagination(),
                  setError(toAppError(cause, { url: store._url })),
                ),
            }),
          ),
        ),
      ),
    );

    return {
      /**
       * Binds the screen's `?source=` and starts the listing: the first value runs the
       * first drain, so a deep link fetches the set it asked for rather than fetching
       * everything and correcting itself. `_drain`'s `switchMap` supersedes the one in
       * flight when the value changes.
       */
      bindSource: signalMethod<boolean>((generatedOnly) => {
        patchState(store, { generatedOnly });
        _drain(generatedOnly);
      }),

      /**
       * Binds the screen's `?name=` to the client-side filter, once, from its
       * constructor.
       *
       * `signalMethod`, not `rxMethod`: the filter narrows rows the store already
       * holds, so there is no request to race and nothing for `switchMap` to cancel.
       * Handing it the input signal is what makes a deep link filter on its very
       * first render.
       */
      bindQuery: signalMethod<string>((term) => patchState(store, setQuery(term))),

      /** Re-runs the drain from the first page, after a failure. */
      retry(): void {
        _drain(store.generatedOnly());
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // Server-confirmed facts only, which is the whole contract of this event group.
    events.on(conversationEvents.updated).pipe(
      tap(({ payload }) => {
        // US-1003's client filter is on document names only, so a rename never evicts
        // a row here — it only has to keep the link column honest.
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

        // Deleting a conversation takes its documents with it server-side, so their
        // rows go here too rather than 404-ing on the next download.
        patchState(
          store,
          removeEntities((document: UserDocumentDto) => document.conversationId === conversationId),
          shiftCounters(-removed),
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
