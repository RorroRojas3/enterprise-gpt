import { HttpClient } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { tapResponse } from '@ngrx/operators';
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
import { pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ModelDto } from '@shared/models/api/chat-model.model';
import { providerMeta } from '@shared/utils/catalog/provider-meta';
import { toAppError } from '@core/errors/to-app-error';
import { adminEvents } from '@core/events/admin-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url-service';
import { setQuery, withClientQuery } from '@core/state/with-client-query';
import {
  setError,
  setFulfilled,
  setPending,
  withRequestStatus,
} from '@core/state/with-request-status';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

function searchableText(model: ModelDto): string {
  return `${model.name} ${providerMeta(model.providerId)?.name ?? ''}`;
}

/**
 * `GET api/models/all` is unpaginated, so search runs on the client over an always-complete
 * set. No URL contract: a client-side filter is screen state, not a deep link.
 */
export const AdminModelsStore = signalStore(
  withEntities<ModelDto>(),
  withRequestStatus(),
  withProps(() => ({
    _http: inject(HttpClient),
    // The only route that returns retired models, which the table renders muted.
    _url: inject(ApiUrl).build('models/all'),
    _signedOut$: injectSignedOut(),
  })),
  withFeature(({ entities, isFulfilled }) =>
    withClientQuery(entities, {
      searchableText,
      // No control selects these yet, but the feature requires the map.
      comparators: {
        name: (left: ModelDto, right: ModelDto) => left.name.localeCompare(right.name),
        provider: (left: ModelDto, right: ModelDto) =>
          searchableText(left).localeCompare(searchableText(right)),
      },
      // Not `isFulfilled` alone: every mutation refetches, and the rows on screen are still
      // the whole catalog while the next copy is on the wire.
      isAuthoritative: computed(() => isFulfilled() || entities().length > 0),
      incompleteSetReason: 'The catalog did not load completely.',
    }),
  ),
  withComputed(({ entities, results, hasQuery, isEmptyResult, isFulfilled, isPending }) => ({
    isFirstLoad: computed(() => isPending() && entities().length === 0),
    /** The catalog itself is empty, as distinct from nothing matching. */
    isEmpty: computed(() => isFulfilled() && entities().length === 0),
    /** Nothing matched a term, so the empty state can quote it. */
    hasNoMatches: computed(() => isFulfilled() && isEmptyResult() && hasQuery()),
    /** States the filtered count against the total, so it never contradicts the rows shown. */
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
    // `switchMap`: a refetch can overlap one a mutation just started, and only the newest
    // answer may replace the rows.
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
      load(): void {
        _load();
      },

      search(query: string): void {
        patchState(store, setQuery(query));
      },

      retry(): void {
        _load();
      },

      refresh(): void {
        _load();
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // A refetch, not a patch: a save that sets `isDefault` demotes a row no response
    // describes, and a retire clears it on the row it retires.
    events.on(adminEvents.modelCatalogChanged).pipe(tap(() => store.refresh())),
  ]),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
