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
   * Tracks the request, not the dialog: a dialog force-closed mid-flight leaves this true, so a
   * reopened one shows `exhaustMap` is still occupied instead of swallowing the next submit.
   */
  readonly renameBusy: boolean;
  /** The validation problem the server returned for the last rename attempt. */
  readonly renameError: ValidationAppError | null;
  /**
   * The name the server rejected. {@link renameError} shows only while the field still holds it,
   * so the error clears on the next edit; Signal Forms has no `setErrors`.
   */
  readonly renameRejectedName: string | null;
  /** The conversation the delete confirmation names. Null keeps it closed. */
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
 * Conversation flows shared by `features/shell` and `features/chat`, which must not import each
 * other; the dialogs mount once in the shell. Row writes go through `ConversationListStore`
 * methods, and the route-scoped `ConversationStore` hears outcomes as `conversationEvents`.
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
     * Re-exported so a surface that only invokes these flows, like the library table, never
     * touches the sidebar's list. Populated by id whether or not that list holds the row.
     */
    pendingIds: computed(() => _list.pendingIds()),
  })),
  withMethods((store) => {
    const _rename = rxMethod<{ target: ConversationDto; name: string }>(
      // exhaustMap: a second submit in flight is a double-click; this closes the race under
      // the buttons `renameBusy` disables.
      exhaustMap(({ target, name }) => {
        // In the projection, not a leading `tap`, so an emission exhaustMap drops leaves no
        // unresolved optimistic patch.
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

              // Inline only while the dialog is up; after a cancel there is no field, so it
              // falls back to a toast rather than being dropped.
              if (error.kind === 'validation-error' && store.renameTarget() !== null) {
                patchState(store, { renameError: error, renameRejectedName: name });
              } else {
                if (error.kind === 'validation-error') {
                  // `fromError` silences validation problems, assuming they render inline,
                  // so with the dialog closed this one is raised by hand.
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
            // The one path run on success, failure and sign-out alike, so busy clears only
            // here: closing the dialog must not end a request still on the wire.
            finalize: () => {
              store._list.setRowPending(target.id, false);
              patchState(store, { renameBusy: false });
            },
          }),
        );
      }),
    );

    const _delete = rxMethod<ConversationDto>(
      // mergeMap: deletes of different conversations are independent; exhaustMap would drop
      // the second. Same-id overlap is impossible: beginDelete guards on the pending id.
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
              // Only on the 204: leaving a deleted open conversation is never optimistic.
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
      // mergeMap, as with the single delete. Ids cannot overlap: the caller drops them from
      // its selection as the first batch goes out.
      mergeMap(({ ids, onRollback }) => {
        for (const id of ids) {
          store._list.setRowPending(id, true);
        }

        // Null for rows the 50-item sidebar does not hold; nothing to restore there.
        const removals = ids.map((id) => store._list.removeRow(id)).filter((r) => r !== null);
        const url = store._api.build('conversations/bulk');

        // `HttpClient.delete` sends a body only via options, and the endpoint binds `[FromBody]`;
        // without it the server sees no ids and answers 400.
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
              // LIFO: `removeRow` captures each index against the already-shrunk list, so
              // only a reverse restore is its inverse.
              for (const removal of [...removals].reverse()) {
                store._list.restoreRow(removal);
              }
              onRollback();
              // One toast for the batch, not one per row.
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
      // mergeMap: favourites of different conversations are independent. Same-id overlap is
      // impossible: toggleFavorite refuses while the row is pending.
      mergeMap(({ target, isFavorite }) => {
        // In the projection, which rxMethod runs synchronously, so toggleFavorite's guard sees
        // the pending id on a double-click's second call.
        store._list.setRowPending(target.id, true);
        store._list.favoriteRow(target.id, isFavorite);
        const url = store._api.build(`conversations/${ApiUrl.segment(target.id)}/favorite`);

        // A SET, not a toggle: the body carries the state being asked for, so a
        // retried or duplicated request cannot land on the opposite value.
        return store._http.put<void>(url, { isFavorite }).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: () => {
              // Re-asserted: the 204 has no DTO, and a detail load (`refreshRow`) or search
              // (`setAllEntities`) landing mid-flight writes the pre-PUT flag back.
              store._list.favoriteRow(target.id, isFavorite);
              store._dispatcher.dispatch(conversationEvents.updated({ ...target, isFavorite }));
            },
            // No validation arm: the endpoint has no validator, so every failure rolls back.
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
      // mergeMap: moves of different conversations are independent. Same-id overlap is
      // impossible: moveToProject refuses while the row is pending.
      mergeMap(({ target, projectId }) => {
        // In the projection so the guard sees the pending id on a double-click's second call.
        store._list.setRowPending(target.id, true);
        store._list.moveRow(target.id, projectId);
        const url = store._api.build('conversations');

        // Verbatim, never through `??`: only an explicit null unlinks, and `undefined` would
        // echo the current project and make "remove from project" a silent no-op.
        return store._http.put<ConversationDto>(url, toUpdateBody(target, { projectId })).pipe(
          takeUntil(store._signedOut$),
          tapResponse({
            next: (updated) => {
              // The server's DTO: a move bumps `dateModified`, which only the response carries.
              store._list.refreshRow(updated);
              store._dispatcher.dispatch(conversationEvents.updated(updated));
            },
            // No validation arm: the name is echoed from an accepted conversation, so a 400 is
            // unreachable; a 404 rolls back and toasts like any other failure.
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

      /** Safe from every close path, including redundantly after a submit already closed it. */
      cancelRename(): void {
        patchState(store, {
          renameTarget: null,
          renameError: null,
          renameRejectedName: null,
        });
      },

      /**
       * Renames optimistically, rolling back on failure. An unchanged name just closes and an
       * empty one is ignored; the dialog disables submit for both, and this guard enforces it.
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
       * Closes the modal and removes the row at once, restoring it on failure. The API always
       * answers 204, so the only failures are transport and 5xx.
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
       * Root-scoped so the batch survives the library route being left mid-flight. The caller
       * undoes its own optimistic removal via `onRollback`; the server answers 204 even for
       * missing or foreign ids, so there is no partial outcome.
       */
      deleteMany(ids: readonly string[], onRollback: () => void): void {
        if (ids.length === 0) {
          return;
        }

        _deleteMany({ ids, onRollback });
      },

      /**
       * The pending-id guard is the whole re-entry story: a double-click's second call no-ops
       * rather than racing an opposite SET against the first.
       */
      toggleFavorite(conversation: ConversationDto): void {
        if (store._list.isRowPending(conversation.id)) {
          return;
        }

        _favorite({ target: conversation, isFavorite: !conversation.isFavorite });
      },

      /**
       * A null `projectId` unlinks. `PUT api/conversations` is a full representation, so a move
       * between projects is one write; the pending-id guard stops a second call racing it.
       */
      moveToProject(conversation: ConversationDto, projectId: string | null): void {
        if (store._list.isRowPending(conversation.id)) {
          return;
        }

        // The picker marks the current project selected, so this is a re-click, not a move.
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
