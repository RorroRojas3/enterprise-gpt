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
import { exhaustMap, mergeMap, takeUntil } from 'rxjs';
import { ConversationDto } from '@domain/api/conversation';
import { ValidationAppError } from '@core/errors/app-error';
import { traceLine } from '@core/errors/error-message';
import { toAppError } from '@core/errors/to-app-error';
import { conversationEvents } from '@core/events/conversation-events';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import { ToastStore } from '@core/notifications/toast-store';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';
import { ConversationListStore } from './conversation-list-store';
import { toUpdateBody } from './conversation-update';

interface ConversationActionsState {
  /** The conversation the rename dialog is editing. Null keeps the dialog closed. */
  readonly renameTarget: ConversationDto | null;
  /**
   * A rename request is in flight: the dialog blocks dismissal and disables its
   * buttons. In-flight tracking, not dialog state — a dialog force-closed mid-flight
   * (a second Escape is uncancelable in Chromium) leaves this true until the request
   * settles, so a reopened dialog honestly reports that `exhaustMap` is still
   * occupied instead of swallowing the next submit silently.
   */
  readonly renameBusy: boolean;
  /** The validation problem the server returned for the last rename attempt. */
  readonly renameError: ValidationAppError | null;
  /**
   * The exact name the server rejected. The dialog shows {@link renameError}'s
   * messages only while the field still holds this value, which is what makes the
   * inline error clear itself on the user's next edit without an imperative reset —
   * Signal Forms has no `setErrors` to clear.
   */
  readonly renameRejectedName: string | null;
  /** The conversation the delete confirmation names (US-306). Null keeps it closed. */
  readonly deleteTarget: ConversationDto | null;
}

const initialState: ConversationActionsState = {
  renameTarget: null,
  renameBusy: false,
  renameError: null,
  renameRejectedName: null,
  deleteTarget: null,
};

/**
 * The rename, favourite and delete flows every conversation surface shares.
 *
 * Root-scoped on purpose: the invokers live in two features that must not import each
 * other — the sidebar row kebab (`features/shell`) and the conversation header kebab
 * and star (`features/chat`) — while the dialogs themselves are mounted once in the
 * shell, which wraps both. Everyone talks to this store; nobody talks to a dialog.
 *
 * Four boundaries shape what lives here:
 *
 * - **Entity writes go through `ConversationListStore` methods** (`renameRow`,
 *   `setRowPending`, `refreshRow`): its state is protected, so this store cannot
 *   `patchState` it — and should not, because how a row is patched is the list's
 *   decision.
 * - **The route-scoped `ConversationStore` is reached by event, not by method.**
 *   A root store cannot inject a route-scoped one, so server-confirmed outcomes are
 *   dispatched as `conversationEvents` and the chat surface subscribes.
 * - **Failures follow the `core/errors` conventions**: validation problems render
 *   inline in the dialog and are never toasted; anything else rolls back the
 *   optimistic patch and goes through `ToastStore.fromError`.
 * - **`conversationEvents.updated` carries the server's own DTO**, with one deliberate
 *   exception: the favourite endpoint answers 204 with no body, so that event is built
 *   from the target and the flag the server just accepted. An idempotent SET confirms
 *   exactly that and changes nothing else, so nothing is being guessed at.
 */
