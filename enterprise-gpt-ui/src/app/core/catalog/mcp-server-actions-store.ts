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

/** What the MCP server form is doing, which decides its heading and its verb. */
export type McpServerFormMode = 'create' | 'edit';

/**
 * Which flow a failed request came from.
 *
 * Carried through the request rather than read back off the store, so a response cannot
 * be rendered on a surface that replaced the one it belongs to — the defect
 * `UserActionsStore.rejectForm` documents in three variants.
 */
type FormFlow = { readonly mode: 'create' } | { readonly mode: 'edit'; readonly id: string };

/**
 * Frame `5h`'s `--fail` panel: a save that failed, named and explained.
 *
 * A structure rather than a rendered string so the template can bold the server's name,
 * which is the part of the criterion that makes the panel worth having — a reader who
 * opened one dialog, was interrupted and came back needs to know *which* save failed.
 */
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
   * **A submit from the dialog is in flight** — and only ever that.
   *
   * The dialog reads this to disable its fields and both footer buttons, and hands it to
   * `Modal.busy`, which suppresses Escape and backdrop dismissal. Anything else setting
   * it would trap the reader inside a dialog they opened, waiting on a request that is
   * not theirs (WCAG 2.1.2) — the rule `ModelActionsStore` states in full.
   *
   * In-flight tracking, not dialog state — a form force-closed mid-flight (a second
   * Escape is uncancelable in Chromium) leaves this true until the request settles, so a
   * reopened form honestly reports that `exhaustMap` is still occupied rather than
   * swallowing the next submit silently. `cancelForm()` deliberately does not clear it.
   */
  readonly formBusy: boolean;
  /** The validation problem the server returned for the last attempt. */
  readonly formError: ValidationAppError | null;
  /**
   * The panel at the top of the dialog, set for **every** rejection kept on this surface
   * — a validation problem included.
   *
   * This is the one place `ModelActionsStore`'s shape is not copied, and the acceptance
   * criterion is why: it asks for a `--fail` panel that names the server on a validation
   * failure *and* on a transport-level one. A non-validation failure therefore renders
   * here instead of raising a toast, so the same fact is not reported twice; it falls
   * back to a toast only once its surface is no longer the one on screen.
   */
  readonly formFailure: McpServerSaveFailure | null;
  /**
   * The exact form values the server rejected. Each field shows {@link formError}'s
   * messages only while it still holds the value that was sent, which is what makes an
   * inline message clear itself on the next edit **of the field it belongs to** — Signal
   * Forms has no `setErrors`, so an imperative reset would have nothing to reset.
   *
   * The whole value rather than one field's, because the server may fault more than one,
   * and the raw form value rather than the body built from it, so the comparison is
   * exact: the body has been trimmed, and a scope the body dropped is still in the field.
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
 * The register, edit and deactivate flows the MCP server tab shares (US-1208).
 *
 * Root-scoped for the reason `UserActionsStore` and `ModelActionsStore` are: `rxMethod`
 * binds its subscription to the injector that created it, and every one of these requests
 * can outlive the screen that started it — an administrator who confirms a deactivation
 * and then clicks another admin tab would otherwise have the `DELETE` torn down
 * mid-flight, with nothing deleted and nothing said.
 *
 * **It must stay reachable only from the admin chunk.** `providedIn: 'root'` is a
 * dependency-injection scope, not a chunk assignment, so this file rides the lazy admin
 * bundle exactly as long as nothing eagerly reachable imports it — and the initial graph
 * has a fraction of a kilobyte of headroom. `check-initial-chunk.mjs` guards
 * `features/admin/`; this file is in `core/`, so the only thing keeping it out is that
 * `features/admin/mcps/` is its sole importer.
 *
 * The boundaries are the ones the user and model stores document:
 *
 * - **Entity writes travel as events** (`adminEvents.mcpCatalogChanged`), never as
 *   `patchState` into another store's protected state. Both observers — the route-scoped
 *   `AdminMcpsStore` and the root `McpCatalogStore` the composer's Tools menu reads —
 *   refetch.
 * - **No failure closes the dialog**, because five fields including a 1024-character
 *   description are behind it and `retryInterceptor` retries neither a POST nor a PUT.
 * - **Events carry server-confirmed facts only.**
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

    /** Frame `5h`'s panel copy: which server, and what the server said about it. */
    function toFailure(error: AppError, serverName: string): McpServerSaveFailure {
      if (error.kind === 'validation-error') {
        return {
          serverName,
          // "The server rejected the URL." when the keys name a field this form shows,
          // and the generic line when they do not — a panel must never claim a field was
          // rejected that the reader cannot find.
          reason: describeRejectedFields(Object.keys(error.errors)) ?? userMessage(error),
          // The fields carry the detail; a trace id beside them is noise.
          traceLine: null,
        };
      }

      return { serverName, reason: userMessage(error), traceLine: traceLine(error) };
    }

    /**
     * Renders a rejection on the surface that produced it, and as a toast when that
     * surface is no longer the one on screen.
     *
     * **The flow identifies itself; nothing is inferred from what is open.** Create and
     * edit share one dialog and one store, and it can be force-closed mid-flight, so by
     * the time a response lands the *other* mode may be up — or a different row.
     *
     * **Nothing here closes the dialog.**
     */
    function rejectForm(
      cause: unknown,
      url: string,
      value: McpServerFormValue,
      flow: FormFlow,
      serverName: string,
    ): void {
      const error = toAppError(cause, { url });
      // Still the same surface, and — for an edit — still the same row: reopening the
      // dialog on another server while a save is in flight is the same mistake.
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
        // `fromError` silences validation problems on the assumption they render inline;
        // with no inline to render on there is nothing to say them, so this one is raised
        // by hand — the same deliberate exception the user and model dialogs make.
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
      // exhaustMap: a second submit while one is in flight is a double-click, not a
      // second server — and a duplicate that slipped through would take the "name already
      // in use" 400 the first one had just made true. `formBusy` disables the buttons;
      // this closes the race under them.
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
            // Runs on success, failure and sign-out cancellation alike — the one path
            // that reliably releases the flag. It is not cleared in `cancelForm()`.
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
              // Void, and the list refetches: a rename also renames the server's linked
              // permission, which nothing in this response describes.
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
      // mergeMap: deactivations of different servers are independent and may overlap —
      // exhaustMap would silently drop the second. Same-id overlap cannot happen:
      // `beginDeactivate` guards on the pending id.
      mergeMap((target) => {
        patchState(store, addPendingId(target.id));
        const url = store._api.build(`mcps/${ApiUrl.segment(target.id)}`);

        return store._http.delete<void>(url).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: () => {
              store._toasts.success(`${target.name} deactivated`);
              // The same event as a save. `DeactivateMcpServerAsync` also deactivates the
              // linked permission and every grant of it, none of which a 204 can carry.
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
      /** Opens the add-server dialog (frame `5h`), unless a submit is still on the wire. */
      beginCreate(): void {
        // The `formBusy` guard is not politeness. That flag drives the schema's `disabled`
        // rule, both footer buttons *and* `Modal`'s Escape and backdrop handling, so a
        // dialog opened while it is true would contain no focusable field and no way out
        // until an unrelated request settled.
        if (store.formBusy()) {
          return;
        }

        patchState(store, { formMode: 'create', formTarget: null, ...clearFormErrors() });
      },

      /**
       * Opens the dialog on a server, unless its own request is in flight.
       *
       * No fetch first, unlike `ProjectActionsStore.beginEdit`: `GET api/mcps/all` returns
       * the complete `McpServerDto`, so the row already carries every field the PUT has to
       * send back. `GET api/mcps/{id}` exists and is deliberately unused — it would answer
       * 404 for a deactivated server, and a second path to the same data with a different
       * failure mode is what the user tab's §13 note refuses.
       */
      beginEdit(server: McpServerDto): void {
        if (store.formBusy() || store.isRowPending(server.id)) {
          return;
        }

        patchState(store, { formMode: 'edit', formTarget: server, ...clearFormErrors() });
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
       * @param value The dialog's fields. `toMcpServerBody` refuses anything the wire
       *   cannot take, so an unparsable auth type cannot reach the request as `NaN` and a
       *   scope the form has hidden cannot reach it at all.
       */
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

      /** Opens the deactivate confirmation, unless the server's request is in flight. */
      beginDeactivate(server: McpServerDto): void {
        if (store.isRowPending(server.id)) {
          return;
        }

        patchState(store, { deactivateTarget: server });
      },

      /** Closes the confirmation. Safe on every close path, confirm included. */
      cancelDeactivate(): void {
        patchState(store, { deactivateTarget: null });
      },

      /**
       * Deactivates the open target.
       *
       * The modal closes at once and the request settles behind it. **Nothing is removed
       * optimistically**, unlike a user deactivation: this is a soft delete the table goes
       * on rendering, muted, once the refetch lands — so there is no rollback to thread
       * back to the screen and this confirms on the store rather than emitting.
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
