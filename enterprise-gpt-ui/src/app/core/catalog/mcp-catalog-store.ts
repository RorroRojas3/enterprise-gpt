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
import { ApiUrl } from '@core/api/api-url';
import { McpCredentialStatusDto, McpDto } from '@core/api/mcp.model';
import {
  setError,
  setFulfilled,
  setIdle,
  setPending,
  withRequestStatus,
} from '@core/api/with-request-status';
import { toAppError } from '@core/errors/to-app-error';
import { adminEvents } from '@core/events/admin-events';
import { mcpEvents } from '@core/events/mcp-events';
import { injectSignedOut } from '@core/events/session-events';
import { withResetOnSignOut } from '@core/session/with-reset-on-sign-out';

interface McpCatalogState {
  readonly servers: readonly McpDto[];
}

const initialState: McpCatalogState = { servers: [] };

/**
 * Returns no change for a server this user cannot see, so an unrelated event doesn't allocate a
 * new array and invalidate every downstream computed.
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
 * The MCP servers the signed-in user may use, filtered by grant server-side: each server's
 * permission is created at run time, so no client constant could name it.
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
      // Only the newest attempt may write, so a stale in-flight load can't overwrite a reload.
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
          // Sign-out cancelled the request: idle, since the user's own action isn't an error.
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
      // The memo is a prop, so the reset below does not reach it.
      store._signedOut$.pipe(takeUntilDestroyed()).subscribe(() => {
        store._load.promise = null;
      });
    },
  }),
  // Keeps the Tools menu from offering a deactivated server; `reload()` since the resolved memo
  // would return the stale list. The credential event carries its row's answer, so it patches.
  withEventHandlers((store, events = inject(Events)) => [
    events.on(adminEvents.mcpCatalogChanged).pipe(tap(() => void store.reload())),
    events
      .on(mcpEvents.credentialChanged)
      .pipe(tap(({ payload }) => patchState(store, applyCredentialStatus(payload)))),
  ]),
  withResetOnSignOut(),
);
