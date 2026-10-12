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
import { Dispatcher } from '@ngrx/signals/events';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import {
  EMPTY,
  Subject,
  catchError,
  exhaustMap,
  finalize,
  map,
  merge,
  mergeMap,
  of,
  pipe,
  switchMap,
  take,
  takeUntil,
  tap,
} from 'rxjs';
import {
  CHAT_ROLE,
  ChatConversationDto,
  ConversationDto,
  ConversationMessageDto,
  MessageAttachmentDto,
  MessageFeedbackRating,
} from '@domain/api/conversation';
import {
  AssistantStatusSnapshot,
  AssistantUiEvent,
  createInitialSnapshot,
  foldAssistantEvents,
} from '@domain/stream/andes/assistant-ui.contract';
import { replayAssistantText } from '@domain/stream/replayed-turn';
import { turnAnnouncement } from '@domain/stream/turn-announcement';
import { TurnTimeline, createInitialTimeline, foldTimeline } from '@domain/stream/turn-timeline';
import {
  ReasoningTiming,
  createInitialReasoningTiming,
  foldReasoningTiming,
  hasModelReasoning,
  modelReasoningText,
  reasoningSeconds,
} from '@domain/stream/reasoning-timing';
import { ComposerExportTarget, ComposerProjectTarget } from '@core/chat/composer-host';
import { PendingAttachmentsStore } from '@core/chat/pending-attachments-store';
import { PendingPromptStore } from '@core/chat/pending-prompt-store';
import { TurnSettingsStore, TurnSelection } from '@core/chat/turn-settings-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { AppError } from '@core/errors/app-error';
import { ToastStore } from '@core/notifications/toast-store';
import { toAppError } from '@core/errors/to-app-error';
import { injectSignedOut } from '@core/events/session-events';
import { turnEvents } from '@core/events/turn-events';
import { ApiUrl } from '@core/http/api-url';
import {
  addPendingId,
  clearPendingIds,
  removePendingId,
  withPendingIds,
} from '@core/state/with-pending-ids';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';
import { ConversationStreamClient } from '@core/stream/conversation-stream-client';
import { Router } from '@angular/router';
import { UploadStore } from '@core/documents/upload-store';
import { ConversationStore } from './conversation-store';

/** What Retry re-sends: the prompt and selection as sent, even if the pickers moved since. */
export interface TurnRetry {
  readonly prompt: string;
  readonly selection: TurnSelection;
}

/**
 * Holds each turn's timeline, which the stream never replays. Replayed ids count down from -1
 * and live ids up from 1, so a history refetch never collides with live turns.
 */
export type TranscriptEntry =
  | { readonly kind: 'user'; readonly id: number; readonly text: string }
  | {
      readonly kind: 'assistant';
      readonly id: number;
      readonly snapshot: AssistantStatusSnapshot;
      readonly timeline: TurnTimeline;
      /** The model's own reasoning, without the server's seeded delta. */
      readonly reasoningText: string;
      /** How long the model reasoned, measured while it streamed. */
      readonly reasoningSeconds: number | null;
      /**
       * Null for an id-less replayed message or a turn whose transcript write failed. No id, no
       * thumbs: there is nowhere to address a rating.
       */
      readonly messageId: string | null;
      /** Server-confirmed, or optimistic. */
      readonly rating: MessageFeedbackRating | null;
      /** Identities only: a link is minted per click, so nothing here goes stale. */
      readonly attachments: readonly MessageAttachmentDto[];
    }
  | {
      readonly kind: 'cutOff';
      readonly id: number;
      readonly snapshot: AssistantStatusSnapshot;
      readonly timeline: TurnTimeline;
      readonly reasoningText: string;
      readonly reasoningSeconds: number | null;
      readonly retry: TurnRetry;
    };

/** The detached "Stopped — not saved" card left after the user stops a turn. */
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
 * - `creating`: `POST api/conversations` is in flight for a first prompt.
 * - `awaitingFirst`: the stream is open but nothing renderable has arrived.
 * - `stopping`: the user pressed Stop; the abort's error is on its way.
 */
export type TurnPhase = 'idle' | 'creating' | 'awaitingFirst' | 'streaming' | 'stopping';

