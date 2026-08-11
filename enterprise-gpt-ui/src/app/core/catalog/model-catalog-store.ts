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
import { firstValueFrom, takeUntil } from 'rxjs';
import { ModelDto } from '@domain/api/model';
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

interface ModelCatalogState {
  readonly models: readonly ModelDto[];
}

const initialState: ModelCatalogState = { models: [] };

/**
 * The models this deployment offers, from `GET api/models`.
 *
 * A short, read-only list rather than `withEntities`: nothing updates or removes a
 * model by id here, and the administrative catalog that does is a separate store
 * behind the admin route (EP-12).
 *
 * Loaded by `SessionBootstrap` after the session resolves, never alongside it — the
 * route is authenticated, so a request racing the bootstrap would go out without a
 * usable token.
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
      // Only the newest attempt may write: a reload started from a UI control while an
      // earlier load is still in flight must not be overwritten by it. See SessionStore.
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
          // Sign-out cancelled the request; idle rather than an error, which is not
          // what the user's own action deserves to be reported as.
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
      // The memo is a prop, so the reset below does not reach it. See SessionStore.
      store._signedOut$.pipe(takeUntilDestroyed()).subscribe(() => {
        store._load.promise = null;
      });
    },
  }),
  withResetOnSignOut(),
);
