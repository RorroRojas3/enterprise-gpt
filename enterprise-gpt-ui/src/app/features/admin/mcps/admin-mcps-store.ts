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
import { McpServerDto } from '@domain/api/mcp';
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

/**
 * The text one row is matched against — frame `5g`'s "Search servers".
 *
 * Name, description and URL, because all three are on the row and an administrator
 * looking for a server as often knows its host as its name. The search field's label
 * names all three, so nothing is searched that the reader was not told about.
 */
function searchableText(server: McpServerDto): string {
  return `${server.name} ${server.description} ${server.url}`;
}

/**
 * The administration MCP server registry, from `GET api/mcps/all` (US-1208).
 *
 * **Regime C**, exactly as the model catalog is: the route is unpaginated and
 * Administrator-gated, so one request *is* the whole set and a filter over it is exact.
 * Two of `withClientQuery`'s four config values are shaped by that, and both for the
 * reasons `AdminModelsStore` records:
 *
 * - `isAuthoritative` is `isFulfilled() || entities().length > 0` rather than
 *   `isFulfilled` alone, because every mutation triggers a refetch that puts the store
 *   back into `pending` — and the rows on screen are still the whole registry while the
 *   next copy of it is on the wire.
 * - `incompleteSetReason` is unreachable, so it says what would have to have gone wrong
 *   rather than describing a state the screen can enter.
 *
 * There is no URL contract and no `bindQuery`: a client-side filter over a complete set
 * is screen state, not a shareable server query.
 *
 * The list refetches on every registry change rather than patching a row, which is
 * `adminEvents.mcpCatalogChanged` being void made concrete — see that event's doc.
 */
export const AdminMcpsStore = signalStore(
  withEntities<McpServerDto>(),
  withRequestStatus(),
  withProps(() => ({
    _http: inject(HttpClient),
    // `mcps/all`, not `mcps`: the user-facing route filters to the caller's own grants
    // and hides deactivated servers, and this table has to show both (frame `5g`).
    _url: inject(ApiUrl).build('mcps/all'),
    _signedOut$: injectSignedOut(),
  })),
  withFeature(({ entities, isFulfilled }) =>
    withClientQuery(entities, {
      searchableText,
      // No control selects either of these yet — frame `5g` draws no sort — but the
      // feature requires the map, and these are the two columns a sort would be offered
      // on first. US-902's sibling story on this tab is then a template change.
      comparators: {
        name: (left: McpServerDto, right: McpServerDto) => left.name.localeCompare(right.name),
        url: (left: McpServerDto, right: McpServerDto) => left.url.localeCompare(right.url),
      },
      isAuthoritative: computed(() => isFulfilled() || entities().length > 0),
      incompleteSetReason: 'The server registry did not load completely.',
    }),
  ),
  withComputed(({ entities, results, hasQuery, isEmptyResult, isFulfilled, isPending }) => ({
    /** Nothing on screen yet, so a skeleton is the honest thing to show. */
    isFirstLoad: computed(() => isPending() && entities().length === 0),
    /** Loaded, and nothing is registered — distinct from "nothing matched". */
    isEmpty: computed(() => isFulfilled() && entities().length === 0),
    /** Loaded, nothing matched, and a term is why — the empty state that can quote it. */
    hasNoMatches: computed(() => isFulfilled() && isEmptyResult() && hasQuery()),
    /**
     * The count, over the rows actually on screen.
     *
     * It states the filtered figure against the total rather than the total alone,
     * because a client-side filter changes what the table is showing without changing
     * what the server holds.
     */
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
    // No `debounceTime`: `SearchInput` already debounces 300 ms and emits only committed
    // values. `switchMap` because a refetch can overlap the one a mutation just started,
    // and only the newest answer may replace the rows.
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
      /** Fetches the registry. Called once from the screen's constructor. */
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

      /** Re-reads the registry after a failure, from `ErrorPanel`'s retry. */
      retry(): void {
        _load();
      },

      /** Re-reads the registry. What every mutation does to this list. */
      refresh(): void {
        _load();
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // A refetch rather than a patch, and the same handler for all three verbs: the list
    // is ordered by name so a new server's slot is the server's to decide, a 204 carries
    // no `dateDeactivated` for the row that has to turn muted, and a rename also renames
    // the linked permission. The registry is unpaginated and small, so this costs one
    // request and is the only thing that can show any of them.
    events.on(adminEvents.mcpCatalogChanged).pipe(tap(() => store.refresh())),
  ]),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
