import { HttpClient } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withFeature,
  withMethods,
  withProps,
} from '@ngrx/signals';
import { setAllEntities, withEntities } from '@ngrx/signals/entities';
import { Events, withEventHandlers } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ModelDto } from '@domain/api/model';
import { providerMeta } from '@domain/api/provider';
import { toAppError } from '@core/errors/to-app-error';
import { adminEvents } from '@core/events/admin-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import { setQuery, withClientQuery } from '@core/state/with-client-query';
import {
  setError,
  setFulfilled,
  setPending,
  withRequestStatus,
} from '@core/state/with-request-status';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

/** The text one row is matched against — frame `5e`'s "Search provider or model". */
function searchableText(model: ModelDto): string {
  return `${model.name} ${providerMeta(model.providerId)?.name ?? ''}`;
}

/**
 * The administration model catalog, from `GET api/models/all` (US-1207).
 *
 * **Regime C**: the route is unpaginated and Administrator-gated, so one request is the
 * whole set and search runs entirely on the client. This is `withClientQuery`'s first
 * production consumer — US-104 built it, and `ProjectListStore` has been deferring it to
 * US-902 because a drained-to-a-ceiling set is not authoritative and this one always is.
 *
 * Two of its four config values are shaped by that:
 *
 * - `isAuthoritative` drops the `isFullyLoaded()` half of the feature's own example —
 *   there is no pagination to be partway through — but it cannot be `isFulfilled` alone
 *   either, because every mutation triggers a refetch that puts the store back into
 *   `pending`. It is `isFulfilled() || entities().length > 0`: the rows on screen are
 *   still the whole catalog while the next copy of it is on the wire.
 * - `incompleteSetReason` is then unreachable — nothing here can produce a partial set —
 *   so it says what would have to have gone wrong rather than describing a state the
 *   screen can enter.
 *
 * Unlike `AdminUsersStore` there is no URL contract and no `bindQuery`: a client-side
 * filter over a complete set is screen state, not a shareable server query, and putting
 * it in the address bar would promise a deep link that reproduces a *server* result.
 *
 * The list refetches on every catalog change rather than patching a row, which is
 * `adminEvents.modelCatalogChanged` being void made concrete — see that event's doc.
 */
export const AdminModelsStore = signalStore(
  withEntities<ModelDto>(),
  withRequestStatus(),
  withProps(() => ({
    _http: inject(HttpClient),
    // `models/all`, not `models`: the administrative route is the only one that returns
    // retired models, which the table renders muted rather than hiding (frame `5e`).
    _url: inject(ApiUrl).build('models/all'),
    _signedOut$: injectSignedOut(),
  })),
  withFeature(({ entities, isFulfilled }) =>
    withClientQuery(entities, {
      searchableText,
      // No control selects either of these yet — frame `5e` draws no sort — but the
      // feature requires the map, and these are the two columns a sort would be offered
      // on first. US-902's sibling story on this tab is then a template change.
      comparators: {
        name: (left: ModelDto, right: ModelDto) => left.name.localeCompare(right.name),
        provider: (left: ModelDto, right: ModelDto) =>
          searchableText(left).localeCompare(searchableText(right)),
      },
      // `isFulfilled` **or** rows already held, not `isFulfilled` alone. Every mutation
      // triggers a refetch, which puts the store back into `pending` — and on that alone
      // the set would read as non-authoritative for the length of every save, so US-902's
      // sort control would vanish and come back on each one. The rows on screen are still
      // the whole catalog while the next copy of it is on the wire.
      isAuthoritative: computed(() => isFulfilled() || entities().length > 0),
      incompleteSetReason: 'The catalog did not load completely.',
    }),
  ),
  withComputed(({ entities, results, hasQuery, isEmptyResult, isFulfilled, isPending }) => ({
    /** Nothing on screen yet, so a skeleton is the honest thing to show. */
    isFirstLoad: computed(() => isPending() && entities().length === 0),
    /** Loaded, and the catalog is genuinely empty — distinct from "nothing matched". */
    isEmpty: computed(() => isFulfilled() && entities().length === 0),
    /** Loaded, nothing matched, and a term is why — the empty state that can quote it. */
    hasNoMatches: computed(() => isFulfilled() && isEmptyResult() && hasQuery()),
    /**
     * The count, over the rows actually on screen.
     *
     * It states the filtered figure against the total rather than the total alone,
     * because a client-side filter changes what the table is showing without changing
     * what the server holds, and a count that ignored the filter would contradict the
     * rows underneath it.
     */
    countSummary: computed(() => {
      const total = entities().length;
      const shown = results().length;
      if (total === 0) {
        return 'No models';
      }

      const noun = total === 1 ? 'model' : 'models';

      return hasQuery()
        ? `Showing ${shown} of ${total} ${noun}`
        : `${total} ${noun} in the catalog`;
    }),
  })),
  withMethods((store) => {
    // No `debounceTime`: `SearchInput` already debounces 300 ms and emits only committed
    // values. `switchMap` because a refetch can overlap the one a mutation just started,
    // and only the newest answer may replace the rows.
    const _load = rxMethod<void>(
      pipe(
        tap(() => patchState(store, setPending())),
        switchMap(() =>
          store._http.get<ModelDto[]>(store._url).pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (models) => patchState(store, setAllEntities(models), setFulfilled()),
              error: (cause: unknown) =>
                patchState(store, setError(toAppError(cause, { url: store._url }))),
            }),
          ),
        ),
      ),
    );

    return {
      /** Fetches the catalog. Called once from the screen's constructor. */
      load(): void {
        _load();
      },

      /**
       * Applies the search field's committed term.
       *
       * A method rather than the screen calling `patchState` with `setQuery`: state stays
       * protected, which is the rule every store here keeps.
       */
      search(query: string): void {
        patchState(store, setQuery(query));
      },

      /** Re-reads the catalog after a failure, from `ErrorPanel`'s retry. */
      retry(): void {
        _load();
      },

      /** Re-reads the catalog. What every mutation does to this list. */
      refresh(): void {
        _load();
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // A refetch rather than a patch, and the same handler for all three verbs: a save
    // that sets `isDefault` demotes a row no response describes, and a retire clears
    // `IsDefault` on the row it retires. The catalog is unpaginated and small, so this
    // costs one request and is the only thing that can show either.
    events.on(adminEvents.modelCatalogChanged).pipe(tap(() => store.refresh())),
  ]),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
