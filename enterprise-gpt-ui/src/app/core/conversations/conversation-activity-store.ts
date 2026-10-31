import { HttpClient } from '@angular/common/http';
import { Signal, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { tapResponse } from '@ngrx/operators';
import {
  PartialStateUpdater,
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { Dispatcher, Events, withEventHandlers } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { mergeMap, takeUntil, tap } from 'rxjs';
import { ApiUrl } from '@core/api/api-url';
import { ConversationDto } from '@core/api/conversation.model';
import { DEFAULT_MAX_CONCURRENT_TURNS } from '@core/config/app-config';
import { APP_CONFIG } from '@core/config/app-config-token';
import { conversationEvents } from '@core/events/conversation-events';
import { injectSignedOut } from '@core/events/session-events';
import { turnEvents } from '@core/events/turn-events';
import { CHAT_ROUTE } from '@core/navigation/route-paths';
import { ToastStore } from '@core/notifications/toast-store';
import { withResetOnSignOut } from '@core/session/with-reset-on-sign-out';
import { ConversationActivity } from '@shared/badges/activity-indicator';
import { ConversationListStore, listFields } from './conversation-list-store';

/** What the chat feature's session registry reports. A port, as `core/` may not import it. */
export interface ConversationActivitySource {
  readonly running: Signal<ReadonlySet<string>>;
  /** Also counts a first prompt whose conversation has no id yet. */
  readonly runningCount: Signal<number>;
  /** Null on the blank chat route or off the chat screen. */
  readonly viewing: Signal<string | null>;
  stop(conversationId: string): void;
}

type Outcome = Exclude<ConversationActivity, 'running'>;

interface ActivityState {
  /** Endings the reader has not seen, by conversation id. */
  readonly unseen: Readonly<Record<string, Outcome>>;
}

const NOTHING_RUNNING: ReadonlySet<string> = new Set();

function marked(id: string, outcome: Outcome): PartialStateUpdater<ActivityState> {
  return ({ unseen }) => (unseen[id] === outcome ? {} : { unseen: { ...unseen, [id]: outcome } });
}

function unmarked(id: string): PartialStateUpdater<ActivityState> {
  return ({ unseen }) =>
    id in unseen
      ? { unseen: Object.fromEntries(Object.entries(unseen).filter(([key]) => key !== id)) }
      : {};
}

const answerToastKey = (id: string): string => `answer-ready:${id}`;

/**
 * What each conversation's turns are doing, and how they ended out of sight, for the sidebar and
 * navbar; also the concurrent-turn cap and a first turn's server-generated name.
 */
export const ConversationActivityStore = signalStore(
  { providedIn: 'root' },
  withState<ActivityState>({ unseen: {} }),
  withProps(() => ({
    _source: signal<ConversationActivitySource | null>(null),
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _dispatcher: inject(Dispatcher),
    _toasts: inject(ToastStore),
    _router: inject(Router),
    _list: inject(ConversationListStore),
    _signedOut$: injectSignedOut(),
    maxConcurrentTurns: inject(APP_CONFIG).chat?.maxConcurrentTurns ?? DEFAULT_MAX_CONCURRENT_TURNS,
  })),
  withComputed((store) => {
    const runningCount = computed(() => store._source()?.runningCount() ?? 0);
    const answers = computed(
      () => Object.values(store.unseen()).filter((outcome) => outcome === 'answer').length,
    );
    const interrupted = computed(() => Object.keys(store.unseen()).length - answers());

    return {
      _running: computed(() => store._source()?.running() ?? NOTHING_RUNNING),
      /** The one mark a collapsed control shows: a turn in flight outranks an unseen ending. */
      summary: computed<ConversationActivity | null>(() => {
        if (runningCount() > 0) {
          return 'running';
        }

        if (answers() > 0) {
          return 'answer';
        }

        return interrupted() > 0 ? 'interrupted' : null;
      }),
      /** Appended to a control's accessible name, e.g. " — 2 generating, 1 new answer". */
      summarySuffix: computed(() => {
        const parts = [
          runningCount() > 0 ? `${runningCount()} generating` : '',
          answers() > 0 ? `${answers()} new answer${answers() > 1 ? 's' : ''}` : '',
          interrupted() > 0 ? `${interrupted()} interrupted` : '',
        ].filter((part) => part !== '');

        return parts.length === 0 ? '' : ` — ${parts.join(', ')}`;
      }),
      atCapacity: computed(() => runningCount() >= store.maxConcurrentTurns),
    };
  }),
  withMethods((store) => {
    function stillUnseen(id: string): boolean {
      return store.unseen()[id] === 'answer';
    }

    function announce(id: string, name: string | null): void {
      store._toasts.show({
        key: answerToastKey(id),
        tone: 'info',
        title: 'Answer ready',
        detail: name,
        action: { label: 'Open', run: () => void store._router.navigate([CHAT_ROUTE, id]) },
      });
    }

    const refreshName = rxMethod<{ id: string; announce: boolean }>(
      mergeMap(({ id, announce: toast }) =>
        store._http
          .get<ConversationDto>(store._api.build(`conversations/${ApiUrl.segment(id)}`))
          .pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (conversation) => {
                store._list.refreshRow(conversation);
                store._dispatcher.dispatch(conversationEvents.updated(listFields(conversation)));
                if (toast && stillUnseen(id)) {
                  announce(id, conversation.name);
                }
              },
              // The answer is still ready; it just goes unnamed.
              error: () => {
                if (toast && stillUnseen(id)) {
                  announce(id, null);
                }
              },
            }),
          ),
      ),
    );

    return {
      connect(source: ConversationActivitySource): void {
        store._source.set(source);
      },

      activityOf(id: string): ConversationActivity | null {
        return store._running().has(id) ? 'running' : (store.unseen()[id] ?? null);
      },

      markSeen(id: string): void {
        store._toasts.dismissKey(answerToastKey(id));
        patchState(store, unmarked(id));
      },

      stop(id: string): void {
        store._source()?.stop(id);
      },

      _ended(id: string, outcome: Outcome, wasFirstTurn: boolean): void {
        const onScreen = store._source()?.viewing() === id;
        if (!onScreen) {
          patchState(store, marked(id, outcome));
        }

        if (outcome !== 'answer') {
          return;
        }

        // The server names a conversation on its first turn without saying so on the stream.
        const held = store._list.entityMap()[id];
        if (wasFirstTurn || (held === undefined && !onScreen)) {
          refreshName({ id, announce: !onScreen });
        } else if (held !== undefined && !onScreen) {
          announce(id, held.name);
        }
      },
    };
  }),
  withEventHandlers((store, events = inject(Events)) => [
    events
      .on(turnEvents.completed)
      .pipe(
        tap(({ payload }) => store._ended(payload.conversationId, 'answer', payload.wasFirstTurn)),
      ),
    // No toast: the row's icon already says it.
    events
      .on(turnEvents.interrupted)
      .pipe(tap(({ payload }) => store._ended(payload.conversationId, 'interrupted', false))),
    events.on(conversationEvents.deleted).pipe(tap(({ payload: id }) => store.markSeen(id))),
  ]),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