interface TurnState {
  /**
   * Written from the 201 before navigating, so `bindRoute` can tell the URL catching up with our
   * own create from a real route change.
   */
  readonly boundConversationId: string | null;
  readonly phase: TurnPhase;
  readonly entries: readonly TranscriptEntry[];
  readonly nextEntryId: number;
  /** The optimistic user bubble; a field, not an entry, so settling is one atomic patch. */
  readonly pendingUserText: string | null;
  /** The live turn's folded state; the accumulating answer is `snapshot.text`. */
  readonly snapshot: AssistantStatusSnapshot;
  /** The live turn's arrival-order index. */
  readonly timeline: TurnTimeline;
  /** The live turn's reasoning window, measured from arrival times. */
  readonly reasoning: ReasoningTiming;
  readonly stoppedTurn: StoppedTurn | null;
  readonly turnError: TurnErrorNotice | null;
  /** The stored transcript is being read back. */
  readonly historyPending: boolean;
  /** The stored transcript could not be read. The turn surface stays usable. */
  readonly historyError: AppError | null;
  /**
   * One-shot composer text from a chip or Retry. A fresh object per seed, so the same text twice
   * still propagates.
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
  reasoning: createInitialReasoningTiming(),
  stoppedTurn: null,
  turnError: null,
  historyPending: false,
  historyError: null,
  composerSeed: null,
};

/**
 * Replaces the history block rather than merging: after an aborted turn the server holds fewer
 * messages than the screen. `keepLive` is false for a retry, whose response already includes any
 * turn taken while history was broken.
 */
function replaceHistory(
  replayed: readonly TranscriptEntry[],
  keepLive: boolean,
): PartialStateUpdater<TurnState> {
  return (state) => ({
    entries: keepLive
      ? [...replayed, ...state.entries.filter((entry) => entry.id > 0)]
      : [...replayed],
    historyPending: false,
    historyError: null,
  });
}

function beginTurn(
  command: TurnRetry,
  phase: 'creating' | 'awaitingFirst',
): PartialStateUpdater<TurnState> {
  return () => ({
    phase,
    pendingUserText: command.prompt,
    snapshot: createInitialSnapshot(),
    timeline: createInitialTimeline(),
    reasoning: createInitialReasoningTiming(),
    stoppedTurn: null,
    turnError: null,
  });
}

/**
 * Guarded to in-flight phases so a batch racing a reset cannot resurrect a cleared turn. Batches
 * during `stopping` still fold: they are the partial output the stopped card keeps.
 */
function applyBatch(
  batch: readonly AssistantUiEvent[],
  nowMs: number,
): PartialStateUpdater<TurnState> {
  return (state) => {
    if (
      state.phase !== 'awaitingFirst' &&
      state.phase !== 'streaming' &&
      state.phase !== 'stopping'
    ) {
      return {};
    }

    // Only renderable content clears the thinking indicator; no status line is fabricated.
    const hasContent = batch.some(
      (event) => event.kind === 'ActivityStarted' || event.kind === 'TextDelta',
    );

    return {
      snapshot: batch.reduce(foldAssistantEvents, state.snapshot),
      timeline: batch.reduce(foldTimeline, state.timeline),
      // The caller's clock keeps this pure; one reading per ~16ms flush is precise enough.
      reasoning: batch.reduce(
        (timing, event) => foldReasoningTiming(timing, event, nowMs),
        state.reasoning,
      ),
      phase: state.phase === 'awaitingFirst' && hasContent ? 'streaming' : state.phase,
    };
  };
}

/** Clears the live turn without touching the transcript, ready for the next send. */
function clearLiveTurn(): Pick<
  TurnState,
  'phase' | 'pendingUserText' | 'snapshot' | 'timeline' | 'reasoning'
> {
  return {
    phase: 'idle',
    pendingUserText: null,
    snapshot: createInitialSnapshot(),
    timeline: createInitialTimeline(),
    reasoning: createInitialReasoningTiming(),
  };
}

/**
 * Narrowed element by element: a malformed entry from the untyped metadata bag would throw inside
 * a computed and take down the transcript's render.
 */
function parseAttachments(value: string | undefined): readonly MessageAttachmentDto[] {
  if (!value) {
    return [];
  }

  try {
    const parsed: unknown = JSON.parse(value);
    return Array.isArray(parsed) ? parsed.filter(isAttachment) : [];
  } catch {
    return [];
  }
}

function isAttachment(value: unknown): value is MessageAttachmentDto {
  const file = value as Partial<MessageAttachmentDto> | null;

  return (
    typeof file?.id === 'string' && typeof file.name === 'string' && typeof file.size === 'number'
  );
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
        reasoningText: modelReasoningText(state.reasoning, state.snapshot.reasoningText ?? ''),
        reasoningSeconds: reasoningSeconds(state.reasoning),
        // A failed transcript write sends `Finished` with no metadata: nothing to rate.
        messageId: state.snapshot.metadata?.['assistantMessageId'] ?? null,
        rating: null,
        // JSON in a string: the vendored contract's metadata bag is `Record<string, string>`.
        attachments: parseAttachments(state.snapshot.metadata?.['generatedAttachments']),
      },
    ],
    nextEntryId: state.nextEntryId + 2,
  });
}

