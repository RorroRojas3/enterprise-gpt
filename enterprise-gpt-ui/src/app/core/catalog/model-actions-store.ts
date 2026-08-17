import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withProps, withState } from '@ngrx/signals';
import { Dispatcher } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { exhaustMap, mergeMap, takeUntil } from 'rxjs';
import { ModelDto, ModelWriteBody } from '@domain/api/model';
import { ValidationAppError } from '@core/errors/app-error';
import { traceLine } from '@core/errors/error-message';
import { toAppError } from '@core/errors/to-app-error';
import { adminEvents } from '@core/events/admin-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import { ToastStore } from '@core/notifications/toast-store';
import { addPendingId, removePendingId, withPendingIds } from '@core/state/with-pending-ids';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';
import { ModelFormValue, toModelBody } from './model-form';

/** What the model form is doing, which decides its heading and its verb. */
export type ModelFormMode = 'create' | 'edit';

/**
 * Which flow a failed request came from.
 *
 * Carried through the request rather than read back off the store, so a response cannot
 * be rendered on a surface that replaced the one it belongs to — the defect
 * `UserActionsStore.rejectForm` documents in three variants.
 */
type FormFlow = { readonly mode: 'create' } | { readonly mode: 'edit'; readonly id: string };

interface ModelActionsState {
  /** The form dialog is open when this is set. */
  readonly formMode: ModelFormMode | null;
  /** The model being edited. Null while creating one. */
  readonly formTarget: ModelDto | null;
  /**
   * **A submit from the dialog is in flight** — and only ever that.
   *
   * The dialog reads this to disable its fields and both footer buttons, and hands it to
   * `Modal.busy`, which suppresses Escape and backdrop dismissal. So anything that sets
   * it makes the dialog undismissable for the length of a request, which is why the
   * kebab's Set as default has a flow of its own and never touches it: it would trap the
   * reader inside a dialog they opened, waiting on a request that is not theirs.
   *
   * In-flight tracking, not dialog state — a dialog force-closed mid-flight (a second
   * Escape is uncancelable in Chromium) leaves this true until the request settles, so a
   * reopened dialog honestly reports that `exhaustMap` is still occupied rather than
   * swallowing the next submit silently. `cancelForm()` deliberately does not clear it.
   */
  readonly formBusy: boolean;
  /** The validation problem the server returned for the last attempt. */
  readonly formError: ValidationAppError | null;
  /**
   * The 404 a **create** takes when its provider is missing or deactivated, which is not
   * a validation problem and carries no `errors` dictionary.
   *
   * `CreateModelAsync` throws exactly one `NotFoundException`, from
   * `EnsureProviderExistsAsync`, so on that flow a 404 can only be about the provider —
   * and it is held apart from {@link formError} because it reaches the same field by a
   * different route. An edit's 404 is ambiguous (the model may also have been retired by
   * somebody else), so that one is toasted rather than guessed at.
   */
  readonly formNotFound: string | null;
  /**
   * The exact form values the server rejected. Each field shows {@link formError}'s
   * messages only while it still holds the value that was sent, which is what makes an
   * inline message clear itself on the next edit **of the field it belongs to** — Signal
   * Forms has no `setErrors`, so an imperative reset would have nothing to reset.
   *
   * The whole value rather than one field's, because unlike the user and project forms
   * this one has eight rejectable fields and the server may fault more than one. The raw
   * form value rather than the body built from it, so the comparison is exact: the body
   * has been trimmed and its numbers parsed, and `'0400000'` would not match the
   * `400000` the wire carried.
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
 * The create, edit, set-as-default and retire flows the model catalog shares (US-1207).
 *
 * Root-scoped for the reason `UserActionsStore` is: `rxMethod` binds its subscription to
 * the injector that created it, and every one of these requests can outlive the screen
 * that started it — an administrator who confirms a retirement and then clicks another
 * admin tab would otherwise have the `DELETE` torn down mid-flight, with nothing deleted
 * and nothing said.
 *
 * **It must stay reachable only from the admin chunk.** `providedIn: 'root'` is a
 * dependency-injection scope, not a chunk assignment, so this file rides the lazy admin
 * bundle exactly as long as nothing eagerly reachable imports it — and the initial graph
 * has well under a kilobyte of headroom. `check-initial-chunk.mjs` guards
 * `features/admin/`; this file is in `core/`, so the only thing keeping it out is that
 * `features/admin/models/` is its sole importer.
 *
 * The boundaries are the ones the user and project stores document:
 *
 * - **Entity writes travel as events** (`adminEvents.modelCatalogChanged`), never as
 *   `patchState` into another store's protected state. Both observers — the route-scoped
 *   `AdminModelsStore` and the root `ModelCatalogStore` the chat picker reads — refetch.
 * - **Validation problems render inline** on the open form and are never toasted;
 *   everything else goes through `ToastStore.fromError` **without closing the dialog**.
 * - **Events carry server-confirmed facts only.**
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
     * Renders a rejection inline on the surface that produced it, and as a toast when
     * that surface is no longer the one on screen.
     *
     * **The flow identifies itself; nothing is inferred from what is open.** Create and
     * edit share one dialog and one store, and it can be force-closed mid-flight, so by
     * the time a response lands the *other* mode may be up — or a different row.
     *
     * **Nothing here closes the dialog.** Eleven fields, one of them a 1024-character
     * description, are behind it; discarding them because a 502 came back would cost the
     * reader every one of them and `retryInterceptor` does not retry a POST or a PUT. The
     * failure is said, the values stay, and the reader decides whether to try again.
     */
    function rejectForm(cause: unknown, url: string, value: ModelFormValue, flow: FormFlow): void {
      const error = toAppError(cause, { url });
      // Still the same surface, and — for an edit — still the same row: reopening the
      // dialog on somebody else's model while a save is in flight is the same mistake.
      const isOwnSurface =
        store.formMode() === flow.mode &&
        (flow.mode === 'create' || store.formTarget()?.id === flow.id);

      if (isOwnSurface && error.kind === 'validation-error') {
        patchState(store, { formError: error, formNotFound: null, formRejectedValue: value });

        return;
      }

      // A 404 on create is the *provider*: `CreateModelAsync` throws exactly one
      // `NotFoundException`, and it is `EnsureProviderExistsAsync`'s — which fires for a
      // provider a deployment has deactivated since this build's list was written. It
      // carries no `errors` dictionary, so it cannot travel the `serverMessagesFor` path,
      // but it is about the Provider field just the same. An edit's 404 is ambiguous, so
      // it falls through to the toast rather than being guessed at.
      if (isOwnSurface && flow.mode === 'create' && error.kind === 'resource-not-found') {
        patchState(store, {
          formError: null,
          formNotFound: error.detail ?? error.message,
          formRejectedValue: value,
        });

        return;
      }

      if (error.kind === 'validation-error') {
        // `fromError` silences validation problems on the assumption they render inline;
        // with no inline to render on there is nothing to say them, so this one is raised
        // by hand — the same deliberate exception the user and project dialogs make.
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
            // Runs on success, failure and sign-out cancellation alike — the one path
            // that reliably releases the flag. It is not cleared in `cancelForm()`:
            // closing the dialog must not pretend a request still on the wire has
            // finished.
            finalize: () => patchState(store, { formBusy: false }),
          }),
        );
      }),
    );

    const _update = rxMethod<{ target: ModelDto; body: ModelWriteBody; value: ModelFormValue }>(
      // exhaustMap, as with create and for the same reason. Only the dialog's own save
      // reaches this — the kebab's Set as default has `_setDefault` — so a row action can
      // never occupy it and silently swallow the reader's submit.
      exhaustMap(({ target, body, value }) => {
        patchState(store, { formBusy: true, ...clearFormErrors() });
        patchState(store, addPendingId(target.id));
        const url = store._api.build(`models/${ApiUrl.segment(target.id)}`);

        return store._http.put<ModelDto>(url, body).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (updated) => {
              // Void, and the list refetches. Saving a model with `isDefault` demotes
              // whichever row held it before, and this response describes only the model
              // it saved — so patching it in place would leave two rows marked default.
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
      // Its own flow rather than `_update`'s, for two reasons that both matter. It must
      // not set `formBusy`, which would make a dialog the reader opens meanwhile
      // undismissable — the fields, both buttons and `Modal`'s own Escape handling all
      // hang off that flag. And `mergeMap` rather than `exhaustMap`: promotions of
      // different rows are independent, exactly as retirements are, while sharing
      // `_update`'s `exhaustMap` would have silently dropped the reader's own save.
      // Same-row overlap cannot happen: `setAsDefault` guards on the pending id.
      mergeMap((target) => {
        patchState(store, addPendingId(target.id));
        const url = store._api.build(`models/${ApiUrl.segment(target.id)}`);

        // The row's own ten other fields, echoed unchanged. `ModelMapper` assigns every
        // property on a PUT, so a body carrying only `isDefault` would clear the prices
        // and read `isReasoningEnabled` as false.
        const body: ModelWriteBody = {
          providerId: target.providerId,
          name: target.name,
          deploymentName: target.deploymentName,
          description: target.description,
          contextWindowSize: target.contextWindowSize,
          maxOutputTokens: target.maxOutputTokens,
          isToolEnabled: target.isToolEnabled,
          isReasoningEnabled: target.isReasoningEnabled,
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
      // mergeMap: retirements of different models are independent and may overlap —
      // exhaustMap would silently drop the second. Same-id overlap cannot happen:
      // `beginDeactivate` guards on the pending id.
      mergeMap((target) => {
        patchState(store, addPendingId(target.id));
        const url = store._api.build(`models/${ApiUrl.segment(target.id)}`);

        return store._http.delete<void>(url).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: () => {
              store._toasts.success(`${target.name} retired`);
              // The same event as a save, for a reason no payload could carry:
              // `DeactivateModelAsync` also clears `IsDefault` on the row it retires, so
              // the catalog this deletes from may now have no default at all.
              store._dispatcher.dispatch(adminEvents.modelCatalogChanged());
            },
            error: (cause: unknown) => store._toasts.fromError(toAppError(cause, { url })),
            finalize: () => patchState(store, removePendingId(target.id)),
          }),
        );
      }),
    );

    return {
      /** Opens the add-model dialog (frame `5f`), unless a submit is still on the wire. */
      beginCreate(): void {
        // The `formBusy` guard is not politeness. That flag drives the schema's `disabled`
        // rule, both footer buttons *and* `Modal`'s Escape and backdrop handling, so a
        // dialog opened while it is true would contain no focusable element and no way
        // out until an unrelated request settled — the same trap the row flows were split
        // out to avoid, reached from the other side. Two routes get here: a dialog
        // force-closed mid-flight (a second Escape is uncancelable in Chromium), and
        // `AdminModels`'s constructor calling `cancelForm()` when the reader submits,
        // presses Back, and returns to the tab.
        //
        // The flag itself is deliberately left alone — it tracks the request, not the
        // dialog, and a flight still occupying `exhaustMap` must stay visible.
        if (store.formBusy()) {
          return;
        }

        patchState(store, { formMode: 'create', formTarget: null, ...clearFormErrors() });
      },

      /**
       * Opens the dialog on a model, unless its own request is in flight.
       *
       * No fetch first, unlike `ProjectActionsStore.beginEdit`: `GET api/models/all`
       * returns the complete `ModelDto`, so the row already carries every one of the
       * eleven fields the PUT has to send back.
       */
      beginEdit(model: ModelDto): void {
        // `formBusy` as well as the row's own request, for the reason `beginCreate`
        // records: the dialog would open disabled end to end with no way to dismiss it.
        if (store.formBusy() || store.isRowPending(model.id)) {
          return;
        }

        patchState(store, { formMode: 'edit', formTarget: model, ...clearFormErrors() });
      },

      /**
       * Closes the dialog. Safe to call from every close path — Cancel, Escape, backdrop
       * — including redundantly after a submit already closed it.
       */
      cancelForm(): void {
        patchState(store, { formMode: null, formTarget: null, ...clearFormErrors() });
      },

      /**
       * Submits the open form.
       *
       * @param value The dialog's fields. `toModelBody` refuses anything the wire cannot
       *   take, so an unusable numeric field cannot reach the request as `NaN`.
       */
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

      /**
       * Makes a model the deployment default, from the row's kebab (frame `5e`).
       *
       * The server demotes the previous default in the same transaction; the refetch the
       * event triggers is what shows it.
       */
      setAsDefault(model: ModelDto): void {
        if (model.isDefault || store.isRowPending(model.id)) {
          return;
        }

        _setDefault(model);
      },

      /** Opens the retire confirmation, unless the model's own request is in flight. */
      beginDeactivate(model: ModelDto): void {
        if (store.isRowPending(model.id)) {
          return;
        }

        patchState(store, { deactivateTarget: model });
      },

      /** Closes the confirmation. Safe on every close path, confirm included. */
      cancelDeactivate(): void {
        patchState(store, { deactivateTarget: null });
      },

      /**
       * Retires the open target.
       *
       * The modal closes at once and the request settles behind it. **Nothing is removed
       * optimistically**, unlike a user deactivation: this is a soft delete the table
       * goes on rendering, so the row does not leave — it turns muted when the refetch
       * lands, and there is no rollback to thread back.
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
