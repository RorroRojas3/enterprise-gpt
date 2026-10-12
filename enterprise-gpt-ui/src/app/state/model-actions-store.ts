import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';
import { tapResponse } from '@ngrx/operators';
import { patchState, signalStore, withMethods, withProps, withState } from '@ngrx/signals';
import { Dispatcher } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { exhaustMap, mergeMap, takeUntil } from 'rxjs';
import { ModelDto, ModelWriteBody } from '@shared/models/api/chat-model.model';
import { ModelFormValue, toModelBody } from '@shared/utils/catalog/model-form';
import { ValidationAppError } from '@core/errors/app-error';
import { traceLine } from '@core/errors/error-message';
import { toAppError } from '@core/errors/to-app-error';
import { adminEvents } from '@core/events/admin-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url-service';
import { ToastStore } from '@core/notifications/toast-store';
import { addPendingId, removePendingId, withPendingIds } from '@core/state/with-pending-ids';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

export type ModelFormMode = 'create' | 'edit';

/** Carried with each request so a response never renders on a surface that replaced its own. */
type FormFlow = { readonly mode: 'create' } | { readonly mode: 'edit'; readonly id: string };

interface ModelActionsState {
  /** The form dialog is open when this is set. */
  readonly formMode: ModelFormMode | null;
  /** The model being edited. Null while creating one. */
  readonly formTarget: ModelDto | null;
  /**
   * Only a dialog submit is in flight: setting it blocks dismissal, which is why Set as default
   * has its own flow. It outlives a force-closed dialog, so `cancelForm()` leaves it alone.
   */
  readonly formBusy: boolean;
  /** The validation problem the server returned for the last attempt. */
  readonly formError: ValidationAppError | null;
  /**
   * A create's 404, which can only mean its provider is missing or deactivated; it carries no
   * `errors` dictionary. An edit's 404 is ambiguous, so it is toasted instead.
   */
  readonly formNotFound: string | null;
  /**
   * The raw value the server rejected, unparsed so `'0400000'` still differs from `400000`. Each
   * field shows {@link formError} only while unchanged from it; Signal Forms has no `setErrors`.
   */
  readonly formRejectedValue: ModelFormValue | null;
  /** The model the retire confirmation names. Null keeps it closed. */
  readonly deactivateTarget: ModelDto | null;
}

const initialState: ModelActionsState = {
  formMode: null,
  formTarget: null,
  formBusy: false,
  formError: null,
  formNotFound: null,
  formRejectedValue: null,
  deactivateTarget: null,
};

/**
 * Root-scoped so a request outlives the screen that started it; import it only from the admin
 * screens, since `check-initial-chunk.mjs` guards `{pages,components,state}/admin/`, not `state/`.
 * Validation renders inline; other failures toast without closing the dialog.
 */
