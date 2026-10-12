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
import { McpServerDto } from '@shared/models/api/mcp.model';
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

/** Includes the URL because administrators often know a server by its host; the label says so. */
function searchableText(server: McpServerDto): string {
  return `${server.name} ${server.description} ${server.url}`;
}

/**
 * The admin MCP server registry. The route is unpaginated, so one request is the whole set and
 * the filter is client-side screen state with no URL contract. `isAuthoritative` also accepts
 * loaded rows because each mutation's refetch flips the store back to pending.
 */
export const AdminMcpsStore = signalStore(
  withEntities<McpServerDto>(),
  withRequestStatus(),
  withProps(() => ({
    _http: inject(HttpClient),
    // `mcps/all`, not `mcps`: the user-facing route filters to the caller's own grants
    // and hides deactivated servers, and this table has to show both.
    _url: inject(ApiUrl).build('mcps/all'),
    _signedOut$: injectSignedOut(),
  })),
  withFeature(({ entities, isFulfilled }) =>
    withClientQuery(entities, {
      searchableText,
      // No control sorts yet, but the feature requires the map; these are the columns a
      // sort would be offered on first.
      comparators: {
        name: (left: McpServerDto, right: McpServerDto) => left.name.localeCompare(right.name),
        url: (left: McpServerDto, right: McpServerDto) => left.url.localeCompare(right.url),
      },
      isAuthoritative: computed(() => isFulfilled() || entities().length > 0),
      incompleteSetReason: 'The server registry did not load completely.',
    }),
  ),
  withComputed(({ entities, results, hasQuery, isEmptyResult, isFulfilled, isPending }) => ({
    isFirstLoad: computed(() => isPending() && entities().length === 0),
    isEmpty: computed(() => isFulfilled() && entities().length === 0),
    hasNoMatches: computed(() => isFulfilled() && isEmptyResult() && hasQuery()),
    countSummary: computed(() => {
      const total = entities().length;
      const shown = results().length;
      if (total === 0) {
        return 'No MCP servers';
      }

      const noun = total === 1 ? 'server' : 'servers';

      return hasQuery()
        ? `Showing ${shown} of ${total} ${noun}`
        : `${total} MCP ${noun} registered`;
    }),
  })),
  withMethods((store) => {
    // `switchMap`: a refetch can overlap the one a mutation just started, and only the
    // newest answer may replace the rows.
    const _load = rxMethod<void>(
      pipe(
        tap(() => patchState(store, setPending())),
        switchMap(() =>
          store._http.get<McpServerDto[]>(store._url).pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (servers) => patchState(store, setAllEntities(servers), setFulfilled()),
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
    // Refetch rather than patch: the server orders rows, a 204 carries no `dateDeactivated`,
    // and a rename also renames the linked permission. The registry is small.
    events.on(adminEvents.mcpCatalogChanged).pipe(tap(() => store.refresh())),
  ]),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
