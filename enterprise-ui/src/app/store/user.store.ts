import { computed } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withState,
} from '@ngrx/signals';
import { ADMINISTRATOR_PERMISSION_ID } from '../dtos/PermissionDto';
import { UserDto } from '../dtos/UserDto';

type UserState = {
  user: UserDto | null;
  isInitialized: boolean;
};

const initialState: UserState = {
  user: null,
  isInitialized: false,
};

export const UserStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withComputed((store) => ({
    /** True when the authenticated user holds the Administrator permission. */
    isAdmin: computed(
      () =>
        store
          .user()
          ?.permissions.some((p) => p.id === ADMINISTRATOR_PERMISSION_ID) ??
        false,
    ),
  })),
  withMethods((store) => ({
    /**
     * Stores the authenticated user (with permissions) and marks the store
     * initialized. Called once from `AppComponent` after `createUser()` resolves.
     *
     * @param user - The authenticated user returned by the API.
     */
    setUser(user: UserDto): void {
      patchState(store, { user, isInitialized: true });
    },
    /** Resets the store to its initial state (e.g. on logout). */
    clear(): void {
      patchState(store, initialState);
    },
  })),
);