export const ModelActionsStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  // Per-row in-flight tracking, so one row's kebab disables and not forty.
  withPendingIds(),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _toasts: inject(ToastStore),
    _dispatcher: inject(Dispatcher),
    _signedOut$: injectSignedOut(),
  })),
  withMethods((store) => {
    function clearFormErrors(): Partial<ModelActionsState> {
      return { formError: null, formNotFound: null, formRejectedValue: null };
    }

    /**
     * Create and edit share one dialog that can be force-closed mid-flight, so the flow names its
     * surface. Never closes the dialog: `retryInterceptor` won't retry a POST or PUT.
     */
    function rejectForm(cause: unknown, url: string, value: ModelFormValue, flow: FormFlow): void {
      const error = toAppError(cause, { url });
      // For an edit, the same row too: the dialog may have reopened on another model.
      const isOwnSurface =
        store.formMode() === flow.mode &&
        (flow.mode === 'create' || store.formTarget()?.id === flow.id);

      if (isOwnSurface && error.kind === 'validation-error') {
        patchState(store, { formError: error, formNotFound: null, formRejectedValue: value });

        return;
      }

      // A create's only `NotFoundException` is the provider check, so this 404 belongs on the
      // Provider field despite having no `errors` for `serverMessagesFor`.
      if (isOwnSurface && flow.mode === 'create' && error.kind === 'resource-not-found') {
        patchState(store, {
          formError: null,
          formNotFound: error.detail ?? error.message,
          formRejectedValue: value,
        });

        return;
      }

      if (error.kind === 'validation-error') {
        // `fromError` silences validation errors, assuming they render inline; here they can't.
        store._toasts.show({
          tone: 'error',
          title: 'The model could not be saved',
          detail: error.message,
          traceLine: traceLine(error),
        });

        return;
      }

      store._toasts.fromError(error);
    }

    const _create = rxMethod<{ body: ModelWriteBody; value: ModelFormValue }>(
      // exhaustMap: a second submit while one is in flight is a double-click, not a
      // second model. `formBusy` disables the buttons; this closes the race under them.
      exhaustMap(({ body, value }) => {
        patchState(store, { formBusy: true, ...clearFormErrors() });
        const url = store._api.build('models');

        return store._http.post<ModelDto>(url, body).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (created) => {
              store._dispatcher.dispatch(adminEvents.modelCatalogChanged());
              store._toasts.success(`${created.name} added`);
              patchState(store, { formMode: null, formTarget: null });
            },
            error: (cause: unknown) => rejectForm(cause, url, value, { mode: 'create' }),
            // The only path that always releases the flag, so `cancelForm()` leaves it alone.
            finalize: () => patchState(store, { formBusy: false }),
          }),
        );
      }),
    );

    const _update = rxMethod<{ target: ModelDto; body: ModelWriteBody; value: ModelFormValue }>(
      // Only the dialog's save reaches this, so a row action can't occupy it and swallow a submit.
      exhaustMap(({ target, body, value }) => {
        patchState(store, { formBusy: true, ...clearFormErrors() });
        patchState(store, addPendingId(target.id));
        const url = store._api.build(`models/${ApiUrl.segment(target.id)}`);

        return store._http.put<ModelDto>(url, body).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (updated) => {
              // Refetch, not patch: saving `isDefault` demotes another row, which this response
              // doesn't describe.
              store._dispatcher.dispatch(adminEvents.modelCatalogChanged());
              store._toasts.success(`${updated.name} updated`);
              patchState(store, { formMode: null, formTarget: null });
            },
            error: (cause: unknown) =>
              rejectForm(cause, url, value, { mode: 'edit', id: target.id }),
            finalize: () => {
              patchState(store, removePendingId(target.id), { formBusy: false });
            },
          }),
        );
      }),
    );

    const _setDefault = rxMethod<ModelDto>(
      // Separate from `_update` so it never sets `formBusy` (which would trap an open dialog)
      // and can `mergeMap` across rows; `setAsDefault` guards same-row overlap.
      mergeMap((target) => {
        patchState(store, addPendingId(target.id));
        const url = store._api.build(`models/${ApiUrl.segment(target.id)}`);

        // Echo every field: `ModelMapper` assigns all of them on a PUT, so omitting one clears
        // it, and could put the hidden summarizer back into every user's picker.
        const body: ModelWriteBody = {
          providerId: target.providerId,
          name: target.name,
          deploymentName: target.deploymentName,
          description: target.description,
          contextWindowSize: target.contextWindowSize,
          maxOutputTokens: target.maxOutputTokens,
          isToolEnabled: target.isToolEnabled,
          isReasoningEnabled: target.isReasoningEnabled,
          isUserSelectable: target.isUserSelectable,
          isDefault: true,
          inputPricePerMillionTokens: target.inputPricePerMillionTokens,
          outputPricePerMillionTokens: target.outputPricePerMillionTokens,
        };

        return store._http.put<ModelDto>(url, body).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (updated) => {
              store._dispatcher.dispatch(adminEvents.modelCatalogChanged());
              store._toasts.success(`${updated.name} is now the default`);
            },
            // No form produced this, so there is nowhere inline to render it.
            error: (cause: unknown) => store._toasts.fromError(toAppError(cause, { url })),
            finalize: () => patchState(store, removePendingId(target.id)),
          }),
        );
      }),
    );

    const _deactivate = rxMethod<ModelDto>(
      // mergeMap: retirements of different models may overlap; `beginDeactivate`'s pending-id
      // guard rules out the same one twice.
      mergeMap((target) => {
        patchState(store, addPendingId(target.id));
        const url = store._api.build(`models/${ApiUrl.segment(target.id)}`);

        return store._http.delete<void>(url).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: () => {
              store._toasts.success(`${target.name} retired`);
              // Refetch: retiring also clears `IsDefault`, so the catalog may now have no default.
              store._dispatcher.dispatch(adminEvents.modelCatalogChanged());
            },
            error: (cause: unknown) => store._toasts.fromError(toAppError(cause, { url })),
            finalize: () => patchState(store, removePendingId(target.id)),
          }),
        );
      }),
    );

    return {
      beginCreate(): void {
        // A dialog opened while `formBusy` would have no enabled field and no way out; reachable
        // after a force-close mid-flight, or by leaving and returning to the tab after a submit.
        if (store.formBusy()) {
          return;
        }

        patchState(store, { formMode: 'create', formTarget: null, ...clearFormErrors() });
      },

      /** Needs no fetch: the `api/models/all` row is the complete DTO. */
      beginEdit(model: ModelDto): void {
        // `formBusy` too: the dialog would open disabled with no way to dismiss it.
        if (store.formBusy() || store.isRowPending(model.id)) {
          return;
        }

        patchState(store, { formMode: 'edit', formTarget: model, ...clearFormErrors() });
      },

      /** Safe on every close path, including after a submit already closed the dialog. */
      cancelForm(): void {
        patchState(store, { formMode: null, formTarget: null, ...clearFormErrors() });
      },

      submitForm(value: ModelFormValue): void {
        const mode = store.formMode();
        if (mode === null || store.formBusy()) {
          return;
        }

        const body = toModelBody(value);
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

      /** The server demotes the previous default in the same transaction; the refetch shows it. */
      setAsDefault(model: ModelDto): void {
        if (model.isDefault || store.isRowPending(model.id)) {
          return;
        }

        _setDefault(model);
      },

      beginDeactivate(model: ModelDto): void {
        if (store.isRowPending(model.id)) {
          return;
        }

        patchState(store, { deactivateTarget: model });
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
