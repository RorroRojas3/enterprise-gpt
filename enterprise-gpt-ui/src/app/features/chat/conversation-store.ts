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
import { Events, withEventHandlers } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { EMPTY, pipe, switchMap, takeUntil, tap } from 'rxjs';
import { ConversationDetailDto, ConversationDto } from '@domain/api/conversation';
import { TurnSettingsStore } from '@core/chat/turn-settings-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { toAppError } from '@core/errors/to-app-error';
import { conversationEvents } from '@core/events/conversation-events';
import { projectEvents } from '@core/events/project-events';
import { injectSignedOut } from '@core/events/session-events';
import { turnEvents } from '@core/events/turn-events';
import { ApiUrl } from '@core/http/api-url';
import {
  setError,
  setFulfilled,
  setIdle,
  setPending,
  withRequestStatus,
} from '@core/state/with-request-status';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

interface ConversationState {
  /** The conversation the chat route is showing. Null on the empty `/chat` route. */
  readonly conversationId: string | null;
  readonly conversation: ConversationDetailDto | null;
}

const initialState: ConversationState = { conversationId: null, conversation: null };

/**
 * Reads `GET api/conversations/{id}`, not `/messages`, which reports `projectId`, `modelId` and
 * `isFavorite` as null/null/false whatever the truth and carries no `mcpServerIds`.
 */
export const ConversationStore = signalStore(
  withState(initialState),
  withRequestStatus(),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _list: inject(ConversationListStore),
    _settings: inject(TurnSettingsStore),
    _signedOut$: injectSignedOut(),
  })),
  withComputed((store) => ({
    isOpen: computed(() => store.conversationId() !== null),
    /** Falls back to the sidebar's copy while the detail loads, so the title shows at once. */
    name: computed(() => {
      const id = store.conversationId();
      if (id === null) {
        return '';
      }

      return store.conversation()?.name ?? store._list.entityMap()[id]?.name ?? '';
    }),
    /**
     * Detail preferred, as rename echoes its fresh `projectId`. Null for a deep link the list
     * never held, so the kebab waits a round trip rather than acting on a guess.
     */
    current: computed<ConversationDto | null>(() => {
      const id = store.conversationId();
      if (id === null) {
        return null;
      }

      return store.conversation() ?? store._list.entityMap()[id] ?? null;
    }),
    /**
     * The list's copy wins, unlike {@link current}: `favoriteRow` patches it optimistically, so
     * the star and the sidebar row flip together. The detail covers a deep link.
     */
    isFavorite: computed(() => {
      const id = store.conversationId();
      if (id === null) {
        return false;
      }

      return store._list.entityMap()[id]?.isFavorite ?? store.conversation()?.isFavorite ?? false;
    }),
    /** This conversation's own rename, favourite or delete is in flight. */
    actionPending: computed(() => {
      const id = store.conversationId();

      return id !== null && store._list.pendingIds().has(id);
    }),
  })),
  withMethods((store) => {
    function detailUrl(id: string): string {
      return store._api.build(`conversations/${ApiUrl.segment(id)}`);
    }

    const open = rxMethod<string | null | undefined>(
      pipe(
        tap((id) => {
          patchState(
            store,
            { conversationId: id ?? null, conversation: null },
            id === undefined || id === null ? setIdle() : setPending(),
          );

          if (id === undefined || id === null) {
            // No resumed-model warning on the empty screen; the selection itself persists.
            store._settings.clearRestoredModel();
          }
        }),
        // Null passes through switchMap rather than being filtered, so closing a conversation
        // cancels its request.
        switchMap((id) => {
          if (id === undefined || id === null) {
            return EMPTY;
          }

          const url = detailUrl(id);

          return store._http.get<ConversationDetailDto>(url).pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (conversation) => {
                patchState(store, { conversation }, setFulfilled());
                // Syncs the sidebar row with a name the server changed out of band.
                store._list.refreshRow(conversation);
                // Unvalidated: the settings store drops a model or server since gone. Empty means
                // the first turn has not finished, and seeding it would undo the user's picks.
                if (conversation.modelId !== null || conversation.mcpServerIds.length > 0) {
                  store._settings.applyConversationSettings({
                    modelId: conversation.modelId,
                    mcpServerIds: conversation.mcpServerIds,
                  });
                }
              },
              error: (cause: unknown) => patchState(store, setError(toAppError(cause, { url }))),
            }),
          );
        }),
      ),
    );

    /**
     * Unlike {@link open}, never clears the detail or goes pending, which would blank the header.
     * Failures are swallowed: what is on screen is still true.
     */
    const refreshDetail = rxMethod<string>(
      pipe(
        switchMap((id) => {
          const url = detailUrl(id);

          return store._http.get<ConversationDetailDto>(url).pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (conversation) => {
                // Unguarded: the sidebar gets the name even if the user has moved on.
                store._list.refreshRow(conversation);
                if (store.conversationId() === conversation.id) {
                  patchState(store, { conversation });
                }
              },
              // Swallowed in `tapResponse`: an escaping throw would kill the rxMethod for good.
              error: () => {},
            }),
          );
        }),
      ),
    );

    return {
      /** Points the screen at a conversation, or at the empty state when null. */
      open,

      /** Re-issues the detail request for whatever is open. The error panel's Retry. */
      retry(): void {
        open(store.conversationId());
      },

      /** @internal Exposed for the event handler below; not part of the screen's API. */
      _refreshDetail: refreshDetail,
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    // `name` prefers the detail copy, so a rename must patch it too, including on a deep link
    // the list never held.
    events.on(conversationEvents.updated).pipe(
      tap(({ payload }) => {
        const current = store.conversation();
        if (current !== null && current.id === payload.id) {
          // The spread keeps `mcpServerIds`, which `ConversationDto` does not carry.
          patchState(store, { conversation: { ...current, ...payload } });
        }
      }),
    ),

    // The server released the conversation to standalone; a stale `projectId` would make the
    // next rename PUT a deleted project and take a 404.
    events.on(projectEvents.deleted).pipe(
      tap(({ payload: projectId }) => {
        const current = store.conversation();
        if (current !== null && current.projectId === projectId) {
          patchState(store, { conversation: { ...current, projectId: null } });
        }
      }),
    ),

    // The server names a conversation after its first turn without saying so on the stream;
    // later turns cannot change the name.
    events.on(turnEvents.completed).pipe(
      tap(({ payload }) => {
        if (payload.wasFirstTurn && payload.conversationId === store.conversationId()) {
          store._refreshDetail(payload.conversationId);
        }
      }),
    ),
  ]),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