/** The body ended without `Finished`: deliberately not an error. */
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
        reasoningText: modelReasoningText(state.reasoning, state.snapshot.reasoningText ?? ''),
        reasoningSeconds: reasoningSeconds(state.reasoning),
        retry: command,
      },
    ],
    nextEntryId: state.nextEntryId + 2,
  });
}

/** A stopped turn transcribes nothing: the partial text moves to the detached card. */
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

/**
 * Other entries keep their identity so `@for` does not re-create them. Writes nothing when nothing
 * changed: several callers are no-ops by design and would otherwise invalidate the computeds.
 */
function rateEntry(
  messageId: string,
  rating: MessageFeedbackRating | null,
): PartialStateUpdater<TurnState> {
  return (state) => {
    const index = state.entries.findIndex(
      (entry) => entry.kind === 'assistant' && entry.messageId === messageId,
    );
    const target = state.entries[index];

    if (target === undefined || target.kind !== 'assistant' || target.rating === rating) {
      return {};
    }

    return {
      entries: state.entries.map((entry, position) =>
        position === index ? { ...target, rating } : entry,
      ),
    };
  };
}

/**
 * Only user and assistant messages render: `Tool` is a model-to-model artefact, and an unknown
 * role from a newer server is skipped rather than guessed at.
 */
function toHistoryEntries(messages: readonly ConversationMessageDto[]): TranscriptEntry[] {
  const entries: TranscriptEntry[] = [];

  for (const message of messages) {
    const id = -(entries.length + 1);
    if (message.role === CHAT_ROLE.user) {
      entries.push({ kind: 'user', id, text: message.text });
    } else if (message.role === CHAT_ROLE.assistant) {
      // Reasoning is never transcribed, so a replayed turn has no window to report.
      entries.push({
        kind: 'assistant',
        id,
        reasoningText: '',
        reasoningSeconds: null,
        // `?? null` despite the required type: an older server sends no id, and `undefined`
        // would pass the `!== null` gate and post to `.../messages/undefined/feedback`.
        messageId: message.id ?? null,
        // Ratings and files survive a reload only through what the server holds here.
        rating: message.feedback?.rating ?? null,
        attachments: message.attachments ?? [],
        ...replayAssistantText(message.text),
      });
    }
  }

  return entries;
}

/** A pre-stream failure: the turn never produced anything, so only a notice remains. */
function settlePreStream(command: TurnRetry, error: AppError): PartialStateUpdater<TurnState> {
  return () => ({
    ...clearLiveTurn(),
    turnError: { error, retry: command },
  });
}

/** The prompt goes back into the composer, so the notice needs no Retry of its own. */
function settleCreateFailed(command: TurnRetry, error: AppError): PartialStateUpdater<TurnState> {
  return () => ({
    ...clearLiveTurn(),
    turnError: { error, retry: null },
    composerSeed: { text: command.prompt },
  });
}

/**
 * `aborted` is told apart by phase, never reason: `stopping` is the user's Stop; `idle` means a
 * reset already cleared state, which patching would resurrect. Any other phase clears the live
 * turn so the composer is not wedged on a Stop with nothing to stop.
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

    // An error racing the user's Stop restores the prompt instead of raising a notice.
    if (state.phase === 'stopping') {
      return { ...clearLiveTurn(), composerSeed: { text: command.prompt } };
    }

    return settlePreStream(command, error)(state);
  };
}

/** `Finished` beats a pending Stop: the answer was saved, so a "not saved" card would lie. */
function settleFromComplete(command: TurnRetry): PartialStateUpdater<TurnState> {
  return (state) => {
    if (state.phase === 'idle' || state.phase === 'creating') {
      return {};
    }

    if (state.snapshot.phase === 'Completed') {
      return settleFinished()(state);
    }

    // Closed just as Stop was pressed: show the stop the user asked for, not a cut-off.
    if (state.phase === 'stopping') {
      return settleStopped(command)(state);
    }

    return settleCutOff(command)(state);
  };
}

