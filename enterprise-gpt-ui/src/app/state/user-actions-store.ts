import { HttpClient } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { Dispatcher } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { exhaustMap, mergeMap, switchMap, takeUntil } from 'rxjs';
import { PermissionDto, UserDto } from '@domain/api/user';
import { AppError, ValidationAppError } from '@core/errors/app-error';
import { traceLine } from '@core/errors/error-message';
import { toAppError } from '@core/errors/to-app-error';
import { adminEvents } from '@core/events/admin-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import { ToastStore } from '@core/notifications/toast-store';
import { SessionStore } from '@core/session/session-store';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';
import { isSelfActionRefusal } from './self-action-messages';

export type UserFormMode = 'create' | 'edit';

/** Carried with each request so a response never renders on a surface that replaced its own. */
type FormFlow = { readonly mode: 'create' } | { readonly mode: 'edit'; readonly id: string };

interface UserActionsState {
  /** The form is open when this is set: a modal on `create`, an offcanvas on `edit`. */
  readonly formMode: UserFormMode | null;
  /** The user being edited. Null while creating one. */
  readonly formTarget: UserDto | null;
  /**
   * A create or update is in flight; it outlives a force-closed surface so a reopened form shows
   * `exhaustMap` is still busy. One flag for both flows, as `exhaustMap` would drop either submit.
   */
  readonly formBusy: boolean;
  /** The validation problem the server returned for the last attempt. */
  readonly formError: ValidationAppError | null;
  /**
   * The directory's 404: `POST api/users` resolves the address through Microsoft Graph first.
   * It has no `errors` dictionary, so it is held apart from {@link formError}.
   */
  readonly formNotFound: string | null;
  /**
   * The email the server rejected; the messages show only while the field still holds it, since
   * Signal Forms has no `setErrors`.
   */
  readonly formRejectedEmail: string | null;
  /** The caller tried to clear their own Administrator grant; rendered in its own wording. */
  readonly formSelfRevokedAdmin: boolean;
  /** Every grantable permission, for the checklist. Loaded once per session. */
  readonly permissions: readonly PermissionDto[];
  readonly permissionsLoading: boolean;
  /**
   * Whether a load has answered, rows or not: the users tab's permission filter waits on it, and
   * a `200 []` read as "not loaded" would leave it waiting forever.
   */
  readonly permissionsLoaded: boolean;
  readonly permissionsError: AppError | null;
  /** The user the deactivate confirmation names. Null keeps it closed. */
  readonly deactivateTarget: UserDto | null;
  /** The caller's own row, whose deactivation was refused; an id keeps prose out of state. */
  readonly selfDeactivateRefusedId: string | null;
  /** Ids whose own request is in flight, so one row's actions disable and not forty. */
  readonly pendingIds: ReadonlySet<string>;
}

const initialState: UserActionsState = {
  formMode: null,
  formTarget: null,
  formBusy: false,
  formError: null,
  formNotFound: null,
  formRejectedEmail: null,
  formSelfRevokedAdmin: false,
  permissions: [],
  permissionsLoading: false,
  permissionsLoaded: false,
  permissionsError: null,
  deactivateTarget: null,
  selfDeactivateRefusedId: null,
  pendingIds: new Set<string>(),
};

/**
 * Root-scoped so a request outlives the screen that started it; reach it only from the admin
 * chunk to keep it off the initial graph. Validation renders inline; everything else toasts.
 */
