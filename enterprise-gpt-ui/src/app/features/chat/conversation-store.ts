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
 * The conversation currently open on the chat route.
 *
 * Provided by `Chat` rather than `providedIn: 'root'`, so its lifetime is the chat
 * screen's. Thanks to `chatMatcher` that lifetime spans `/chat` and
 * `/chat/{id}` without a remount, which is what lets US-401 create a conversation
 * mid-turn without losing anything the user has typed.
 *
 * It reads `GET api/conversations/{id}`, deliberately **not**
 * `GET api/conversations/{id}/messages`: the latter is built from the Cosmos document
 * and reports `projectId`, `modelId` and `isFavorite` as null/null/false whatever the
 * truth, so it cannot hydrate a picker or a favourite star. This is also the only
 * source of `mcpServerIds`, which US-410 restores into the MCP picker.
 *
 * It is also where the model and MCP selections a conversation last used are
 * seeded back into `TurnSettingsStore` (US-410), and where the name the server
 * generates after a first turn is picked up (US-409).
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
    /**
     * The name to put in the header.
     *
     * Falls back to the sidebar's copy while the detail request is in flight, so
     * opening a conversation from the list shows its title immediately rather than
     * flashing a placeholder for the length of a round trip. Empty only for a deep
     * link to a row the list has not loaded.
     */
    name: computed(() => {
      const id = store.conversationId();
      if (id === null) {
        return '';
      }

      return store.conversation()?.name ?? store._list.entityMap()[id]?.name ?? '';
    }),
    /**
     * The DTO the header's actions operate on (US-308). Detail preferred — its
     * `projectId` is the fresh one the rename must echo — with the sidebar's copy
     * while the detail request is in flight. Null for a deep link the list never
     * held: the kebab waits one round trip rather than acting on a guess.
     */
    current: computed<ConversationDto | null>(() => {
      const id = store.conversationId();
      if (id === null) {
        return null;
      }

      return store.conversation() ?? store._list.entityMap()[id] ?? null;
    }),
    /**
     * Whether the open conversation is favourited, for the header star (US-305).
     *
     * The list's copy wins over the detail one — the reverse of {@link current} —
     * because it is the copy `favoriteRow` patches optimistically, so the star and the
     * sidebar row flip together on the click rather than a round trip apart, which is
     * US-308's deferred criterion. A deep link the list never held falls back to the
     * detail DTO, whose `isFavorite` is trustworthy (unlike the one
     * `GET api/conversations/{id}/messages` reports) and simply lags a toggle by the
     * round trip until `conversationEvents.updated` lands.
     */
    isFavorite: computed(() => {
      const id = store.conversationId();
      if (id === null) {
        return false;
      }

      return store._list.entityMap()[id]?.isFavorite ?? store.conversation()?.isFavorite ?? false;
    }),
    /**
     * This conversation's own rename, favourite or delete is in flight; the header
     * kebab's items and its star disable for the duration. Reads `pendingIds()`
     * directly so the dependency is explicit to the computed.
     */
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
            // Nothing is resumed on the empty screen, so no warning about a
            // resumed model belongs there (US-410). The selection itself stays:
            // it persists across conversations for the session (US-402).
            store._settings.clearRestoredModel();
          }
        }),
        // The null case is handled *inside* switchMap rather than filtered out ahead
        // of it: filtering would leave a request for the conversation just closed
        // still running, because switchMap would never see the value that cancels it.
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
                // Keeps the sidebar row in step with a name the server changed out of
                // band. A conversation the list does not hold is ignored there.
                store._list.refreshRow(conversation);
                // US-410: the next turn behaves like the last one. Seeded from
                // the detail response because it is the only shape carrying
                // `mcpServerIds`, and unvalidated because the settings store's
                // own joins drop a model or server that has since gone.
                //
                // An empty payload is not a selection of "nothing" — the server
                // writes `modelId` and the MCP set from the newest usage row, so
                // a conversation whose first turn has not finished reports both
                // empty. Seeding it would clear the model and tool servers the
                // user picked for the turn creating it right now (US-401
                // re-opens this store as its `replaceUrl` lands), which is US-402
                // and US-403 undone by a round trip.
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
     * Re-reads the open conversation without disturbing the screen (US-409).
     *
     * Separate from {@link open} because that one clears `conversation` and
     * goes pending, which would blank the header mid-conversation and flash the
     * sidebar's stale name back. Failure is swallowed for the same reason:
     * everything on screen is still true, and a name that arrives one refresh
     * late is not worth an error panel over a conversation the user is reading.
     */
    const refreshDetail = rxMethod<string>(
      pipe(
        switchMap((id) => {
          const url = detailUrl(id);

          return store._http.get<ConversationDetailDto>(url).pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (conversation) => {
                // Unguarded: the row is keyed by id, so the sidebar earns the
                // generated name even if the user moved on mid-round-trip —
                // which is the half of US-409 they will still be looking at.
                store._list.refreshRow(conversation);
                if (store.conversationId() === conversation.id) {
                  patchState(store, { conversation });
                }
              },
              // Swallowed, and through `tapResponse` rather than a `catchError`
              // ahead of the handler: a throw escaping here would kill the
              // rxMethod, and every later first turn would silently stop
              // refreshing its name.
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
    // A rename confirmed by the server (US-304) lands here even when the sidebar
    // list does not hold the row — the deep-link case `refreshRow` ignores. Without
    // it the header would keep showing the old name, because `name` prefers the
    // detail copy this store holds.
    events.on(conversationEvents.updated).pipe(
      tap(({ payload }) => {
        const current = store.conversation();
        if (current !== null && current.id === payload.id) {
          // The spread keeps `mcpServerIds`, which `ConversationDto` does not carry.
          patchState(store, { conversation: { ...current, ...payload } });
        }
      }),
    ),

    // US-409: the server names a conversation after its first turn, and says so
    // nowhere on the stream — a refetch is the only way to learn it. Bounded to
    // that one turn, because every later turn would refetch a name that cannot
    // have changed.
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
