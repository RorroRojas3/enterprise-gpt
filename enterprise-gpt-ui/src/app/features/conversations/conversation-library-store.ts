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
import { ConversationDto } from '@domain/api/conversation';
import { PaginatedResponseDto } from '@domain/api/paginated-response';
import { conversationEvents } from '@core/events/conversation-events';
import { injectSignedOut } from '@core/events/session-events';
import { toAppError } from '@core/errors/to-app-error';
import { ApiUrl } from '@core/http/api-url';
import { setFirstPage, withOffsetPagination } from '@core/state/with-offset-pagination';
import {
  setError,
  setFulfilled,
  setPending,
  withRequestStatus,
} from '@core/state/with-request-status';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

/**
 * How many rows the library asks for at a time, matching frame `4a`'s own count.
 *
 * Below the server's 1–100 clamp. Separate from `SIDEBAR_PAGE_SIZE` deliberately —
 * that number is chosen so a sidebar rarely needs a second page, and this one is
 * chosen for a table with a Load more control beneath it (US-702).
 */
export const LIBRARY_PAGE_SIZE = 25;

interface ConversationLibraryState {
  /**
   * The `name=` filter the rows on screen belong to.
   *
   * Written when the request starts, not when it commits, so the empty state can
   * name the term that produced it rather than the one still being typed.
   */
  readonly name: string;
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
 * Rows keep the server's order (`dateCreated` descending) and no sort control is
 * offered — the PRD's regime **B**, and US-705's whole subject.
 */
export const ConversationLibraryStore = signalStore(
  withState<ConversationLibraryState>({ name: '' }),
  withEntities<ConversationDto>(),
  withRequestStatus(),
  withOffsetPagination(LIBRARY_PAGE_SIZE),
  withProps(() => ({
    _http: inject(HttpClient),
    _url: inject(ApiUrl).build('conversations/search'),
    _signedOut$: injectSignedOut(),
  })),
  withComputed(({ entities, name, isFulfilled, isPending }) => {
    const hasQuery = computed(() => name().trim() !== '');
    const isEmpty = computed(() => isFulfilled() && entities().length === 0);

    return {
      hasQuery,
      /** Loaded, and there is genuinely nothing — distinct from "not loaded yet". */
      isEmpty,
      /** Loaded, nothing matched, and a filter is why. Frame `4b` rather than `4a`. */
      hasNoMatches: computed(() => isEmpty() && hasQuery()),
      /**
       * Nothing on screen yet, so a skeleton is the honest thing to show. A search
       * that *narrows* an existing list keeps its rows and marks the field busy
       * instead, which is the distinction `Sidebar` draws for the same reason.
       */
      isFirstLoad: computed(() => isPending() && entities().length === 0),
    };
  }),
  withMethods((store) => {
    function searchUrl(name: string): { url: string; params: HttpParams } {
      const trimmed = name.trim();
      let params = new HttpParams().set('skip', '0').set('take', String(store.take()));

      // Omitted rather than sent empty, and never `filter=`: US-701's first
      // criterion, and a blank `name=` would still take the server down its LIKE
      // branch.
      if (trimmed !== '') {
        params = params.set('name', trimmed);
      }

      return { url: store._url, params };
    }

    // No `distinctUntilChanged`: the signal source only emits on a real change, and
    // `retry()` pushes the same term again on purpose.
    const _runQuery = rxMethod<string>(
      pipe(
        tap((name) => patchState(store, { name }, setPending())),
        switchMap((name) => {
          const { url, params } = searchUrl(name);

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

    return {
      /**
       * Binds the screen's `?name=` to the query, once, from its constructor.
       *
       * Taking the signal rather than a value is what makes a deep link one request:
       * `rxMethod` subscribes through an effect, so it reads the term the router has
       * already bound instead of firing unfiltered first and filtered second.
       */
      bindQuery: _runQuery,

      /** Re-runs the term the rows on screen belong to, after a failure. */
      retry(): void {
        _runQuery(store.name());
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // Server-confirmed facts only, which is the whole contract of this event group.
    // A rename or a favourite performed from the sidebar has to land here too, or
    // the two lists disagree about a row they both hold.
    events.on(conversationEvents.updated).pipe(
      tap(({ payload }) => {
        if (store.entityMap()[payload.id] !== undefined) {
          patchState(store, updateEntity({ id: payload.id, changes: payload }));
        }
      }),
    ),

    events.on(conversationEvents.deleted).pipe(
      tap(({ payload: id }) => {
        if (store.entityMap()[id] === undefined) {
          return;
        }

        // Both counters move, as the reference store's `removeRow` does. The count
        // because US-702's "Showing N of M" would otherwise claim a row that is
        // gone, and the **offset** because the server no longer counts the row
        // either — leaving `skip` where it was would make the next page start one
        // row late, and the row that slid into the gap would never be fetched.
        patchState(store, removeEntity(id), (state) => ({
          skip: Math.max(state.skip - 1, 0),
          totalCount: Math.max(state.totalCount - 1, 0),
        }));
      }),
    ),
  ]),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
