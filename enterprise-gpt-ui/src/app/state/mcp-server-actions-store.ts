import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withProps, withState } from '@ngrx/signals';
import { Dispatcher } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { exhaustMap, mergeMap, takeUntil } from 'rxjs';
import { McpServerDto, McpServerWriteBody } from '@domain/api/mcp';
import { AppError, ValidationAppError } from '@core/errors/app-error';
import { traceLine, userMessage } from '@core/errors/error-message';
import { toAppError } from '@core/errors/to-app-error';
import { adminEvents } from '@core/events/admin-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import { ToastStore } from '@core/notifications/toast-store';
import { addPendingId, removePendingId, withPendingIds } from '@core/state/with-pending-ids';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';
import { McpServerFormValue, describeRejectedFields, toMcpServerBody } from './mcp-server-form';

export type McpServerFormMode = 'create' | 'edit';

/** Carried with each request so a response never renders on a surface that replaced its own. */
type FormFlow = { readonly mode: 'create' } | { readonly mode: 'edit'; readonly id: string };

/** A failed save, kept structured so the template can bold which server it was about. */
export interface McpServerSaveFailure {
  /** The server the failed request was about, never the row that is on screen now. */
  readonly serverName: string;
  /** One sentence saying what went wrong, ending in a full stop. */
  readonly reason: string;
  /** Absent for a validation problem, where the fields themselves carry the detail. */
  readonly traceLine: string | null;
}

interface McpServerActionsState {
  /** The form dialog is open when this is set. */
  readonly formMode: McpServerFormMode | null;
  /** The server being edited. Null while registering one. */
  readonly formTarget: McpServerDto | null;
  /**
   * Only a dialog submit is in flight; anything else setting it would trap the reader in a modal
   * (WCAG 2.1.2). It outlives a force-closed form, so `cancelForm()` leaves it alone.
   */
  readonly formBusy: boolean;
  /** The validation problem the server returned for the last attempt. */
  readonly formError: ValidationAppError | null;
  /**
   * The dialog's top panel, set for every rejection kept here, validation included; other
   * failures show here instead of a toast until their surface is gone.
   */
  readonly formFailure: McpServerSaveFailure | null;
  /**
   * The raw value the server rejected (the body is trimmed). Each field shows {@link formError}
   * only while unchanged from it, since Signal Forms has no `setErrors`.
   */
  readonly formRejectedValue: McpServerFormValue | null;
  /** The server the deactivate confirmation names. Null keeps it closed. */
  readonly deactivateTarget: McpServerDto | null;
}

const initialState: McpServerActionsState = {
  formMode: null,
  formTarget: null,
  formBusy: false,
  formError: null,
  formFailure: null,
  formRejectedValue: null,
  deactivateTarget: null,
};

/**
 * Root-scoped so a request outlives the screen that started it; import it only from
 * `features/admin/mcps/`, since `check-initial-chunk.mjs` guards `features/admin/`, not `core/`.
 * No failure closes the dialog: `retryInterceptor` retries neither POST nor PUT.
 */
