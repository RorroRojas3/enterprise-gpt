import { computed } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';
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
import { PERMISSION_ID } from '@domain/api/permission-ids';
import { UserDto } from '@domain/api/user';
import { injectSignedOut } from '@core/events/session-events';
import { toAppError } from '@core/errors/to-app-error';
import { ApiUrl } from '@core/http/api-url';
import {
  setError,
  setFulfilled,
  setIdle,
  setPending,
  withRequestStatus,
} from '@core/state/with-request-status';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

interface SessionState {
  readonly user: UserDto | null;
}

const initialState: SessionState = { user: null };

/**
 * Permissions arrive inline on `UserDto`. Zero grants is a valid session: success is
 * `user() !== null`, never a permission count.
 */
export const SessionStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withRequestStatus(),
  withProps(() => ({
    _http: inject(HttpClient),
    _url: inject(ApiUrl).build('users/me'),
    _signedOut$: injectSignedOut(),
    /** Not state: a promise handle is not a rendering input. */
    _bootstrap: { promise: null as Promise<boolean> | null, generation: 0 },
  })),
  withComputed(({ user }) => ({
    isLoaded: computed(() => user() !== null),
    /** By id: MCP-derived grants are created at run time and have no constant. */
    permissionIds: computed(() => new Set((user()?.permissions ?? []).map(({ id }) => id))),
    displayName: computed(() => user()?.fullName ?? ''),
  })),
  withComputed(({ permissionIds }) => ({
    isAdministrator: computed(() => permissionIds().has(PERMISSION_ID.administrator)),
    /** Never inferred from Administrator: that grant alone does not allow uploads. */
    canUploadFiles: computed(() => permissionIds().has(PERMISSION_ID.uploadFile)),
  })),
  withMethods((store) => {
    async function load(): Promise<boolean> {
      // Only the newest attempt may write; a generation counter rather than `switchMap`
      // because guards await this.
      const generation = ++store._bootstrap.generation;
      const isCurrent = (): boolean => generation === store._bootstrap.generation;

      patchState(store, setPending());

      try {
        // Without the takeUntil, a response already on the wire would write the previous
        // user back after sign-out cleared state.
        const user = await firstValueFrom(
          store._http.post<UserDto>(store._url, null).pipe(takeUntil(store._signedOut$)),
          { defaultValue: null },
        );

        if (!isCurrent()) {
          return false;
        }

        if (user === null) {
          // Sign-out cancelled it: not `pending`, which no screen renders, and not an error,
          // which would send `sessionGuard` to the startup-failure screen.
          patchState(store, setIdle());

          return false;
        }

        patchState(store, { user }, setFulfilled());

        return true;
      } catch (cause) {
        if (!isCurrent()) {
          return false;
        }

        patchState(store, { user: null }, setError(toAppError(cause, { url: store._url })));

        return false;
      }
    }

    return {
      /** Memoized: `canActivate` re-runs on every navigation. */
      ensureLoaded(): Promise<boolean> {
        return (store._bootstrap.promise ??= load());
      },

      /**
       * The startup-failure screen's Retry, and this POST's only one: `retryInterceptor` is
       * restricted to GETs.
       */
      reload(): Promise<boolean> {
        return (store._bootstrap.promise = load());
      },

      /** For MCP-derived grants; the two compile-time grants have named computeds. */
      hasPermission(id: string): boolean {
        return store.permissionIds().has(id);
      },
    };
  }),
  withHooks({
    onInit(store) {
      // withResetOnSignOut does not clear props; left alone, the next user would inherit
      // the previous user's resolved bootstrap.
      store._signedOut$.pipe(takeUntilDestroyed()).subscribe(() => {
        store._bootstrap.promise = null;
      });
    },
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
