import { HttpClient } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { Events, withEventHandlers } from '@ngrx/signals/events';
import { firstValueFrom, takeUntil, tap } from 'rxjs';
import { ApiUrl } from '@core/api/api-url';
import { ModelDto } from '@core/api/chat-model.model';
import {
  setError,
  setFulfilled,
  setIdle,
  setPending,
  withRequestStatus,
} from '@core/api/with-request-status';
import { toAppError } from '@core/errors/to-app-error';
import { adminEvents } from '@core/events/admin-events';
import { injectSignedOut } from '@core/events/session-events';
import { withResetOnSignOut } from '@core/session/with-reset-on-sign-out';

interface ModelCatalogState {
  readonly models: readonly ModelDto[];
}

const initialState: ModelCatalogState = { models: [] };

/**
 * The models this deployment offers; a short read-only list, so no `withEntities`. Loaded by
 * `SessionBootstrap` after the session resolves, since a racing request would lack a token.
 */
export const ModelCatalogStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withRequestStatus(),
  withProps(() => ({
    _http: inject(HttpClient),
    _url: inject(ApiUrl).build('models'),
    _signedOut$: injectSignedOut(),
    _load: { promise: null as Promise<boolean> | null, generation: 0 },
  })),
  withComputed(({ models }) => ({
    /** The model a new turn starts on. Null until the catalog has loaded. */
    defaultModel: computed(() => models().find((model) => model.isDefault) ?? null),
  })),
  withMethods((store) => {
    async function load(): Promise<boolean> {
      // Only the newest attempt may write, so a stale in-flight load can't overwrite a reload.
      const generation = ++store._load.generation;
      const isCurrent = (): boolean => generation === store._load.generation;

      patchState(store, setPending());

      try {
        const models = await firstValueFrom(
          store._http.get<ModelDto[]>(store._url).pipe(takeUntil(store._signedOut$)),
          { defaultValue: null },
        );

        if (!isCurrent()) {
          return false;
        }

        if (models === null) {
          // Sign-out cancelled the request: idle, since the user's own action isn't an error.
          patchState(store, setIdle());

          return false;
        }

        patchState(store, { models }, setFulfilled());

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
  // Keeps the model menu from offering a retired model, which 404s; `reload()` since the
  // resolved memo would return the stale list.
  withEventHandlers((store, events = inject(Events)) => [
    events.on(adminEvents.modelCatalogChanged).pipe(tap(() => void store.reload())),
  ]),
  withResetOnSignOut(),
);