export const McpServerActionsStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  // Per-row in-flight tracking, so one row's kebab disables and not twenty.
  withPendingIds(),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _toasts: inject(ToastStore),
    _dispatcher: inject(Dispatcher),
    _signedOut$: injectSignedOut(),
  })),
  withMethods((store) => {
    function clearFormErrors(): Partial<McpServerActionsState> {
      return { formError: null, formFailure: null, formRejectedValue: null };
    }

    function toFailure(error: AppError, serverName: string): McpServerSaveFailure {
      if (error.kind === 'validation-error') {
        return {
          serverName,
          // Generic copy unless the keys name a field this form shows: never claim a rejected
          // field the reader cannot find.
          reason: describeRejectedFields(Object.keys(error.errors)) ?? userMessage(error),
          // The fields carry the detail; a trace id beside them is noise.
          traceLine: null,
        };
      }

      return { serverName, reason: userMessage(error), traceLine: traceLine(error) };
    }

    /**
     * Create and edit share one dialog that can be force-closed mid-flight, so the flow names its
     * surface and a stale one falls back to a toast. Never closes the dialog.
     */
    function rejectForm(
      cause: unknown,
      url: string,
      value: McpServerFormValue,
      flow: FormFlow,
      serverName: string,
    ): void {
      const error = toAppError(cause, { url });
      // For an edit, the same row too: the dialog may have reopened on another server.
      const isOwnSurface =
        store.formMode() === flow.mode &&
        (flow.mode === 'create' || store.formTarget()?.id === flow.id);

      if (isOwnSurface) {
        patchState(store, {
          formError: error.kind === 'validation-error' ? error : null,
          formFailure: toFailure(error, serverName),
          formRejectedValue: value,
        });

        return;
      }

      if (error.kind === 'validation-error') {
        // `fromError` silences validation errors, assuming they render inline; here they can't.
        store._toasts.show({
          tone: 'error',
          title: `${serverName} could not be saved`,
          detail: error.message,
          traceLine: traceLine(error),
        });

        return;
      }

      store._toasts.fromError(error);
    }

    const _create = rxMethod<{ body: McpServerWriteBody; value: McpServerFormValue }>(
      // exhaustMap: a second submit in flight is a double-click, which would otherwise hit the
      // "name already in use" 400 the first just caused. Closes the race under `formBusy`.
      exhaustMap(({ body, value }) => {
        patchState(store, { formBusy: true, ...clearFormErrors() });
        const url = store._api.build('mcps');

        return store._http.post<McpServerDto>(url, body).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (created) => {
              store._dispatcher.dispatch(adminEvents.mcpCatalogChanged());
              store._toasts.success(`${created.name} registered`);
              patchState(store, { formMode: null, formTarget: null });
            },
            error: (cause: unknown) => rejectForm(cause, url, value, { mode: 'create' }, body.name),
            // The only path that always releases the flag, so `cancelForm()` leaves it alone.
            finalize: () => patchState(store, { formBusy: false }),
          }),
        );
      }),
    );

    const _update = rxMethod<{
      target: McpServerDto;
      body: McpServerWriteBody;
      value: McpServerFormValue;
    }>(
      exhaustMap(({ target, body, value }) => {
        patchState(store, { formBusy: true, ...clearFormErrors() });
        patchState(store, addPendingId(target.id));
        const url = store._api.build(`mcps/${ApiUrl.segment(target.id)}`);

        return store._http.put<McpServerDto>(url, body).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (updated) => {
              // Refetch, not patch: a rename also renames the linked permission, which this
              // response doesn't describe.
              store._dispatcher.dispatch(adminEvents.mcpCatalogChanged());
              store._toasts.success(`${updated.name} updated`);
              patchState(store, { formMode: null, formTarget: null });
            },
            // The target's name, not the body's: a save that renamed the server and
            // failed should still name the row the reader opened.
            error: (cause: unknown) =>
              rejectForm(cause, url, value, { mode: 'edit', id: target.id }, target.name),
            finalize: () => {
              patchState(store, removePendingId(target.id), { formBusy: false });
            },
          }),
        );
      }),
    );

    const _deactivate = rxMethod<McpServerDto>(
      // mergeMap: deactivations of different servers may overlap; `beginDeactivate`'s
      // pending-id guard rules out the same one twice.
      mergeMap((target) => {
        patchState(store, addPendingId(target.id));
        const url = store._api.build(`mcps/${ApiUrl.segment(target.id)}`);

        return store._http.delete<void>(url).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: () => {
              store._toasts.success(`${target.name} deactivated`);
              // Refetch: deactivation also revokes the linked permission and its grants, which
              // a 204 can't carry.
              store._dispatcher.dispatch(adminEvents.mcpCatalogChanged());
            },
            // No form produced this, so there is nowhere inline to put it.
            error: (cause: unknown) => store._toasts.fromError(toAppError(cause, { url })),
            finalize: () => patchState(store, removePendingId(target.id)),
          }),
        );
      }),
    );

    return {
      beginCreate(): void {
        // A dialog opened while `formBusy` would have no enabled field and no way out.
        if (store.formBusy()) {
          return;
        }

        patchState(store, { formMode: 'create', formTarget: null, ...clearFormErrors() });
      },

      /**
       * Needs no fetch: the `api/mcps/all` row is the complete DTO. `GET api/mcps/{id}` is
       * deliberately unused, as it 404s for a deactivated server.
       */
      beginEdit(server: McpServerDto): void {
        if (store.formBusy() || store.isRowPending(server.id)) {
          return;
        }

        patchState(store, { formMode: 'edit', formTarget: server, ...clearFormErrors() });
      },

      /** Safe on every close path, including after a submit already closed the dialog. */
      cancelForm(): void {
        patchState(store, { formMode: null, formTarget: null, ...clearFormErrors() });
      },

      submitForm(value: McpServerFormValue): void {
        const mode = store.formMode();
        if (mode === null || store.formBusy()) {
          return;
        }

        const body = toMcpServerBody(value);
        if (body === null) {
          return;
        }

        if (mode === 'create') {
          _create({ body, value });

          return;
        }

        const target = store.formTarget();
        if (target === null) {
          return;
        }

        _update({ target, body, value });
      },

      beginDeactivate(server: McpServerDto): void {
        if (store.isRowPending(server.id)) {
          return;
        }

        patchState(store, { deactivateTarget: server });
      },

      /** Safe on every close path, confirm included. */
      cancelDeactivate(): void {
        patchState(store, { deactivateTarget: null });
      },

      /**
       * Not optimistic, unlike a user deactivation: the table keeps the soft-deleted row, muted,
       * after the refetch, so there is nothing to roll back.
       */
      confirmDeactivate(): void {
        const target = store.deactivateTarget();
        if (target === null) {
          return;
        }

        patchState(store, { deactivateTarget: null });
        _deactivate(target);
      },
    };
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