export const UserActionsStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _toasts: inject(ToastStore),
    _dispatcher: inject(Dispatcher),
    _session: inject(SessionStore),
    _signedOut$: injectSignedOut(),
  })),
  withComputed(({ permissions }) => ({
    /** MCP-derived permissions last, so their captions read as a group rather than interleaved. */
    checklist: computed(() =>
      [...permissions()].sort((left, right) => {
        const byKind = Number(left.mcpServerId !== null) - Number(right.mcpServerId !== null);

        return byKind === 0 ? left.name.localeCompare(right.name) : byKind;
      }),
    ),
  })),
  withMethods((store) => {
    function setPending(id: string, pending: boolean): void {
      // Replaced, never mutated: an in-place `Set` edit keeps the reference, so nothing updates.
      const next = new Set(store.pendingIds());
      if (pending) {
        next.add(id);
      } else {
        next.delete(id);
      }

      patchState(store, { pendingIds: next });
    }

    function clearFormErrors(): Partial<UserActionsState> {
      return {
        formError: null,
        formNotFound: null,
        formRejectedEmail: null,
        formSelfRevokedAdmin: false,
      };
    }

    function isSelf(id: string): boolean {
      return id === store._session.user()?.id;
    }

    /**
     * Create and edit share this store and a surface can be force-closed mid-flight, so the flow
     * names its surface; `formMode()` alone would put a response on the wrong form.
     */
    function rejectForm(cause: unknown, url: string, email: string, flow: FormFlow): void {
      const error = toAppError(cause, { url });
      // For an edit, the same row too: the panel may have reopened on another user.
      const isOwnSurface =
        store.formMode() === flow.mode &&
        (flow.mode === 'create' || store.formTarget()?.id === flow.id);

      if (isOwnSurface && error.kind === 'validation-error') {
        patchState(store, {
          formError: error,
          formNotFound: null,
          formRejectedEmail: email,
          // Rendered at the checkbox, keyed to this request's row rather than the panel's.
          formSelfRevokedAdmin:
            flow.mode === 'edit' && isSelfActionRefusal(error, 'PermissionIds', isSelf(flow.id)),
        });

        return;
      }

      // A create's 404 is the directory (resolved through Microsoft Graph), so it belongs on the
      // email field. An edit's 404 is ambiguous and falls through to the toast.
      if (isOwnSurface && flow.mode === 'create' && error.kind === 'resource-not-found') {
        patchState(store, {
          formError: null,
          formNotFound: error.detail ?? error.message,
          formRejectedEmail: email,
        });

        return;
      }

      if (error.kind === 'validation-error') {
        // `fromError` silences validation errors, assuming they render inline; here they can't.
        store._toasts.show({
          tone: 'error',
          title: 'The user could not be saved',
          detail: error.message,
          traceLine: traceLine(error),
        });
      } else {
        store._toasts.fromError(error);
      }

      // Close only this request's own surface, never an edit panel opened in the meantime.
      if (isOwnSurface) {
        patchState(store, { formMode: null, formTarget: null });
      }
    }

    const _loadPermissions = rxMethod<void>(
      // switchMap: reopening the form while the first load is on the wire replaces it,
      // and only the newest answer may fill the checklist.
      switchMap(() => {
        // Cleared with the error, so it means "this load answered": a retried catalog must not
        // read as settled to the permission filter.
        patchState(store, {
          permissionsLoading: true,
          permissionsLoaded: false,
          permissionsError: null,
        });
        // Not `permissions/all`: it also returns deactivated permissions, and granting one 404s.
        const url = store._api.build('permissions');

        return store._http.get<PermissionDto[]>(url).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            // Set on both arms, not in `finalize`, which also runs on the sign-out unsubscribe
            // and would land after the reset.
            next: (permissions) => patchState(store, { permissions, permissionsLoaded: true }),
            error: (cause: unknown) =>
              patchState(store, {
                permissionsError: toAppError(cause, { url }),
                permissionsLoaded: true,
              }),
            finalize: () => patchState(store, { permissionsLoading: false }),
          }),
        );
      }),
    );

    const _create = rxMethod<{ email: string; permissionIds: readonly string[] }>(
      // exhaustMap: a second submit in flight is a double-click, which would otherwise hit the
      // "already exists" 400 the first just caused. Closes the race under `formBusy`.
      exhaustMap(({ email, permissionIds }) => {
        patchState(store, { formBusy: true, ...clearFormErrors() });
        const url = store._api.build('users');

        return store._http.post<UserDto>(url, { email, permissionIds }).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (created) => {
              store._dispatcher.dispatch(adminEvents.userCreated(created));
              store._toasts.success(`${created.fullName.trim() || created.email} added`);
              patchState(store, { formMode: null, formTarget: null });
            },
            error: (cause: unknown) => rejectForm(cause, url, email, { mode: 'create' }),
            // The only path that always releases the flag, so `cancelForm()` leaves it alone.
            finalize: () => patchState(store, { formBusy: false }),
          }),
        );
      }),
    );

    const _update = rxMethod<{ target: UserDto; permissionIds: readonly string[] }>(
      exhaustMap(({ target, permissionIds }) => {
        patchState(store, { formBusy: true, ...clearFormErrors() });
        setPending(target.id, true);
        const url = store._api.build(`users/${ApiUrl.segment(target.id)}`);

        // Echo the profile: `UpdateUserActionDto` requires all three fields, and the panel edits
        // permissions only.
        const body = {
          firstName: target.firstName,
          lastName: target.lastName,
          email: target.email,
          permissionIds,
        };

        return store._http.put<UserDto>(url, body).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (updated) => {
              // The server's DTO, with the permission set it actually stored.
              store._dispatcher.dispatch(adminEvents.userUpdated(updated));
              store._toasts.success(`${updated.fullName.trim() || updated.email} updated`);
              patchState(store, { formMode: null, formTarget: null });
            },
            error: (cause: unknown) =>
              rejectForm(cause, url, target.email, { mode: 'edit', id: target.id }),
            finalize: () => {
              setPending(target.id, false);
              patchState(store, { formBusy: false });
            },
          }),
        );
      }),
    );

    const _deactivate = rxMethod<{ target: UserDto; onRollback: (() => void) | undefined }>(
      // mergeMap: deactivations of different rows may overlap; `beginDeactivate`'s pending-id
      // guard rules out the same one twice.
      mergeMap(({ target, onRollback }) => {
        setPending(target.id, true);
        const url = store._api.build(`users/${ApiUrl.segment(target.id)}`);

        return store._http.delete<void>(url).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: () => {
              store._toasts.success(`${target.fullName.trim() || target.email} deactivated`);
              store._dispatcher.dispatch(adminEvents.userDeactivated(target.id));
            },
            error: (cause: unknown) => {
              // The row goes back first, so the refusal below has something to render on.
              onRollback?.();

              const error = toAppError(cause, { url });
              if (
                error.kind === 'validation-error' &&
                isSelfActionRefusal(error, 'Id', isSelf(target.id))
              ) {
                // Rendered at the control, never as a toast: only someone else can resolve it.
                patchState(store, { selfDeactivateRefusedId: target.id });

                return;
              }

              store._toasts.fromError(error);
            },
            finalize: () => setPending(target.id, false),
          }),
        );
      }),
    );

    /** Lazy, since this store is root-scoped and most sessions never open the admin area. */
    function ensurePermissions(): void {
      if (store.permissions().length > 0 || store.permissionsLoading()) {
        return;
      }

      _loadPermissions();
    }

    return {
      isRowPending(id: string): boolean {
        return store.pendingIds().has(id);
      },

      /** Shared with the users tab's filter, so it and the edit panel agree on what exists. */
      ensurePermissions,

      retryPermissions(): void {
        _loadPermissions();
      },

      beginCreate(): void {
        // `formBusy` tracks the request, not the form, so it is left alone.
        patchState(store, { formMode: 'create', formTarget: null, ...clearFormErrors() });
        ensurePermissions();
      },

      /** Safe on every close path, including after a submit already closed the form. */
      cancelForm(): void {
        patchState(store, { formMode: null, formTarget: null, ...clearFormErrors() });
      },

      /** The server adds every default to `permissionIds`, so an empty list can still sign in. */
      submitCreate(email: string, permissionIds: readonly string[]): void {
        if (store.formMode() !== 'create' || store.formBusy() || email === '') {
          return;
        }

        _create({ email, permissionIds });
      },

      /** Needs no fetch: `GET api/users` rows carry the full DTO, active grants included. */
      beginEdit(user: UserDto): void {
        if (store.pendingIds().has(user.id)) {
          return;
        }

        patchState(store, { formMode: 'edit', formTarget: user, ...clearFormErrors() });
        ensurePermissions();
      },

      /** `permissionIds` is the complete desired set; the server revokes anything missing. */
      submitEdit(permissionIds: readonly string[]): void {
        const target = store.formTarget();
        if (store.formMode() !== 'edit' || target === null || store.formBusy()) {
          return;
        }

        _update({ target, permissionIds });
      },

      beginDeactivate(user: UserDto): void {
        if (store.pendingIds().has(user.id)) {
          return;
        }

        // The refusal is a standing fact about the row, so only a retry clears it.
        patchState(store, { deactivateTarget: user, selfDeactivateRefusedId: null });
      },

      /** Safe on every close path, confirm included. */
      cancelDeactivate(): void {
        patchState(store, { deactivateTarget: null });
      },

      /** The store outlives the route, and `role="alert"` would re-announce a stale refusal. */
      clearSelfDeactivateRefusal(): void {
        patchState(store, { selfDeactivateRefusedId: null });
      },

      /**
       * The caller removes the row optimistically (`core/` can't import the route-scoped list),
       * so `onRollback` puts it back on any failure.
       */
      confirmDeactivate(onRollback?: () => void): void {
        const target = store.deactivateTarget();
        if (target === null) {
          return;
        }

        patchState(store, { deactivateTarget: null });
        _deactivate({ target, onRollback });
      },
    };
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