/**
 * Provided by `Chat`, so leaving `/chat` unsubscribes the stream and aborts the fetch; there is no
 * stop endpoint. The activity timeline is session-local: the stream is never persisted.
 */
export const TurnStore = signalStore(
  withState(initialState),
  // Keyed by message id, so a rating in flight disables only that answer's thumbs.
  withPendingIds(),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _router: inject(Router),
    _stream: inject(ConversationStreamClient),
    _dispatcher: inject(Dispatcher),
    _settings: inject(TurnSettingsStore),
    _list: inject(ConversationListStore),
    /** For `projectTarget`'s fallback. No cycle: `ConversationStore` injects only root stores. */
    _detail: inject(ConversationStore),
    /** One-way: the upload store is told its conversation, so injecting this back would cycle. */
    _uploads: inject(UploadStore),
    /** A conversation created elsewhere hands its first prompt over here, cleared on read. */
    _pending: inject(PendingPromptStore),
    _attachments: inject(PendingAttachmentsStore),
    _toasts: inject(ToastStore),
    _signedOut$: injectSignedOut(),
    /**
     * A prop, not state: it is not renderable. Stop and route changes abort through it; the stream
     * client handles sign-out and unsubscribe itself.
     */
    _turnAbort: { current: null as AbortController | null },
    /** Cancels the create `POST`, which accepts no `AbortSignal` of its own. */
    _stop$: new Subject<void>(),
    /**
     * Not `_stop$`, which is the user's Stop. Without it, a route change would orphan a rating
     * request whose id is no longer pending, letting a second one race it.
     */
    _rateCancel$: new Subject<void>(),
    /** Waiting on the upload gate with no stream open; Stop settles it like `creating`. */
    _awaitingUploads: { value: false },
  })),
  withComputed((store) => {
    const inFlight = computed(() => store.phase() !== 'idle');
    /**
     * Hands the pre-answer gap from the ridgeline to the reasoning region. Reads past the server's
     * seeded delta, which would otherwise count as a fabricated status line.
     */
    const modelReasoning = computed(() => hasModelReasoning(store.reasoning()));
    /**
     * Set when `Finished` folds, one step before `inFlight` clears at end-of-body, so the answer's
     * live region is released while the turn is mounted. Assumes nothing renderable follows
     * `Finished`; if something does, later deltas are announced singly rather than dropped.
     */
    const answerComplete = computed(() => store.snapshot.phase() === 'Completed');

    return {
      inFlight,
      answerComplete,
      /**
       * List copy first, as `moveRow` patches it optimistically; the detail covers rows the
       * sidebar never held. Null while neither is in hand, since `toUpdateBody` echoes `projectId`.
       */
      projectTarget: computed<ComposerProjectTarget | null>(() => {
        const id = store.boundConversationId();
        if (id === null) {
          return null;
        }

        const conversation = store._list.entityMap()[id] ?? store._detail.conversation();

        return conversation === undefined || conversation === null
          ? null
          : { kind: 'conversation', conversation };
      }),
      /**
       * Tests `entries`, not `hasContent`, which is also true for pending history and pre-stream
       * notices. The name follows `projectTarget`'s list-then-detail precedence.
       */
      exportTarget: computed<ComposerExportTarget | null>(() => {
        const id = store.boundConversationId();
        if (id === null || store.entries().length === 0) {
          return null;
        }

        const conversation = store._list.entityMap()[id] ?? store._detail.conversation();

        return conversation === undefined || conversation === null
          ? null
          : { id, name: conversation.name };
      }),
      /** The ridgeline between request-accepted and something to read. */
      showThinking: computed(
        () =>
          store.phase() === 'creating' || (store.phase() === 'awaitingFirst' && !modelReasoning()),
      ),
      /** From the first reasoning delta, so the summary streams in; still shown while stopping. */
      showLiveTurn: computed(
        () =>
          store.phase() === 'streaming' ||
          store.phase() === 'stopping' ||
          (store.phase() === 'awaitingFirst' && modelReasoning()),
      ),
      /** The live turn's reasoning, seed removed. */
      liveReasoningText: computed(() =>
        modelReasoningText(store.reasoning(), store.snapshot().reasoningText ?? ''),
      ),
      liveReasoningSeconds: computed(() => reasoningSeconds(store.reasoning())),
      /**
       * Changes only on status transitions, never per `TextDelta`. `Answer ready` is a backstop
       * for assistive tech that forgets what `aria-busy` suppressed.
       */
      turnStatus: computed(() => {
        if (!inFlight()) {
          return '';
        }

        return answerComplete()
          ? 'Answer ready'
          : turnAnnouncement(store.snapshot(), {
              creating: store.phase() === 'creating',
              reasoning: modelReasoning(),
            });
      }),
      /** History states count, so a conversation being read back never flashes the chips. */
      hasContent: computed(
        () =>
          store.entries().length > 0 ||
          store.pendingUserText() !== null ||
          store.stoppedTurn() !== null ||
          store.turnError() !== null ||
          store.historyPending() ||
          store.historyError() !== null ||
          inFlight(),
      ),
    };
  }),
  withMethods((store) => {
    /** Every read comes through here, so `switchMap` cancels one for a conversation left behind. */
    const loadHistory = rxMethod<{ id: string | null; keepLive: boolean }>(
      pipe(
        tap(({ id }) =>
          patchState(store, {
            historyPending: id !== null,
            historyError: null,
          }),
        ),
        switchMap(({ id, keepLive }) => {
          if (id === null) {
            return EMPTY;
          }

          const url = store._api.build(`conversations/${ApiUrl.segment(id)}/messages`);

          return store._http.get<ChatConversationDto>(url).pipe(
            takeUntil(store._signedOut$),
            tapResponse({
              next: (conversation) =>
                patchState(
                  store,
                  replaceHistory(toHistoryEntries(conversation.messages), keepLive),
                ),
              error: (cause: unknown) =>
                patchState(store, {
                  historyPending: false,
                  historyError: toAppError(cause, { url }),
                }),
            }),
          );
        }),
      ),
    );

    const run = rxMethod<TurnRetry>(
      // A send while a turn is in flight is dropped, not queued: the server runs one turn per
      // conversation and would answer a queued one with 409.
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
                  // Before navigating, so the header's list-fallback name renders at once.
                  store._list.prependNewest(created);
                  // Not via the route: the upload gate below needs this id now. The route
                  // binding repeats it a pass later as a no-op.
                  store._uploads.bindConversation(created.id);
                  // Before navigating: `bindRoute` recognises the incoming id as ours by this.
                  patchState(store, { boundConversationId: created.id, phase: 'awaitingFirst' });
                  // Not `Location.replaceState`, which leaves the router at `/chat`: that breaks
                  // routerLinkActive and makes "New conversation" a same-URL no-op.
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
          // Attachments finish ingesting before the stream opens: retrieval only sees documents
          // the conversation already holds. `stop()` settles this window itself.
          switchMap((conversationId) => {
            store._awaitingUploads.value = true;
            // Here, not on attach: files are held until their turn is actually sent.
            store._uploads.startPending();

            return store._uploads.settled$().pipe(
              // Cancelled, this completes without emitting, which frees `exhaustMap` for the
              // next send instead of opening a stream nobody wants.
              takeUntil(merge(store._signedOut$, store._stop$)),
              take(1),
              map(() => conversationId),
              finalize(() => (store._awaitingUploads.value = false)),
            );
          }),
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
                  // Monotonic, so a wall-clock step cannot render a negative duration.
                  next: (batch) => patchState(store, applyBatch(batch, performance.now())),
                  // The client's contract: every error it emits is an AppError.
                  error: (error: AppError) => patchState(store, settleFromError(command, error)),
                  complete: () => {
                    // Read before the settle appends this turn. An unread history is not
                    // an empty one, so a history error never counts as a first turn.
                    const completed = store.snapshot().phase === 'Completed';
                    const wasFirstTurn =
                      store.historyError() === null &&
                      !store.entries().some((entry) => entry.kind === 'assistant');

                    patchState(store, settleFromComplete(command));

                    // Only `Finished` counts: a stopped or cut-off turn gave the server
                    // nothing to name the conversation after.
                    if (completed) {
                      store._dispatcher.dispatch(
                        turnEvents.completed({ conversationId, wasFirstTurn }),
                      );
                    }
                  },
                }),
              ),
          ),
        );
      }),
    );

    /** `mergeMap`: answers rate independently, and `rateMessage` refuses a same-id overlap. */
    const rate = rxMethod<{
      conversationId: string;
      messageId: string;
      rating: MessageFeedbackRating | null;
      previous: MessageFeedbackRating | null;
    }>(
      mergeMap(({ conversationId, messageId, rating, previous }) => {
        // Synchronous with the call, so `rateMessage`'s guard sees the pending id on a
        // double-click's second call.
        patchState(store, addPendingId(messageId), rateEntry(messageId, rating));

        // Carried in, so a route change mid-request cannot redirect the rating.
        const url = store._api.build(
          `conversations/${ApiUrl.segment(conversationId)}/messages/${ApiUrl.segment(messageId)}/feedback`,
        );

        // A set, not a toggle: a retried or duplicated request cannot flip the value.
        return store._http.post<void>(url, { rating }).pipe(
          takeUntil(merge(store._signedOut$, store._rateCancel$)),
          tapResponse({
            next: () =>
              // Re-asserted: a no-op normally, the repair if a history reload landed in between.
              patchState(store, rateEntry(messageId, rating)),
            error: (cause: unknown) => {
              patchState(store, rateEntry(messageId, previous));
              store._toasts.fromError(toAppError(cause, { url }));
            },
            finalize: () => patchState(store, removePendingId(messageId)),
          }),
        );
      }),
    );

    return {
      /** Creates the conversation first when none is bound. No turn is sent without a model. */
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
       * Pressing the selected thumb withdraws the rating; only the store knows what is stored.
       * Refused while that message's request is in flight, or when it is not rateable here.
       */
      rateMessage(messageId: string, rating: MessageFeedbackRating): void {
        const conversationId = store.boundConversationId();
        if (conversationId === null || store.isRowPending(messageId)) {
          return;
        }

        const target = store
          .entries()
          .find((entry) => entry.kind === 'assistant' && entry.messageId === messageId);

        if (target === undefined || target.kind !== 'assistant') {
          return;
        }

        rate({
          conversationId,
          messageId,
          rating: target.rating === rating ? null : rating,
          previous: target.rating,
        });
      },

      /** Flips to `stopping` before the abort, so the settle can tell Stop from sign-out. */
      stop(): void {
        const phase = store.phase();
        // Before any stream: no partial output to card, so the prompt returns to the composer.
        if (phase === 'creating' || store._awaitingUploads.value) {
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

      /** The stopped card's Retry: restores prompt and selection, deliberately without sending. */
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

      /** For the prompt chips and Retry restores. */
      seedComposer(text: string): void {
        patchState(store, seedComposer(text));
      },

      /** The composer acknowledges a seed it has applied. */
      consumeComposerSeed(): void {
        patchState(store, { composerSeed: null });
      },

      /** Discards live entries: any turn taken while history was broken is in the response. */
      retryHistory(): void {
        loadHistory({ id: store.boundConversationId(), keepLive: false });
      },

      /**
       * A conversation change resets everything. The equality guard keeps the first prompt's
       * `replaceUrl` from destroying its own turn, as the 201 already set the bound id.
       */
      bindRoute: signalMethod<string | undefined>((incoming) => {
        const id = incoming ?? null;

        // A route event during `creating` is foreign: the store navigates only after the 201
        // moves the phase on. Uncancelled, that 201 would clobber the new binding.
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
        // Also ends an upload-gate wait, which the abort cannot reach before a stream opens and
        // which would hold `exhaustMap`'s slot.
        store._stop$.next();
        store._turnAbort.current = null;
        // Cancelled, not forgotten, or a returning reader could race an orphaned rating. The
        // explicit clear is needed because `initialState` carries no pendingIds key.
        store._rateCancel$.next();
        patchState(store, () => ({ ...initialState, boundConversationId: id }), clearPendingIds());
        // After the reset, so the first prompt's `replaceUrl` (returned early above) never reads
        // back a conversation whose first turn is not persisted yet.
        loadHistory({ id, keepLive: true });

        // Files handed over from the project screen, claimed in the gate's own call stack (a pass
        // later would miss it). Bind first, or the target change clears the chips just added.
        const handedOver = id === null ? [] : store._attachments.claim(id);
        if (id !== null && handedOver.length > 0) {
          store._uploads.bindConversation(id);
          store._uploads.attach(handedOver);
        } else {
          // Landing anywhere else abandons the handoff; release the held files rather than keep
          // them in root state until sign-out.
          store._attachments.clear();
        }

        // Claimed after the reset so the send lands on this binding; single-shot, so a refresh
        // never sends it twice.
        const pending = id === null ? null : store._pending.claim(id);
        const pendingText = pending?.trim() ?? '';
        if (pendingText !== '') {
          const selection = store._settings.streamSelection();
          if (selection === null) {
            // No model yet: the prompt goes to the composer rather than being dropped.
            patchState(store, seedComposer(pendingText));
          } else {
            run({ prompt: pendingText, selection });
          }
        }
      }),
    };
  }),
  // Last, as the feature requires.
  withResetOnSignOut(),
);