export const ConversationActionsStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _list: inject(ConversationListStore),
    _toasts: inject(ToastStore),
    _dispatcher: inject(Dispatcher),
    _signedOut$: injectSignedOut(),
  })),
  withComputed(({ _list }) => ({
    /**
     * The rows with one of this store's requests in flight.
     *
     * Re-exported rather than having callers reach into `_list`: a surface that only
     * *invokes* these flows — the conversations library's table, which holds rows the
     * 50-item sidebar may not — has no other business with the sidebar's list, and
     * the set is populated by id whether or not that list holds the row.
     */
    pendingIds: computed(() => _list.pendingIds()),
  })),
  withMethods((store) => {
    const _rename = rxMethod<{ target: ConversationDto; name: string }>(
      // exhaustMap: a second submit while one is in flight is a double-click, not a
      // second rename. `renameBusy` disables the buttons; this closes the race under
      // them.
      exhaustMap(({ target, name }) => {
        // Inside the projection rather than a leading `tap`, so an emission
        // exhaustMap drops leaves no optimistic patch behind with no request to
        // resolve it.
        patchState(store, { renameBusy: true, renameError: null, renameRejectedName: null });
        store._list.setRowPending(target.id, true);
        store._list.renameRow(target.id, name);
        const url = store._api.build('conversations');

        return store._http.put<ConversationDto>(url, toUpdateBody(target, { name })).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (updated) => {
              // Adopt the server's DTO — its `dateModified`, not a local guess.
              store._list.refreshRow(updated);
              store._dispatcher.dispatch(conversationEvents.updated(updated));
              patchState(store, { renameTarget: null });
            },
            error: (cause: unknown) => {
              const error = toAppError(cause, { url });
              store._list.renameRow(target.id, target.name);

              // Inline only while the dialog is still up: a validation problem
              // landing after the user cancelled has no field to render under, so it
              // degrades to the toast path rather than being dropped.
              if (error.kind === 'validation-error' && store.renameTarget() !== null) {
                patchState(store, { renameError: error, renameRejectedName: name });
              } else {
                if (error.kind === 'validation-error') {
                  // `fromError` silences validation problems on the assumption they
                  // render inline; with the dialog force-closed there is no inline,
                  // so this one is raised by hand — the single deliberate exception
                  // to "fromError is the only place an AppError becomes a toast".
                  store._toasts.show({
                    tone: 'error',
                    title: 'The rename was rejected',
                    detail: Object.values(error.errors).flat().join(' ') || error.message,
                    traceLine: traceLine(error),
                  });
                } else {
                  store._toasts.fromError(error);
                }
                patchState(store, { renameTarget: null });
              }
            },
            // Runs on success, failure and sign-out cancellation alike — the one
            // path that reliably releases the row and the busy flag. Busy is not
            // cleared anywhere else: `cancelRename` closing the dialog must not
            // pretend a request still on the wire is finished.
            finalize: () => {
              store._list.setRowPending(target.id, false);
              patchState(store, { renameBusy: false });
            },
          }),
        );
      }),
    );

    const _delete = rxMethod<ConversationDto>(
      // mergeMap: deletes of different conversations are independent and may overlap
      // — exhaustMap would silently drop the second. Same-id overlap cannot happen:
      // the row and its dialog are gone, and beginDelete guards on the pending id.
      mergeMap((target) => {
        // Pending before removeRow, so the header kebab stays disabled for the whole
        // flight even though the sidebar row disappears immediately.
        store._list.setRowPending(target.id, true);
        // Null when the list does not hold the row (a deep link beyond the first
        // page); the DELETE still goes out and there is nothing to restore.
        const removal = store._list.removeRow(target.id);
        const url = store._api.build(`conversations/${ApiUrl.segment(target.id)}`);

        return store._http.delete<void>(url).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: () => {
              store._toasts.success('Conversation deleted');
              // Only on the 204: navigating away from a deleted open conversation
              // happens when the delete completes, never optimistically.
              store._dispatcher.dispatch(conversationEvents.deleted(target.id));
            },
            error: (cause: unknown) => {
              if (removal !== null) {
                store._list.restoreRow(removal);
              }
              store._toasts.fromError(toAppError(cause, { url }));
            },
            finalize: () => store._list.setRowPending(target.id, false),
          }),
        );
      }),
    );

    const _deleteMany = rxMethod<{ ids: readonly string[]; onRollback: () => void }>(
      // mergeMap, as with the single delete: two batches are two independent sets of
      // rows. Overlap on an id cannot happen — the caller's rows are out of its
      // selection the moment the first batch goes out.
      mergeMap(({ ids, onRollback }) => {
        for (const id of ids) {
          store._list.setRowPending(id, true);
        }

        // Null for rows the 50-item sidebar does not hold; the DELETE still goes out
        // and there is nothing to restore there.
        const removals = ids.map((id) => store._list.removeRow(id)).filter((r) => r !== null);
        const url = store._api.build('conversations/bulk');

        // `HttpClient.delete` drops a body unless it goes through the options object,
        // and the endpoint binds `[FromBody]` — without this the server sees an empty
        // id list and answers 400.
        return store._http.delete<void>(url, { body: { conversationIds: [...ids] } }).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: () => {
              store._toasts.success(
                ids.length === 1 ? 'Conversation deleted' : `${ids.length} conversations deleted`,
              );

              for (const id of ids) {
                store._dispatcher.dispatch(conversationEvents.deleted(id));
              }
            },
            error: (cause: unknown) => {
              // Reversed, because `removeRow` captures each index against the list as
              // it stands at that moment — so the removals shrink it one at a time and
              // only a LIFO restore is their inverse. Front-to-back would splice each
              // row against a list that has already grown, turning [A, B, C] minus
              // [A, B] back into [B, A, C].
              for (const removal of [...removals].reverse()) {
                store._list.restoreRow(removal);
              }
              onRollback();
              // One toast for the batch, not one per row — a failed delete of eight
              // rows is one thing that went wrong.
              store._toasts.fromError(toAppError(cause, { url }));
            },
            finalize: () => {
              for (const id of ids) {
                store._list.setRowPending(id, false);
              }
            },
          }),
        );
      }),
    );

    const _favorite = rxMethod<{ target: ConversationDto; isFavorite: boolean }>(
      // mergeMap: favourites of different conversations are independent and may
      // overlap, exactly as with delete. Same-id overlap cannot happen — toggleFavorite
      // refuses while the row's pending id is set.
      mergeMap(({ target, isFavorite }) => {
        // Both writes sit in the projection, which rxMethod runs synchronously with the
        // call: that is what lets toggleFavorite's guard see the pending id on a
        // double-click's second call, and it keeps the optimistic patch inseparable
        // from the request that resolves it.
        store._list.setRowPending(target.id, true);
        store._list.favoriteRow(target.id, isFavorite);
        const url = store._api.build(`conversations/${ApiUrl.segment(target.id)}/favorite`);

        // A SET, not a toggle: the body carries the state being asked for, so a
        // retried or duplicated request cannot land on the opposite value.
        return store._http.put<void>(url, { isFavorite }).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: () => {
              // Re-asserted rather than refreshed: the 204 carries no DTO to adopt, so
              // this is the same flag again — idempotent when nothing intervened, and
              // the repair when something did. The optimistic patch is not safe on its
              // own, because the detail request `ConversationStore` fires on open ends
              // in `refreshRow`, and a search or Retry ends in `setAllEntities`; either
              // one landing mid-flight writes the server's pre-`PUT` `isFavorite` back
              // over the row, and without this the list and the detail copy would
              // disagree for good — leaving the header star, which reads the list,
              // showing the opposite of what the server holds.
              store._list.favoriteRow(target.id, isFavorite);
              store._dispatcher.dispatch(conversationEvents.updated({ ...target, isFavorite }));
            },
            // No validation arm — the endpoint has no validator, so every failure,
            // a 404 for a foreign or deleted conversation included, is roll back and
            // toast.
            error: (cause: unknown) => {
              store._list.favoriteRow(target.id, target.isFavorite);
              store._toasts.fromError(toAppError(cause, { url }));
            },
            finalize: () => store._list.setRowPending(target.id, false),
          }),
        );
      }),
    );

    const _move = rxMethod<{ target: ConversationDto; projectId: string | null }>(
      // mergeMap, as with favourite and delete: moves of different conversations are
      // independent. Same-id overlap cannot happen — moveToProject refuses while the
      // row's pending id is set.
      mergeMap(({ target, projectId }) => {
        // In the projection, for the reason favourite's writes are: the guard must see
        // the pending id on a double-click's second call.
        store._list.setRowPending(target.id, true);
        store._list.moveRow(target.id, projectId);
        const url = store._api.build('conversations');

        // `projectId` is handed to the builder verbatim, never through `??`: only an
        // explicit null unlinks, and an `undefined` slipping in here would echo the
        // current project and turn "remove from project" into a silent no-op.
        return store._http.put<ConversationDto>(url, toUpdateBody(target, { projectId })).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (updated) => {
              // The server's DTO, not the local guess: a move bumps `dateModified`,
              // which only the response carries.
              store._list.refreshRow(updated);
              store._dispatcher.dispatch(conversationEvents.updated(updated));
            },
            // No validation arm. The name is echoed unchanged from a conversation the
            // server already accepted, so a 400 is not reachable through this path;
            // a 404 for a deleted conversation or a project that is gone rolls back
            // and toasts like any other failure.
            error: (cause: unknown) => {
              store._list.moveRow(target.id, target.projectId);
              store._toasts.fromError(toAppError(cause, { url }));
            },
            finalize: () => store._list.setRowPending(target.id, false),
          }),
        );
      }),
    );

    return {
      /** Whether one of this store's requests is in flight for a given row. */
      isRowPending(id: string): boolean {
        return store._list.isRowPending(id);
      },

      /** Opens the rename dialog for a conversation, unless its own action is in flight. */
      beginRename(conversation: ConversationDto): void {
        if (store._list.isRowPending(conversation.id)) {
          return;
        }

        // `renameBusy` is deliberately left alone: it tracks the request, not the
        // dialog, and a flight still occupying `exhaustMap` must stay visible.
        patchState(store, {
          renameTarget: conversation,
          renameError: null,
          renameRejectedName: null,
        });
      },

      /**
       * Closes the rename dialog. Safe to call from every close path — Cancel,
       * Escape, backdrop — including redundantly after a submit already closed it.
       */
      cancelRename(): void {
        patchState(store, {
          renameTarget: null,
          renameError: null,
          renameRejectedName: null,
        });
      },

      /**
       * Renames the open target to `rawName`, trimmed.
       *
       * The sidebar row updates optimistically and rolls back on failure. An
       * unchanged name is a close, not a request; an empty one is ignored — the
       * dialog disables its submit for both, and this guard is what makes that
       * unbypassable.
       */
      submitRename(rawName: string): void {
        const target = store.renameTarget();
        if (target === null || store.renameBusy()) {
          return;
        }

        const name = rawName.trim();
        if (name === '') {
          return;
        }

        if (name === target.name) {
          patchState(store, { renameTarget: null, renameError: null, renameRejectedName: null });
          return;
        }

        _rename({ target, name });
      },

      /** Opens the delete confirmation, unless the row's own action is in flight. */
      beginDelete(conversation: ConversationDto): void {
        if (store._list.isRowPending(conversation.id)) {
          return;
        }

        patchState(store, { deleteTarget: conversation });
      },

      /** Closes the delete confirmation. Safe on every close path, confirm included. */
      cancelDelete(): void {
        patchState(store, { deleteTarget: null });
      },

      /**
       * Deletes the open target. The modal closes and the row disappears at once —
       * removal is optimistic, per the acceptance criteria — while the request
       * settles in the background: a 204 raises the success toast and announces the
       * deletion, a failure restores the row and raises an error toast. The API
       * always answers 204 (idempotent, never a 404), so the only failures here are
       * transport and 5xx.
       */
      confirmDelete(): void {
        const target = store.deleteTarget();
        if (target === null) {
          return;
        }

        patchState(store, { deleteTarget: null });
        _delete(target);
      },

      /**
       * Deletes several conversations in one request (US-704).
       *
       * Root-scoped for the same reason the single delete is: the invoking surface is
       * the conversations library, whose store dies with its route. An `rxMethod`
       * declared there would have its subscription torn down by that injector, and a
       * reader who confirmed a delete and then clicked a sidebar row would get no
       * deletion, no toast and no explanation.
       *
       * The sidebar's rows are removed here; `onRollback` is how the *caller's* own
       * optimistic removal is undone, because a store in `core/` cannot inject a
       * route-scoped one. The server filters ids that are missing, foreign or already
       * deleted and answers 204 either way, so there is no partial outcome to model.
       */
      deleteMany(ids: readonly string[], onRollback: () => void): void {
        if (ids.length === 0) {
          return;
        }

        _deleteMany({ ids, onRollback });
      },

      /**
       * Flips a conversation's favourite flag (US-305). The sidebar row and the chat
       * header both update at once, and a failure rolls them back behind an error
       * toast carrying the trace id.
       *
       * With no dialog in front of it, this guard is the whole re-entry story: a
       * double-click's second call finds the pending id the first one set and no-ops,
       * rather than sending an opposite SET that would race the first to decide the
       * final state.
       */
      toggleFavorite(conversation: ConversationDto): void {
        if (store._list.isRowPending(conversation.id)) {
          return;
        }

        _favorite({ target: conversation, isFavorite: !conversation.isFavorite });
      },

      /**
       * Moves a conversation into a project, between projects, or out of every project
       * when `projectId` is null (US-307).
       *
       * One request covers all three: `PUT api/conversations` is a full representation,
       * so leaving project A for project B is a single write of the new id rather than
       * an unlink followed by a link. The row updates optimistically and rolls back
       * behind an error toast.
       *
       * Like favourite, this opens nothing, so the pending-id guard is the whole
       * re-entry story: a second call while the first is in flight would race it to
       * decide which project the conversation ends up in.
       */
      moveToProject(conversation: ConversationDto, projectId: string | null): void {
        if (store._list.isRowPending(conversation.id)) {
          return;
        }

        // A move to the project it is already in is not a request. The picker marks the
        // current project as selected, so this is a re-click, not a change.
        if (conversation.projectId === projectId) {
          return;
        }

        _move({ target: conversation, projectId });
      },
    };
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
