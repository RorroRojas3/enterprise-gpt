import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  patchState,
  signalStore,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { firstValueFrom, takeUntil } from 'rxjs';
import { McpDto } from '@domain/api/mcp';
import { toAppError } from '@core/errors/to-app-error';
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
  withResetOnSignOut(),
);
