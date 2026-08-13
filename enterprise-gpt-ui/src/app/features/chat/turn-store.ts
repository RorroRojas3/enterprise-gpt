import { HttpClient } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import {
  PartialStateUpdater,
  patchState,
  signalMethod,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import {
  EMPTY,
  Subject,
  catchError,
  exhaustMap,
  map,
  merge,
  of,
  switchMap,
  takeUntil,
  tap,
} from 'rxjs';
import { ConversationDto } from '@domain/api/conversation';
import {
  AssistantStatusSnapshot,
  AssistantUiEvent,
  createInitialSnapshot,
  foldAssistantEvents,
} from '@domain/stream/andes/assistant-ui.contract';
import { TurnTimeline, createInitialTimeline, foldTimeline } from '@domain/stream/turn-timeline';
import { TurnSettingsStore, TurnSelection } from '@core/chat/turn-settings-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { AppError } from '@core/errors/app-error';
import { toAppError } from '@core/errors/to-app-error';
import { injectSignedOut } from '@core/events/session-events';
import { ApiUrl } from '@core/http/api-url';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';
import { ConversationStreamClient } from '@core/stream/conversation-stream-client';
import { Router } from '@angular/router';

/** What Retry re-sends: the turn's own prompt and selection, as sent, even if the pickers moved since. */
export interface TurnRetry {
  readonly prompt: string;
  readonly selection: TurnSelection;
}

/**
 * One settled item of the session transcript.
 *
 * `assistant` and `cutOff` entries keep their final folded snapshot and
 * ordering index, because the activity timeline is live-only (streaming
 * contract §6.2): it survives for the session precisely because it is held
 * here, and is gone on reload by the same fact.
 */
export type TranscriptEntry =
  | { readonly kind: 'user'; readonly id: number; readonly text: string }
  | {
      readonly kind: 'assistant';
      readonly id: number;
      readonly snapshot: AssistantStatusSnapshot;
      readonly timeline: TurnTimeline;
    }
  | {
      readonly kind: 'cutOff';
      readonly id: number;
      readonly snapshot: AssistantStatusSnapshot;
      readonly timeline: TurnTimeline;
      readonly retry: TurnRetry;
    };

/** Frame `1h`'s detached "Stopped — not saved to this conversation" card (US-407). */
export interface StoppedTurn {
  readonly text: string;
  readonly prompt: string;
  readonly selection: TurnSelection;
}

/** A pre-stream failure notice. `retry` is null when the reseeded composer is the retry surface. */
export interface TurnErrorNotice {
  readonly error: AppError;
  readonly retry: TurnRetry | null;
}

/**
 * - `idle` — no turn; the send control shows.
 * - `creating` — `POST api/conversations` is in flight for a first prompt.
 * - `awaitingFirst` — the stream request is out but nothing renderable has
 *   arrived; the thinking ridgeline shows (frame `1e`).
 * - `streaming` — content is arriving.
 * - `stopping` — the user pressed Stop; the abort's error is on its way.
 */
export type TurnPhase = 'idle' | 'creating' | 'awaitingFirst' | 'streaming' | 'stopping';

interface TurnState {
  /**
   * The conversation this screen's turns target — the route's id, or the id
   * `POST api/conversations` returned a moment before the URL catches up.
   * Written from the 201 *before* navigating, which is what lets `bindRoute`
   * tell "the URL caught up with our own create" from a real route change.
   */
  readonly boundConversationId: string | null;
  readonly phase: TurnPhase;
  readonly entries: readonly TranscriptEntry[];
  readonly nextEntryId: number;
  /**
   * The optimistic user bubble. A field rather than an entry so the settle is
   * one atomic patch — US-406's "exactly one append" — and US-407's "the
   * bubble is also removed" is nulling it, not popping an array.
   */
  readonly pendingUserText: string | null;
  /** The live turn's folded state — US-406's single accumulating string lives at `snapshot.text`. */
  readonly snapshot: AssistantStatusSnapshot;
  /** The live turn's arrival-order index (US-501). */
  readonly timeline: TurnTimeline;
  readonly stoppedTurn: StoppedTurn | null;
  readonly turnError: TurnErrorNotice | null;
  /**
   * A one-shot text for the composer: a prompt chip, or Retry restoring a
   * prompt. A fresh object per seed, so seeding the same text twice still
   * propagates; the composer nulls it back through `consumeComposerSeed`.
   */
  readonly composerSeed: { readonly text: string } | null;
}

const initialState: TurnState = {
  boundConversationId: null,
  phase: 'idle',
  entries: [],
  nextEntryId: 1,
  pendingUserText: null,
  snapshot: createInitialSnapshot(),
  timeline: createInitialTimeline(),
  stoppedTurn: null,
  turnError: null,
  composerSeed: null,
};

function beginTurn(
  command: TurnRetry,
  phase: 'creating' | 'awaitingFirst',
): PartialStateUpdater<TurnState> {
  return () => ({
    phase,
    pendingUserText: command.prompt,
    snapshot: createInitialSnapshot(),
    timeline: createInitialTimeline(),
    stoppedTurn: null,
    turnError: null,
  });
}

/**
 * Folds one delivered batch — at most one `patchState` per ~16ms flush, at any
 * token rate. Guarded to in-flight phases so a batch racing a reset cannot
 * resurrect a cleared turn; batches landing while `stopping` still fold,
 * because they are the partial output the stopped card keeps.
 */
function applyBatch(batch: readonly AssistantUiEvent[]): PartialStateUpdater<TurnState> {
  return (state) => {
    if (
      state.phase !== 'awaitingFirst' &&
      state.phase !== 'streaming' &&
      state.phase !== 'stopping'
    ) {
      return {};
    }

    // The thinking indicator clears only for renderable content. A batch of
    // `Status` or `ReasoningDelta` events must not clear it: this app emits no
    // request-level status line and fabricates none (US-406).
    const hasContent = batch.some(
      (event) => event.kind === 'ActivityStarted' || event.kind === 'TextDelta',
    );

    return {
      snapshot: batch.reduce(foldAssistantEvents, state.snapshot),
      timeline: batch.reduce(foldTimeline, state.timeline),
      phase: state.phase === 'awaitingFirst' && hasContent ? 'streaming' : state.phase,
    };
  };
}

/** Clears the live turn without touching the transcript, ready for the next send. */
function clearLiveTurn(): Pick<TurnState, 'phase' | 'pendingUserText' | 'snapshot' | 'timeline'> {
  return {
    phase: 'idle',
    pendingUserText: null,
    snapshot: createInitialSnapshot(),
    timeline: createInitialTimeline(),
  };
}

/** The one append `Finished` earns: the user and assistant entries land in a single patch. */
function settleFinished(): PartialStateUpdater<TurnState> {
  return (state) => ({
    ...clearLiveTurn(),
    entries: [
      ...state.entries,
      { kind: 'user', id: state.nextEntryId, text: state.pendingUserText ?? '' },
      {
        kind: 'assistant',
        id: state.nextEntryId + 1,
        snapshot: state.snapshot,
        timeline: state.timeline,
      },
    ],
    nextEntryId: state.nextEntryId + 2,
  });
}

/** The body ended without `Finished` — US-406's deliberate non-error arm. */
function settleCutOff(command: TurnRetry): PartialStateUpdater<TurnState> {
  return (state) => ({
    ...clearLiveTurn(),
    entries: [
      ...state.entries,
      { kind: 'user', id: state.nextEntryId, text: state.pendingUserText ?? '' },
      {
        kind: 'cutOff',
        id: state.nextEntryId + 1,
        snapshot: state.snapshot,
        timeline: state.timeline,
        retry: command,
      },
    ],
    nextEntryId: state.nextEntryId + 2,
  });
}

/**
 * A cancelled turn transcribes neither the answer nor the prompt (US-407): the
 * partial text moves to the detached card and the optimistic bubble goes with
 * the rest of the live turn.
 */
function settleStopped(command: TurnRetry): PartialStateUpdater<TurnState> {
  return (state) => ({
    ...clearLiveTurn(),
    stoppedTurn: {
      text: state.snapshot.text ?? '',
      prompt: command.prompt,
      selection: command.selection,
    },
  });
}

function seedComposer(text: string): PartialStateUpdater<TurnState> {
  return () => ({ composerSeed: { text } });
}

/** A pre-stream failure: the turn never produced anything, so only a notice remains. */
function settlePreStream(command: TurnRetry, error: AppError): PartialStateUpdater<TurnState> {
  return () => ({
    ...clearLiveTurn(),
    turnError: { error, retry: command },
  });
}

/**
 * `POST api/conversations` failed: the prompt goes back into the composer
 * (US-401's "not lost"), which is why the notice carries no Retry of its own.
 */
function settleCreateFailed(command: TurnRetry, error: AppError): PartialStateUpdater<TurnState> {
  return () => ({
    ...clearLiveTurn(),
    turnError: { error, retry: null },
    composerSeed: { text: command.prompt },
  });
}

/**
 * The abort settle. `aborted` is deliberately discriminated by *phase*, never
 * by abort reason: `stopping` means the user's Stop, so the partial output
 * becomes the detached card; `idle` means sign-out or a route-change reset
 * already cleared the state, and patching here would resurrect a turn — for
 * sign-out, the previous user's turn. Any other phase is an abort nothing
 * accounted for, and only the live turn is cleared — clearing cannot
 * resurrect anything, but a phase left in flight would wedge the composer
 * on a Stop control with nothing to stop.
 */
function settleFromError(command: TurnRetry, error: AppError): PartialStateUpdater<TurnState> {
  return (state) => {
    if (error.kind === 'aborted') {
      if (state.phase === 'stopping') {
        return settleStopped(command)(state);
      }

      return state.phase === 'idle' ? {} : clearLiveTurn();
    }

    if (state.phase === 'idle') {
      return {};
    }

    // A pre-stream error racing the user's Stop: they wanted out, so restore
    // the prompt rather than raising a notice for a turn they cancelled.
    if (state.phase === 'stopping') {
      return { ...clearLiveTurn(), composerSeed: { text: command.prompt } };
    }

    return settlePreStream(command, error)(state);
  };
}

/**
 * The completion settle. `Finished` wins even over a pending Stop — the abort
 * raced a clean close, the server transcribed the answer, and a card claiming
 * "not saved" would be false (US-407's own honesty rationale).
 */
function settleFromComplete(command: TurnRetry): PartialStateUpdater<TurnState> {
  return (state) => {
    if (state.phase === 'idle' || state.phase === 'creating') {
      return {};
    }

    if (state.snapshot.phase === 'Completed') {
      return settleFinished()(state);
    }

    // The server closed the body just as Stop was pressed: visually this is
    // the stop the user asked for, not a cut-off warning.
    if (state.phase === 'stopping') {
      return settleStopped(command)(state);
    }

    return settleCutOff(command)(state);
  };
}

/**
 * The live turn, the session transcript, and the send flow for the chat screen
 * (US-401, US-406, US-407; US-501 renders `timeline`).
 *
 * Provided by `Chat` beside `ConversationStore`, so its lifetime is the chat
 * screen's: leaving `/chat` tears the store down, which unsubscribes the
 * stream and aborts the fetch — there is no stop endpoint to call (US-405).
 * The transcript is session-local by design; history replay is US-410.
 */
export const TurnStore = signalStore(
  withState(initialState),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _router: inject(Router),
    _stream: inject(ConversationStreamClient),
    _settings: inject(TurnSettingsStore),
    _list: inject(ConversationListStore),
    _signedOut$: injectSignedOut(),
    /**
     * The in-flight turn's abort controller. A mutable prop rather than state:
     * it is not renderable, and `withResetOnSignOut`'s snapshot would clone-
     * compare it pointlessly. Stop and route changes abort through it; the
     * stream client composes sign-out and unsubscribe aborts itself.
     */
    _turnAbort: { current: null as AbortController | null },
    /** Cancels the create `POST`, which accepts no `AbortSignal` of its own. */
    _stop$: new Subject<void>(),
  })),
  withComputed((store) => {
    const inFlight = computed(() => store.phase() !== 'idle');

    return {
      inFlight,
      /** Frame `1e`: the ridgeline between request-accepted and the first renderable content. */
      showThinking: computed(
        () => store.phase() === 'creating' || store.phase() === 'awaitingFirst',
      ),
      /** The live assistant turn region — still rendered while `stopping`, until the settle lands. */
      showLiveTurn: computed(() => store.phase() === 'streaming' || store.phase() === 'stopping'),
      /** Whether the transcript replaces the empty state. */
      hasContent: computed(
        () =>
          store.entries().length > 0 ||
          store.pendingUserText() !== null ||
          store.stoppedTurn() !== null ||
          store.turnError() !== null ||
          inFlight(),
      ),
    };
  }),
  withMethods((store) => {
    const run = rxMethod<TurnRetry>(
      // exhaustMap is the double-send guard: a send while a turn is in flight
      // is ignored rather than queued — a queued one would only meet the
      // server's 409, because a conversation runs one turn at a time (§6.3).
      // A send in the microtask gap between a route-change reset and the old
      // stream's abort rejection is likewise swallowed; the window is
      // unreachable by real interaction, since the button is still a Stop
      // control for its duration.
      exhaustMap((command) => {
        const controller = new AbortController();
        store._turnAbort.current = controller;
        const existing = store.boundConversationId();
        patchState(store, beginTurn(command, existing === null ? 'creating' : 'awaitingFirst'));

        const createUrl = store._api.build('conversations');
        const conversationId$ =
          existing !== null
            ? of(existing)
            : store._http.post<ConversationDto>(createUrl, { projectId: null }).pipe(
                // The POST has no abort surface, so Stop and sign-out cancel
                // it here. Nothing to unwind: the tap below never ran.
                takeUntil(merge(store._signedOut$, store._stop$)),
                tap((created) => {
                  // Before navigating, so the header's list-fallback name
                  // renders the moment the URL updates (US-302's `name` path).
                  store._list.prependNewest(created);
                  // Before navigating too: `bindRoute` treats the incoming
                  // route id as "ours" only because this is already set.
                  patchState(store, { boundConversationId: created.id, phase: 'awaitingFirst' });
                  // replaceUrl rather than `Location.replaceState`, so the
                  // Router's own state moves with the URL — a deliberate
                  // deviation from US-401's letter (2026-08-12): replaceState
                  // leaves the router at `/chat`, which breaks the sidebar's
                  // routerLinkActive selection and makes "New conversation" a
                  // same-URL no-op. The intent — history replaced, no remount
                  // — holds: one route config serves both URL shapes.
                  void store._router.navigateByUrl(`/chat/${created.id}`, { replaceUrl: true });
                }),
                map((created) => created.id),
                catchError((cause: unknown) => {
                  patchState(
                    store,
                    settleCreateFailed(command, toAppError(cause, { url: createUrl })),
                  );
                  // EMPTY keeps the rxMethod alive: the next send must work.
                  return EMPTY;
                }),
              );

        return conversationId$.pipe(
          switchMap((conversationId) =>
            store._stream
              .stream({
                conversationId,
                prompt: command.prompt,
                modelId: command.selection.modelId,
                mcpServerIds: command.selection.mcpServerIds,
                signal: controller.signal,
              })
              .pipe(
                tapResponse({
                  next: (batch) => patchState(store, applyBatch(batch)),
                  // The client's contract: every error it emits is an AppError.
                  error: (error: AppError) => patchState(store, settleFromError(command, error)),
                  complete: () => patchState(store, settleFromComplete(command)),
                }),
              ),
          ),
        );
      }),
    );

    return {
      /**
       * Sends a prompt against the bound conversation, creating one first when
       * none is bound (US-401). The null-selection guard backs the composer's
       * disabled send: no caller may issue a turn without a `modelId`.
       */
      send(prompt: string): void {
        const selection = store._settings.streamSelection();
        const text = prompt.trim();
        if (selection === null || text === '') {
          return;
        }

        run({ prompt: text, selection });
      },

      /** Re-sends a cut-off or failed turn with its own prompt and selection. */
      retryTurn(retry: TurnRetry): void {
        run(retry);
      },

      /**
       * Stops the turn (US-407). During `creating` the POST is cancelled and
       * the prompt goes straight back to the composer — nothing streamed, so
       * there is no partial output to card. In flight, the phase flips to
       * `stopping` *before* the abort so the settle can tell Stop from
       * sign-out, and the send control returns on this synchronous patch.
       */
      stop(): void {
        const phase = store.phase();
        if (phase === 'creating') {
          store._stop$.next();
          patchState(store, (state) => ({
            ...clearLiveTurn(),
            composerSeed: { text: state.pendingUserText ?? '' },
          }));
          return;
        }

        if (phase === 'awaitingFirst' || phase === 'streaming') {
          patchState(store, { phase: 'stopping' });
          store._turnAbort.current?.abort();
        }
      },

      /**
       * The stopped card's Retry (US-407): restores the prompt into the
       * composer and the turn's model and MCP selection into the pickers —
       * and deliberately does not send.
       */
      restoreStoppedToComposer(): void {
        const stopped = store.stoppedTurn();
        if (stopped === null) {
          return;
        }

        store._settings.applyConversationSettings({
          modelId: stopped.selection.modelId,
          mcpServerIds: stopped.selection.mcpServerIds,
        });
        patchState(store, seedComposer(stopped.prompt), { stoppedTurn: null });
      },

      /** Seeds the composer's text — the prompt chips (US-303/401) and Retry restores. */
      seedComposer(text: string): void {
        patchState(store, seedComposer(text));
      },

      /** The composer acknowledges a seed it has applied. */
      consumeComposerSeed(): void {
        patchState(store, { composerSeed: null });
      },

      /**
       * Follows the route's `conversationId` input. A change of conversation —
       * including to none — aborts any live turn and clears the session
       * transcript, the stopped card, and every notice (US-407: "gone on
       * reopen or route change"). The equality guard is what keeps US-401's
       * own `replaceUrl` navigation from destroying the turn it belongs to:
       * `boundConversationId` was set from the 201 before the URL moved.
       */
      bindRoute: signalMethod<string | undefined>((incoming) => {
        const id = incoming ?? null;

        // A route event while the create is in flight is always foreign — the
        // store's own navigation cannot fire before the 201's tap has moved
        // the phase on. Without this cancel, the 201 would land after the
        // reset below, clobber the new binding, and navigate the user off the
        // conversation they just opened.
        const foreignCreate = store.phase() === 'creating';
        if (foreignCreate) {
          store._stop$.next();
        }

        if (id === store.boundConversationId()) {
          if (foreignCreate) {
            // Same-URL corner: the create was cancelled but no reset follows,
            // so settle exactly as Stop-during-create does.
            patchState(store, (state) => ({
              ...clearLiveTurn(),
              composerSeed: { text: state.pendingUserText ?? '' },
            }));
          }
          return;
        }

        store._turnAbort.current?.abort();
        store._turnAbort.current = null;
        patchState(store, () => ({ ...initialState, boundConversationId: id }));
      }),
    };
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
