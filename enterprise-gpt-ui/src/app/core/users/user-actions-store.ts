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

/** What the user form is doing, which decides its surface, its heading and its verb. */
export type UserFormMode = 'create' | 'edit';

/**
 * Which flow a failed request came from.
 *
 * Carried through the request rather than read back off the store, so a response cannot
 * be rendered on a surface that replaced the one it belongs to.
 */
type FormFlow = { readonly mode: 'create' } | { readonly mode: 'edit'; readonly id: string };

interface UserActionsState {
  /** The form is open when this is set: a modal on `create`, an offcanvas on `edit`. */
  readonly formMode: UserFormMode | null;
  /** The user being edited. Null while creating one. */
  readonly formTarget: UserDto | null;
  /**
   * A create or update is in flight: the surface blocks dismissal and disables its
   * buttons. In-flight tracking, not dialog state — a surface force-closed mid-flight
   * (a second Escape is uncancelable in Chromium) leaves this true until the request
   * settles, so a reopened form honestly reports that `exhaustMap` is still occupied
   * rather than swallowing the next submit silently.
   *
   * One flag for both flows, deliberately: `exhaustMap` would drop a submit made while
   * the other flow's request is on the wire, so a form that opened *enabled* in that
   * window would accept a press and do nothing. Disabled is the honest state, and the
   * window needs a force-close mid-flight to reach at all. Which surface a *failure*
   * belongs to is a separate question, and one this flag deliberately does not answer —
   * see `rejectForm`.
   */
  readonly formBusy: boolean;
  /** The validation problem the server returned for the last attempt. */
  readonly formError: ValidationAppError | null;
  /**
   * The 404 the *directory* answered, which is not a validation problem and carries no
   * `errors` dictionary — `POST api/users` resolves the address through Microsoft Graph
   * before it writes anything, and answers `/problems/resource-not-found` when no
   * directory user has it. Held apart from {@link formError} because it reaches the same
   * field by a different route.
   */
  readonly formNotFound: string | null;
  /**
   * The exact email the server rejected. The form shows the messages above only while
   * the field still holds this value, which is what makes the inline error clear itself
   * on the next edit without an imperative reset — Signal Forms has no `setErrors`.
   */
  readonly formRejectedEmail: string | null;
  /**
   * The server refused to let the caller clear their own Administrator grant (US-1203).
   *
   * Held as its own flag rather than left in {@link formError} for the edit panel to
   * pick apart, because the message the board asks for is not the server's: it is the
   * same fact worded back at the reader, with the way out named.
   */
  readonly formSelfRevokedAdmin: boolean;
  /** Every grantable permission, for the checklist. Loaded once per session. */
  readonly permissions: readonly PermissionDto[];
  readonly permissionsLoading: boolean;
  /**
   * Whether a load has answered, separately from whether it answered with rows.
   *
   * `permissions.length > 0` cannot stand in for this. For the checklist the difference
   * only ever cost a redundant refetch, but the users tab's permission filter waits on
   * the catalog before it can decide whether a `?permission=` names anything (US-1206) —
   * and a `200 []` that reads as "not loaded yet" leaves that wait unending.
   */
  readonly permissionsLoaded: boolean;
  readonly permissionsError: AppError | null;
  /** The user the deactivate confirmation names. Null keeps it closed. */
  readonly deactivateTarget: UserDto | null;
  /**
   * The row whose deactivation the server refused because it is the caller's own
   * (US-1204).
   *
   * An id rather than a message: there is exactly one refusal this can be, frame `5d`
   * words it, and holding the server's prose here would put display text in state.
   */
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
 * The create, edit and deactivate flows the administration user surfaces share.
 *
 * Root-scoped for the reason `ProjectActionsStore` is: `rxMethod` binds its subscription
 * to the injector that created it, and every one of these requests can outlive the
 * screen that started it — an administrator who confirms a deactivation and then clicks
 * another admin tab would otherwise have the request torn down mid-flight, with the row
 * already gone optimistically, nothing deleted and nothing said.
 *
 * **It must stay reachable only from the admin chunk.** `providedIn: 'root'` is a
 * dependency-injection scope, not a chunk assignment, so this file rides the lazy admin
 * bundle exactly as long as nothing eagerly reachable imports it — and the initial graph
 * has under a kilobyte of headroom.
 *
 * The boundaries are the ones the project and conversation stores document:
 *
 * - **Entity writes travel as events** (`adminEvents`), never as `patchState` into
 *   another store's protected state. `AdminUsersStore` is scoped to its route and lives
 *   in `features/`, which `core/` may not import.
 * - **Validation problems render inline** on the open form and are never toasted;
 *   everything else goes through `ToastStore.fromError`.
 * - **Events carry server-confirmed facts only.**
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
    /**
     * The checklist's rows, MCP-derived ones last.
     *
     * The server orders by name, which interleaves the two kinds; the boards draw the
     * grantable ones first and the server-managed ones together at the end, where their
     * captions read as a group rather than as four unrelated asides.
     */
    checklist: computed(() =>
      [...permissions()].sort((left, right) => {
        const byKind = Number(left.mcpServerId !== null) - Number(right.mcpServerId !== null);

        return byKind === 0 ? left.name.localeCompare(right.name) : byKind;
      }),
    ),
  })),
  withMethods((store) => {
    function setPending(id: string, pending: boolean): void {
      // Replaced, never mutated: `patchState` compares by reference, so an in-place `Set`
      // edit performs no signal write at all and the row silently stops updating.
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

    /** Whether a row is the signed-in administrator's own. */
    function isSelf(id: string): boolean {
      return id === store._session.user()?.id;
    }

    /**
     * Renders a rejection inline on the surface that produced it, and as a toast when
     * that surface is no longer the one on screen.
     *
     * **The flow identifies itself; nothing is inferred from what is open.** Create and
     * edit share this store, and a surface can be force-closed mid-flight — the second
     * Escape is uncancelable in Chromium — so by the time a response lands the *other*
     * form may be up. Deciding from `formMode()` alone puts a save's 404 under a fresh
     * create form's email field, a create's 400 in the edit panel's alert region, and an
     * unrelated 500 in charge of closing a panel whose checkbox edits are unsaved.
     */
    function rejectForm(cause: unknown, url: string, email: string, flow: FormFlow): void {
      const error = toAppError(cause, { url });
      // Still the same surface, and — for an edit — still the same row: reopening the
      // panel on somebody else while a save is in flight is the same mistake in miniature.
      const isOwnSurface =
        store.formMode() === flow.mode &&
        (flow.mode === 'create' || store.formTarget()?.id === flow.id);

      if (isOwnSurface && error.kind === 'validation-error') {
        patchState(store, {
          formError: error,
          formNotFound: null,
          formRejectedEmail: email,
          // The one refusal the panel renders at a checkbox rather than as a field
          // message, in the board's own second-person wording. Keyed to the row this
          // request was about, not to whatever the panel now holds.
          formSelfRevokedAdmin:
            flow.mode === 'edit' && isSelfActionRefusal(error, 'PermissionIds', isSelf(flow.id)),
        });

        return;
      }

      // A 404 from `POST api/users` is the *directory*, not a missing route: the service
      // resolves the address through Microsoft Graph before it writes anything. It has no
      // `errors` dictionary, so it cannot travel the `serverMessagesFor` path — but it is
      // about the email field just the same, and a toast would leave the reader guessing
      // which of the two things they typed was wrong.
      //
      // The edit path has no email field to render it against, and `PUT api/users/{id}`
      // answers 404 too — for a row somebody else deactivated, or a permission id that no
      // longer exists — so that one falls through to the toast below rather than stopping
      // its spinner and saying nothing.
      if (isOwnSurface && flow.mode === 'create' && error.kind === 'resource-not-found') {
        patchState(store, {
          formError: null,
          formNotFound: error.detail ?? error.message,
          formRejectedEmail: email,
        });

        return;
      }

      if (error.kind === 'validation-error') {
        // `fromError` silences validation problems on the assumption they render inline;
        // with no inline to render on there is nothing to say them, so this one is raised
        // by hand — the same deliberate exception the project dialog makes.
        store._toasts.show({
          tone: 'error',
          title: 'The user could not be saved',
          detail: error.message,
          traceLine: traceLine(error),
        });
      } else {
        store._toasts.fromError(error);
      }

      // Only this request's own surface is closed. A create's 500 must not discard the
      // unsaved checkboxes of an edit panel the reader opened in the meantime.
      if (isOwnSurface) {
        patchState(store, { formMode: null, formTarget: null });
      }
    }

    const _loadPermissions = rxMethod<void>(
      // switchMap: reopening the form while the first load is on the wire replaces it,
      // and only the newest answer may fill the checklist.
      switchMap(() => {
        // `permissionsLoaded` is cleared alongside the error, so it means "*this* load
        // answered" rather than "a load answered once". A sticky flag would report a
        // catalog that is being retried as settled, and the users tab's permission filter
        // decides whether a `?permission=` names anything on exactly that answer.
        patchState(store, {
          permissionsLoading: true,
          permissionsLoaded: false,
          permissionsError: null,
        });
        // `permissions`, not `permissions/all`. The criterion names the latter, but that
        // route additionally returns *deactivated* permissions, and the server answers a
        // grant of one with a 404 — so offering it would be a checkbox that can only ever
        // fail. This route is already Administrator-gated and returns the active rows.
        const url = store._api.build('permissions');

        return store._http.get<PermissionDto[]>(url).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            // `permissionsLoaded` is set on both arms but **not** in `finalize`: a failure
            // settles the question as surely as rows do, while an unsubscribe — which is
            // what `takeUntil(signedOut$)` does — settles nothing. `finalize` runs then
            // too, and writing the flag there would land it *after* the sign-out reset,
            // leaving a cleared store claiming a catalog it no longer holds.
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
      // exhaustMap: a second submit while one is in flight is a double-click, not a
      // second user. `formBusy` disables the buttons; this closes the race under them —
      // and a duplicate that slipped through would take the "already exists" 400 the
      // first one had just made true.
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
            // Runs on success, failure and sign-out cancellation alike — the one path
            // that reliably releases the flag. It is not cleared in `cancelForm()`:
            // closing the form must not pretend a request still on the wire has finished.
            finalize: () => patchState(store, { formBusy: false }),
          }),
        );
      }),
    );

    const _update = rxMethod<{ target: UserDto; permissionIds: readonly string[] }>(
      // exhaustMap, as with create and for the same reason.
      exhaustMap(({ target, permissionIds }) => {
        patchState(store, { formBusy: true, ...clearFormErrors() });
        setPending(target.id, true);
        const url = store._api.build(`users/${ApiUrl.segment(target.id)}`);

        // The profile fields are echoed back unchanged. `UpdateUserActionDto` requires
        // all three to be non-empty, and frame `5c` edits permissions only — a body of
        // `{ permissionIds }` alone is a 400 on three required fields, not a partial
        // update. Nothing here consults Microsoft Graph, so an echo is also the only way
        // the profile survives the round trip.
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
              // The server's own DTO, carrying the permission set it actually stored
              // rather than the one that was asked for.
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
      // mergeMap: deactivations of different rows are independent and may overlap —
      // exhaustMap would silently drop the second. Same-id overlap cannot happen: the
      // row is gone and `beginDeactivate` guards on the pending id.
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
                // Frame `5d` is explicit that this renders at the control and never as a
                // generic failure toast: it is the one refusal the reader caused on
                // purpose and can only resolve by asking somebody else.
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

    /**
     * Loads the grantable permissions if they are not already held.
     *
     * Called when a form opens — or when a screen that filters by permission mounts —
     * rather than at construction: this store is root-scoped and most sessions never open
     * the administration area at all.
     */
    function ensurePermissions(): void {
      if (store.permissions().length > 0 || store.permissionsLoading()) {
        return;
      }

      _loadPermissions();
    }

    return {
      /** Whether one of this store's requests is in flight for a given user. */
      isRowPending(id: string): boolean {
        return store.pendingIds().has(id);
      },

      /**
       * Loads the grantable permissions unless they are already held or on the wire.
       *
       * Exposed for the users tab's permission filter (US-1206), which needs the catalog
       * to render its options and to tell a `?permission=` naming a real permission from
       * one naming nothing. Sharing this store's copy rather than fetching a second is
       * what keeps the filter and the edit panel from disagreeing about what exists.
       */
      ensurePermissions,

      /** Retries a failed permission load, from the checklist's own error line. */
      retryPermissions(): void {
        _loadPermissions();
      },

      /** Opens the create modal (frame `5b`). */
      beginCreate(): void {
        // `formBusy` is deliberately left alone: it tracks the request, not the form, and
        // a flight still occupying `exhaustMap` must stay visible.
        patchState(store, { formMode: 'create', formTarget: null, ...clearFormErrors() });
        ensurePermissions();
      },

      /**
       * Closes the form. Safe to call from every close path — Cancel, Escape, backdrop —
       * including redundantly after a submit already closed it.
       */
      cancelForm(): void {
        patchState(store, { formMode: null, formTarget: null, ...clearFormErrors() });
      },

      /**
       * Submits the create form.
       *
       * @param email The address, already trimmed by the form.
       * @param permissionIds The checked permissions. The server grants these **plus**
       *   every default, so an empty list still leaves the user able to sign in.
       */
      submitCreate(email: string, permissionIds: readonly string[]): void {
        if (store.formMode() !== 'create' || store.formBusy() || email === '') {
          return;
        }

        _create({ email, permissionIds });
      },

      /**
       * Opens the edit offcanvas on a row (frame `5c`), unless its own action is in flight.
       *
       * No fetch first, unlike `ProjectActionsStore.beginEdit`: `GET api/users` returns
       * the full `UserDto` including its active grants, so the row already carries
       * everything the panel needs and everything the PUT has to echo.
       */
      beginEdit(user: UserDto): void {
        if (store.pendingIds().has(user.id)) {
          return;
        }

        patchState(store, { formMode: 'edit', formTarget: user, ...clearFormErrors() });
        ensurePermissions();
      },

      /**
       * Saves the edited permission set.
       *
       * `permissionIds` is the **complete desired set**, not a delta — the server revokes
       * anything missing from it — which is what the panel's `--warn-bg` panel says out
       * loud before the administrator presses Save.
       */
      submitEdit(permissionIds: readonly string[]): void {
        const target = store.formTarget();
        if (store.formMode() !== 'edit' || target === null || store.formBusy()) {
          return;
        }

        _update({ target, permissionIds });
      },

      /** Opens the deactivate confirmation, unless the row's own action is in flight. */
      beginDeactivate(user: UserDto): void {
        if (store.pendingIds().has(user.id)) {
          return;
        }

        // Clearing the previous refusal here is what makes a retry meaningful: the
        // message is a standing fact about that row, so nothing else would ever remove it.
        patchState(store, { deactivateTarget: user, selfDeactivateRefusedId: null });
      },

      /** Closes the confirmation. Safe on every close path, confirm included. */
      cancelDeactivate(): void {
        patchState(store, { deactivateTarget: null });
      },

      /**
       * Forgets a standing refusal, for a directory screen that is starting fresh.
       *
       * This store outlives the route, and `role="alert"` fires when its region is
       * inserted — so without this, leaving the tab and coming back would re-announce a
       * refusal that did not just happen, as would the same row reappearing under a
       * different search.
       */
      clearSelfDeactivateRefusal(): void {
        patchState(store, { selfDeactivateRefusedId: null });
      },

      /**
       * Deactivates the open target.
       *
       * The modal closes at once and the request settles behind it. Removal of the row is
       * the caller's — the directory list is scoped to its route and `core/` may not
       * import from `features/` — so the undo travels back as a callback, exactly as
       * `ConversationActionsStore.deleteMany` does.
       *
       * @param onRollback Puts the optimistically removed row back, on any failure.
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
