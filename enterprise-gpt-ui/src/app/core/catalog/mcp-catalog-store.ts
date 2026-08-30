import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  PartialStateUpdater,
  patchState,
  signalStore,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { Events, withEventHandlers } from '@ngrx/signals/events';
import { firstValueFrom, takeUntil, tap } from 'rxjs';
import { McpCredentialStatusDto, McpDto } from '@domain/api/mcp';
import { toAppError } from '@core/errors/to-app-error';
import { adminEvents } from '@core/events/admin-events';
import { mcpEvents } from '@core/events/mcp-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import {
  setError,
  setFulfilled,
  setIdle,
  setPending,
  withRequestStatus,
} from '@core/state/with-request-status';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

interface McpCatalogState {
  readonly servers: readonly McpDto[];
}

const initialState: McpCatalogState = { servers: [] };

/**
 * Applies one server's confirmed credential state to the catalogue.
 *
 * A standalone updater rather than an inline literal, as `withRequestStatus`'s setters are:
 * it tree-shakes and tests without a `TestBed`. Returns the state unchanged when the event
 * names a server this user cannot see, so an unrelated event does not invalidate every
 * downstream computed by allocating a new array.
 */
function applyCredentialStatus(
  status: McpCredentialStatusDto,
): PartialStateUpdater<McpCatalogState> {
  return (state) =>
    state.servers.some((server) => server.id === status.mcpServerId)
      ? {
          servers: state.servers.map((server) =>
            server.id === status.mcpServerId
              ? { ...server, hasUserApiKey: status.hasApiKey, apiKeyHint: status.apiKeyHint }
              : server,
          ),
        }
      : {};
}

/**
 * The MCP tool servers the signed-in user may use, from `GET api/mcps`.
 *
 * The route is already filtered to the caller's grants server-side, so this list is
 * rendered as it arrives. Filtering it again here would be impossible anyway: the
 * permission behind each server is created per server at run time and no client
 * constant names it.
 *
 * Reloaded per session rather than cached across users, because the filter is the
 * user's own permission set.
 */
export const McpCatalogStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withRequestStatus(),
  withProps(() => ({
    _http: inject(HttpClient),
    _url: inject(ApiUrl).build('mcps'),
    _signedOut$: injectSignedOut(),
    _load: { promise: null as Promise<boolean> | null, generation: 0 },
  })),
  withMethods((store) => {
    async function load(): Promise<boolean> {
      // Only the newest attempt may write: a reload started from a UI control while an
      // earlier load is still in flight must not be overwritten by it. See SessionStore.
      const generation = ++store._load.generation;
      const isCurrent = (): boolean => generation === store._load.generation;

      patchState(store, setPending());

      try {
        const servers = await firstValueFrom(
          store._http.get<McpDto[]>(store._url).pipe(takeUntil(store._signedOut$)),
          { defaultValue: null },
        );

        if (!isCurrent()) {
          return false;
        }

        if (servers === null) {
          // Sign-out cancelled the request; idle rather than an error, which is not
          // what the user's own action deserves to be reported as.
          patchState(store, setIdle());

          return false;
        }

        patchState(store, { servers }, setFulfilled());

        return true;
      } catch (cause) {
        if (!isCurrent()) {
          return false;
        }

        patchState(store, setError(toAppError(cause, { url: store._url })));

        return false;
      }
    }

    return {
      ensureLoaded(): Promise<boolean> {
        return (store._load.promise ??= load());
      },

      reload(): Promise<boolean> {
        return (store._load.promise = load());
      },
    };
  }),
  withHooks({
    onInit(store) {
      // The memo is a prop, so the reset below does not reach it. See SessionStore.
      store._signedOut$.pipe(takeUntilDestroyed()).subscribe(() => {
        store._load.promise = null;
      });
    },
  }),
  // The administration area registers, renames and deactivates these servers; this
  // catalogue is what the composer's Tools menu reads. Without this the picker goes on
  // offering a server whose permission was revoked from everyone the moment it was
  // deactivated, and a turn started on one fails.
  //
  // `reload()` rather than `ensureLoaded()`: the memo is already resolved by the time an
  // administrator can change anything, so the memoized call would hand back the stale
  // list — and `reload()` also replaces the memo, so a later `ensureLoaded()` sees the
  // fresh one. Mirrors `ModelCatalogStore`.
  //
  // The credential event patches one row instead, because unlike the catalogue events it
  // arrives carrying the server's own answer for exactly the server it names.
  withEventHandlers((store, events = inject(Events)) => [
    events.on(adminEvents.mcpCatalogChanged).pipe(tap(() => void store.reload())),
    events
      .on(mcpEvents.credentialChanged)
      .pipe(tap(({ payload }) => patchState(store, applyCredentialStatus(payload)))),
  ]),
  withResetOnSignOut(),
);
