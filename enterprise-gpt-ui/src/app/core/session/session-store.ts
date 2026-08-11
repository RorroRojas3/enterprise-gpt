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
 * Who is signed in and what they are allowed to do.
 *
 * It lives in `core/` for the reason `ToastStore` records: app-wide singleton state
 * written from guards that also live in `core/`, and `shared/` holds no store.
 *
 * Permissions arrive inline on `UserDto`, so a session costs one request rather than
 * two — and three states stay distinguishable, which US-202 requires:
 *
 * | | `requestStatus` | `user()` |
 * | --- | --- | --- |
 * | not loaded | `idle` | `null` |
 * | loaded, no grants at all | `fulfilled` | set, `permissions: []` |
 * | failed | `{ error }` | `null` |
 *
 * A user with zero grants is a perfectly valid answer, so nothing here may treat an
 * empty `permissions` array as a load failure. Success is `user() !== null`, never a
 * permission count.
 */
export const SessionStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withRequestStatus(),
  withProps(() => ({
    _http: inject(HttpClient),
    _url: inject(ApiUrl).build('users/me'),
    _signedOut$: injectSignedOut(),
    /**
     * The in-flight bootstrap. A promise handle is not a rendering input, so it lives
     * here rather than in state, where every guard run would re-render every consumer.
     */
    _bootstrap: { promise: null as Promise<boolean> | null, generation: 0 },
  })),
  withComputed(({ user }) => ({
    isLoaded: computed(() => user() !== null),
    /** Matched by id throughout: MCP-derived grants are created at run time and have no constant. */
    permissionIds: computed(() => new Set((user()?.permissions ?? []).map(({ id }) => id))),
    displayName: computed(() => user()?.fullName ?? ''),
  })),
  withComputed(({ permissionIds }) => ({
    isAdministrator: computed(() => permissionIds().has(PERMISSION_ID.administrator)),
    /**
     * Deliberately independent of {@link isAdministrator}. An administrator who does
     * not hold `Upload File` may not upload, and US-204 forbids inferring one from
     * the other — two separate computeds make that impossible to get wrong.
     */
    canUploadFiles: computed(() => permissionIds().has(PERMISSION_ID.uploadFile)),
  })),
  withMethods((store) => {
    async function load(): Promise<boolean> {
      // A reload started while an earlier load is still in flight must not be
      // overwritten by it. `rxMethod` + `switchMap` is the usual answer, but a guard
      // has to await this, so the same guarantee is spelled out with a generation
      // counter: only the newest attempt is allowed to write.
      const generation = ++store._bootstrap.generation;
      const isCurrent = (): boolean => generation === store._bootstrap.generation;

      patchState(store, setPending());

      try {
        // Clearing state on sign-out is not the same as cancelling work: without the
        // takeUntil, a response already on the wire would write the previous user
        // back over the state that was just emptied.
        const user = await firstValueFrom(
          store._http.post<UserDto>(store._url, null).pipe(takeUntil(store._signedOut$)),
          { defaultValue: null },
        );

        if (!isCurrent()) {
          return false;
        }

        if (user === null) {
          // Sign-out cancelled the request. Leaving `pending` behind would strand the
          // store in a state no screen renders, and reporting it as an error would
          // send `sessionGuard` to frame 6c over the user's own deliberate action.
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
      /**
       * Resolves the session, once. `canActivate` re-runs on every navigation, so an
       * unmemoized load would re-issue `POST api/users/me` on each one.
       */
      ensureLoaded(): Promise<boolean> {
        return (store._bootstrap.promise ??= load());
      },

      /**
       * Re-issues the bootstrap. This is frame `6c`'s Retry, and it is the only retry
       * this call gets: `retryInterceptor` is restricted to GETs, so a transient 502
       * on this POST reaches the user rather than being replayed silently.
       */
      reload(): Promise<boolean> {
        return (store._bootstrap.promise = load());
      },

      /**
       * Whether the signed-in user holds a grant. Takes an id because the permissions
       * behind MCP servers are data, not constants; the two compile-time grants have
       * named computeds instead.
       */
      hasPermission(id: string): boolean {
        return store.permissionIds().has(id);
      },
    };
  }),
  withHooks({
    onInit(store) {
      // withResetOnSignOut clears state and nothing else, and the bootstrap memo is a
      // prop rather than state. Left alone it would survive sign-out, and the next
      // user on a shared machine would inherit the previous user's resolved promise
      // and never call POST api/users/me at all.
      store._signedOut$.pipe(takeUntilDestroyed()).subscribe(() => {
        store._bootstrap.promise = null;
      });
    },
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
