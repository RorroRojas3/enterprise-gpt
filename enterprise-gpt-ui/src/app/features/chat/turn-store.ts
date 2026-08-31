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
 *
 * Ids are the `@for` track key and split into two ranges: replayed history
 * (US-410) counts down from -1 and live turns count up from 1, so a refetch
 * can rebuild the whole history block without renumbering — or colliding
 * with — turns taken since.
 */
export type TranscriptEntry =
  | { readonly kind: 'user'; readonly id: number; readonly text: string }
  | {
      readonly kind: 'assistant';
      readonly id: number;
      readonly snapshot: AssistantStatusSnapshot;
      readonly timeline: TurnTimeline;
      /** The model's own reasoning, without the server's seeded delta (US-503). */
      readonly reasoningText: string;
      /** How long the model reasoned, measured while it streamed (US-503). */
      readonly reasoningSeconds: number | null;
      /**
       * The server's own id for this message, or null when the turn claimed none
       * (US-1101). Null is the honest answer twice over: for a replayed message a server
       * sent no id for, and for a completed turn whose transcript write failed — that
       * turn is sent a `Finished` carrying no `metadata` at all. No id means no thumbs,
       * because there is nowhere to address a rating.
       */
      readonly messageId: string | null;
      /** The rating recorded against it (US-1103): server-confirmed, or optimistic. */
      readonly rating: MessageFeedbackRating | null;
      /**
       * The files the assistant produced on this turn, empty when it produced none.
       *
       * Identities only — a link is minted when a chip is clicked, so nothing here goes
       * stale between the turn and the download.
       */
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
  /** The live turn's reasoning window, measured from arrival times (US-503). */
  readonly reasoning: ReasoningTiming;
  readonly stoppedTurn: StoppedTurn | null;
  readonly turnError: TurnErrorNotice | null;
  /** The stored transcript is being read back (US-410). */
  readonly historyPending: boolean;
  /** The stored transcript could not be read. The turn surface stays usable. */
  readonly historyError: AppError | null;
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
  reasoning: createInitialReasoningTiming(),
  stoppedTurn: null,
  turnError: null,
  historyPending: false,
  historyError: null,
  composerSeed: null,
};

/**
 * Rebuilds the replayed-history block (US-410's "replaced, not merged").
 *
 * A whole-block replace rather than a merge is the point: after an aborted
 * turn the server holds *fewer* messages than the screen does, and a merge
 * would leave the abandoned pair on screen forever, claiming the model saw
 * them. History occupies the negative id range, so live entries can survive
 * untouched with their `@for` identity stable across the swap.
 *
 * `keepLive` is what the two callers disagree on. A read that was already in
 * flight predates any turn taken during it, so those entries are still to
 * come and are kept. A **retry** is issued after everything on screen — the
 * panel keeps the composer live, so the user may well have taken a turn while
 * history was broken — and the response now contains that turn too, so
 * keeping it would render the exchange twice.
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
 * Folds one delivered batch — at most one `patchState` per ~16ms flush, at any
 * token rate. Guarded to in-flight phases so a batch racing a reset cannot
 * resurrect a cleared turn; batches landing while `stopping` still fold,
 * because they are the partial output the stopped card keeps.
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

    // The thinking indicator clears only for renderable content. A batch of
    // `Status` or `ReasoningDelta` events must not clear it: this app emits no
    // request-level status line and fabricates none (US-406).
    const hasContent = batch.some(
      (event) => event.kind === 'ActivityStarted' || event.kind === 'TextDelta',
    );

    return {
      snapshot: batch.reduce(foldAssistantEvents, state.snapshot),
      timeline: batch.reduce(foldTimeline, state.timeline),
      // The clock is the caller's, so this stays a pure function of its
      // arguments — the same reason the other two reducers take no clock.
      // One reading per flush is enough: a ~16ms batch is finer than a
      // duration written to a tenth of a second.
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
 * Reads the generated-file identities off the `Finished` frame's metadata.
 *
 * Narrowed element by element, not just parsed: the payload crosses an untyped
 * `Record<string, string>` bag, and a malformed entry reaching the chip would throw inside
 * a computed and take the whole transcript's render with it. A turn whose files could not
 * be described is a turn with no chips.
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
        // Bracket access because `metadata` is an index signature and
        // `noPropertyAccessFromIndexSignature` is on. A completed turn whose transcript
        // write failed is sent `Finished` with no metadata object at all, which lands
        // here as null and leaves the answer unratable — the honest outcome, since there
        // is no persisted message to rate.
        messageId: state.snapshot.metadata?.['assistantMessageId'] ?? null,
        rating: null,
        // The chip on the turn that made the file. The bag is Record<string, string>, so
        // the payload is JSON in a string — the contract is vendored and byte-locked, and
        // a typed array there would mean a new event kind.
        attachments: parseAttachments(state.snapshot.metadata?.['generatedAttachments']),
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
        reasoningText: modelReasoningText(state.reasoning, state.snapshot.reasoningText ?? ''),
        reasoningSeconds: reasoningSeconds(state.reasoning),
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

/**
 * Writes a rating onto one settled assistant entry (US-1103).
 *
 * The matched entry is replaced rather than mutated, and so is the array around it:
 * `patchState` compares each key by reference before it writes, so an in-place edit would
 * perform no signal write at all and the footer would simply never update. Every other
 * entry keeps its identity, which is what stops the `@for` re-creating the transcript.
 *
 * Writing nothing when nothing changed is the other half of that. Three of this
 * function's callers are no-ops by design — the 204 re-asserts a value already written,
 * and both handlers of a request that outlived its conversation address an entry the
 * transcript no longer holds — and each would otherwise install a new `entries` identity,
 * invalidating `hasContent`, `exportTarget` and `announcement` for a state change that
 * did not happen.
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
 * Maps the stored messages onto transcript entries (US-410).
 *
 * Only the two roles the user wrote or read are rendered: the server already
 * drops `System`, and a `Tool` message is a model-to-model artefact that the
 * activity timeline — which this response cannot reconstruct anyway — is the
 * proper surface for. An unknown role from a future server is skipped rather
 * than guessed at.
 */
function toHistoryEntries(messages: readonly ConversationMessageDto[]): TranscriptEntry[] {
  const entries: TranscriptEntry[] = [];

  for (const message of messages) {
    const id = -(entries.length + 1);
    if (message.role === CHAT_ROLE.user) {
      entries.push({ kind: 'user', id, text: message.text });
    } else if (message.role === CHAT_ROLE.assistant) {
      // Reasoning is never transcribed (streaming contract §6.2), so a replayed
      // turn has no window to report — as it has no activities and no usage.
      entries.push({
        kind: 'assistant',
        id,
        reasoningText: '',
        reasoningSeconds: null,
        // `?? null` against a required field on purpose: `id` is what US-1101 guarantees,
        // and typing it as optional would spread the doubt to every reader. But an older
        // server that sends none would otherwise put `undefined` here, which passes the
        // `!== null` gate and offers thumbs that post to `.../messages/undefined/feedback`.
        // Unratable is the right answer for a message with no anchor.
        messageId: message.id ?? null,
        // The whole of how a rating survives a reload: replaceHistory rebuilds this block
        // from the response, so what the server holds is what the footer draws.
        rating: message.feedback?.rating ?? null,
        // The whole of how a generated file survives a reload, and the same mechanism the
        // rating above uses: what the server holds is what the transcript draws.
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
 *
 * Opening a conversation replays its stored messages (US-410); the activity
 * timeline around them stays session-local, because the stream that produces
 * it is never persisted (streaming contract §6.2).
 */
export const TurnStore = signalStore(
  withState(initialState),
  // Keyed by the server's message id, so a rating in flight disables that answer's thumbs
  // and no other's (US-1103).
  withPendingIds(),
  withProps(() => ({
    _http: inject(HttpClient),
    _api: inject(ApiUrl),
    _router: inject(Router),
    _stream: inject(ConversationStreamClient),
    _dispatcher: inject(Dispatcher),
    _settings: inject(TurnSettingsStore),
    _list: inject(ConversationListStore),
    /**
     * The open conversation's detail DTO, for `projectTarget`'s fallback (US-307).
     *
     * No cycle: `ConversationStore` reaches only root stores, and `Chat` provides both
     * on one injector. The dependency runs this way only.
     */
    _detail: inject(ConversationStore),
    /**
     * The composer's attachments (EP-8). The dependency runs this way only: the
     * upload store learns its conversation by being told, so injecting it back
     * would be a cycle.
     */
    _uploads: inject(UploadStore),
    /**
     * The first prompt of a conversation created somewhere else (US-906). Read once,
     * on the route binding that opens it, and cleared by the read.
     */
    _pending: inject(PendingPromptStore),
    _attachments: inject(PendingAttachmentsStore),
    _toasts: inject(ToastStore),
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
    /**
     * Cancels in-flight rating requests when the conversation changes (US-1103).
     *
     * Its own subject rather than `_stop$`: that one is the user's Stop, and a rating is
     * not part of the turn it would be cancelling. Without a cancel here, clearing the
     * pending ids on a route change would leave an orphaned request whose id is no longer
     * pending — so returning to the conversation and pressing again would open a second
     * request racing the first, which is the exact overlap `rateMessage`'s guard exists
     * to make impossible.
     */
    _rateCancel$: new Subject<void>(),
    /**
     * The turn is waiting on EP-8's upload gate: accepted, but with no stream open
     * yet. A second pre-stream window beside `creating`, and Stop has to settle it
     * the same way — there is nothing streaming for an abort to reach.
     */
    _awaitingUploads: { value: false },
  })),
  withComputed((store) => {
    const inFlight = computed(() => store.phase() !== 'idle');
    /**
     * Whether the model is reasoning in its own words, past the server's seed
     * (US-503). It is what hands the pre-answer gap from the ridgeline to the
     * reasoning region: US-406's rule is that the gap shows a ridgeline and
     * never a fabricated status line, and the model's own reasoning is neither
     * fabricated nor a status line — but the seeded delta every turn opens with
     * *is*, which is why this reads past it.
     */
    const modelReasoning = computed(() => hasModelReasoning(store.reasoning()));
    /**
     * The folded `Finished` (US-1402). Distinct from `!inFlight()`, which is one
     * step later: the fold sets `snapshot.phase` the moment the frame lands,
     * while the settle that clears `phase` waits for the response body to close.
     * `aria-busy` names the frame, not the body, so the deferred live region is
     * released while the live turn is still mounted to be read.
     *
     * **What this trusts is that nothing renderable follows `Finished`** — not
     * that it terminates the stream, which it explicitly does not: the contract
     * is blunt that "`Finished` is not a terminator … end-of-body is the
     * terminator" (streaming contract §4.1), which is exactly why `inFlight`
     * stays true past it and the settle waits for the body.
     *
     * The fold does not enforce even the weaker claim: it keeps appending
     * `TextDelta` text to a `Completed` snapshot. A server that framed one after
     * the other would leave the answer region live and no longer busy, and every
     * later delta would be announced on its own. The alternative is to drop those
     * deltas, which would lose answer text to protect an announcement; a chatty
     * reading is the better failure of the two.
     */
    const answerComplete = computed(() => store.snapshot.phase() === 'Completed');

    return {
      inFlight,
      answerComplete,
      /**
       * What the composer's project control acts on (US-307).
       *
       * **The list copy wins, with the detail DTO behind it** — the same precedence
       * `ConversationStore.isFavorite` uses, and the reverse of its `current`. The list
       * row is the one `moveRow` patches optimistically, so the pill flips on the click
       * rather than a round trip later; the detail DTO is the fallback for a conversation
       * the 50-row sidebar never held, which is every deep link past its first page and
       * every row opened from the library. Reading only the list would leave the pill
       * permanently absent there, while the header kebab beside it offered the move.
       *
       * Null before a conversation exists, and null for the one round trip a deep link
       * spends with neither copy in hand — the rule the header kebab already follows,
       * because `toUpdateBody` echoes `projectId` and acting on a guess would move the
       * conversation somewhere nobody asked for.
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
       * What the download control acts on (US-1502).
       *
       * Non-null only once the transcript holds something the server has persisted, which
       * is what makes the control absent on an empty conversation rather than offering a
       * file with one heading in it. `entries` is the right test and `hasContent` is not:
       * that computed is deliberately true while history is merely *pending*, and while a
       * pre-stream notice is on screen, neither of which is anything to export.
       *
       * The name follows the same list-then-detail precedence as `projectTarget`, so a
       * conversation the sidebar never held still names itself.
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
      /** Frame `1e`: the ridgeline between request-accepted and something to read. */
      showThinking: computed(
        () =>
          store.phase() === 'creating' || (store.phase() === 'awaitingFirst' && !modelReasoning()),
      ),
      /**
       * The live assistant turn region — from the model's first reasoning delta,
       * so the summary is readable *as it streams* rather than appearing whole at
       * the first token; still rendered while `stopping`, until the settle lands.
       */
      showLiveTurn: computed(
        () =>
          store.phase() === 'streaming' ||
          store.phase() === 'stopping' ||
          (store.phase() === 'awaitingFirst' && modelReasoning()),
      ),
      /** The live turn's reasoning, seed removed, for frame `1f`'s region (US-503). */
      liveReasoningText: computed(() =>
        modelReasoningText(store.reasoning(), store.snapshot().reasoningText ?? ''),
      ),
      /** The live turn's reasoning duration for frame `1f`'s pill (US-503). */
      liveReasoningSeconds: computed(() => reasoningSeconds(store.reasoning())),
      /**
       * What a screen reader is told while the turn runs (US-1402).
       *
       * Rendered by `Chat` **outside** the transcript, because the transcript is
       * `aria-busy` for the length of a turn and a live region inside it is
       * deferred along with the answer. Empty at rest, so the region has
       * something to transition *from* on the next turn.
       *
       * `turnAnnouncement` is pure and lives in `domain/stream/`; it changes value
       * only on a status transition, never on a `TextDelta`, which is US-1402's
       * third criterion satisfied by construction rather than by a timer.
       *
       * **`Answer ready` is the deliberate exception, and it is a backstop.**
       * Releasing the answer's own live region is an `aria-busy` change with no
       * accompanying content mutation, so it rests on assistive tech having
       * buffered what it suppressed and replaying it — the spec's intent, but the
       * least reliably implemented corner of the live-region model. A reader
       * whose AT suppressed and forgot would otherwise meet exactly the silence
       * this story exists to remove. Four syllables beside the answer is the
       * cheaper of the two failures.
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
      /**
       * Whether the transcript replaces the empty state. The history states
       * count: a conversation being read back must not show the landing
       * screen's prompt chips for the length of a round trip and then swap
       * them for its own transcript.
       */
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
    /**
     * Reads back the stored transcript for a conversation (US-410).
     *
     * `switchMap` is the whole race guard: every call site — a route change,
     * the panel's Retry — goes through here, so a read for a conversation the
     * user has left is cancelled before it can install entries that belong to
     * a screen they are no longer on.
     */
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
                  // Synchronously, not via the route: files attached before this
                  // conversation existed have been waiting for an id, and the
                  // gate below is about to wait for them. Chat's route effect
                  // calls the same method with the same id a pass later, where
                  // it is a no-op.
                  store._uploads.bindConversation(created.id);
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
          // EP-8: attachments finish ingesting before the stream opens, so the
          // answer is grounded in the file the prompt is about — the retrieval
          // tool is attached from what the conversation *already* holds, and a
          // document still being embedded is not in it yet. A turn with no
          // attachments takes the synchronous path and costs nothing.
          //
          // Stop, sign-out and a route change all end the wait, and none of them
          // opens a stream: the inner observable completes without emitting, which
          // completes the whole turn pipeline and frees `exhaustMap`'s slot for the
          // next send. `stop()` settles the turn state itself, exactly as it does
          // for the other pre-stream window; the other two reset the store outright.
          switchMap((conversationId) => {
            store._awaitingUploads.value = true;
            // Here rather than on attach: the composer holds its files until the turn they
            // belong to is actually sent, and the gate below then waits for them.
            store._uploads.startPending();

            return store._uploads.settled$().pipe(
              // Cancelled, the inner observable completes **without emitting**, so
              // the whole turn pipeline completes and `exhaustMap` frees its slot
              // for the next send. Emitting anyway would open a stream nobody wants
              // and leave the slot held for as long as that read took to fail.
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
                  // `performance.now()`, not `Date.now()`: this reading is one
                  // end of an elapsed measurement, and a clock step between the
                  // two ends would render a negative duration.
                  next: (batch) => patchState(store, applyBatch(batch, performance.now())),
                  // The client's contract: every error it emits is an AppError.
                  error: (error: AppError) => patchState(store, settleFromError(command, error)),
                  complete: () => {
                    // Read before the settle, which appends the entry that
                    // would make this turn look like it had a predecessor.
                    // An unread history is not an empty one: claiming first
                    // there would refetch a name the server settled long ago.
                    const completed = store.snapshot().phase === 'Completed';
                    const wasFirstTurn =
                      store.historyError() === null &&
                      !store.entries().some((entry) => entry.kind === 'assistant');

                    patchState(store, settleFromComplete(command));

                    // Only a `Finished` frame counts: a stopped or cut-off turn
                    // left the server with nothing to name the conversation
                    // after, so announcing it would send observers to read back
                    // the placeholder they already hold (US-409).
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

    /**
     * Records, replaces or withdraws a rating on one settled answer (US-1103).
     *
     * `mergeMap`: ratings on different answers are independent and may overlap. Same-id
     * overlap cannot happen, because `rateMessage` refuses while that message's id is
     * pending — the same guard the conversation favourite toggle uses, and the reason
     * this can be a merge rather than a switch.
     */
    const rate = rxMethod<{
      conversationId: string;
      messageId: string;
      rating: MessageFeedbackRating | null;
      previous: MessageFeedbackRating | null;
    }>(
      mergeMap(({ conversationId, messageId, rating, previous }) => {
        // Both writes sit in the projection, which rxMethod runs synchronously with the
        // call: that is what lets rateMessage's guard see the pending id on a
        // double-click's second call, and it keeps the optimistic patch inseparable from
        // the request that resolves it.
        patchState(store, addPendingId(messageId), rateEntry(messageId, rating));

        // Carried in rather than read back off the store: the conversation this rating
        // belongs to is the one that was open when it was pressed, and a route change
        // landing mid-request must not redirect it at a conversation the reader moved to.
        const url = store._api.build(
          `conversations/${ApiUrl.segment(conversationId)}/messages/${ApiUrl.segment(messageId)}/feedback`,
        );

        // A set, not a toggle: the body carries the state being asked for, so a retried
        // or duplicated request cannot land on the opposite value — which is also how the
        // server keeps a second submission from creating a second rating.
        return store._http.post<void>(url, { rating }).pipe(
          takeUntil(merge(store._signedOut$, store._rateCancel$)),
          tapResponse({
            next: () =>
              // Re-asserted rather than refreshed: the 204 carries no body to adopt, so
              // this is the same value again — idempotent when nothing intervened, and
              // the repair when a history reload landed in between.
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
       * Rates one settled answer, or withdraws the rating already on it (US-1103).
       *
       * Pressing the thumb that is already selected is the withdrawal: the control
       * reports which thumb was pressed and this decides what that means, because only
       * the store knows what is currently stored.
       *
       * Refused while that message's own request is in flight, so a double-click cannot
       * open a second request whose completion order would decide the stored value.
       * Refused too when the message is not a rateable entry of this transcript — a
       * conversation the screen has left, or an id with no anchor.
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

      /**
       * Stops the turn (US-407). During `creating` the POST is cancelled and
       * the prompt goes straight back to the composer — nothing streamed, so
       * there is no partial output to card. In flight, the phase flips to
       * `stopping` *before* the abort so the settle can tell Stop from
       * sign-out, and the send control returns on this synchronous patch.
       */
      stop(): void {
        const phase = store.phase();
        // The two pre-stream windows settle identically, because in both nothing
        // has streamed: there is no partial output to card, and the prompt goes
        // straight back to the composer. An abort would have nothing to reach.
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
       * Re-reads the stored transcript for whatever is open. The history
       * panel's Retry — which discards the live entries, because a turn taken
       * while history was broken is in the response this is about to install.
       */
      retryHistory(): void {
        loadHistory({ id: store.boundConversationId(), keepLive: false });
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
        // Also tears down an EP-8 upload gate the abandoned turn may still be sitting
        // in. Aborting alone would not: the stream has not been opened yet, so there
        // is no signal for it to reach, and `exhaustMap` would drop every later send
        // while the orphaned wait held the slot.
        store._stop$.next();
        store._turnAbort.current = null;
        // Ratings are cancelled rather than merely forgotten (US-1103): an orphaned
        // request whose pending id has been cleared is one the re-entry guard can no
        // longer see, so returning to this conversation would let a second request race
        // it. `finalize` clears the id as the cancellation unsubscribes; the explicit
        // clear below is what keeps the reset total whatever happened before it, since
        // `initialState` is the TurnState literal and carries no pendingIds key.
        store._rateCancel$.next();
        patchState(store, () => ({ ...initialState, boundConversationId: id }), clearPendingIds());
        // Replay hangs off the reset rather than off the route input directly,
        // so US-401's own `replaceUrl` — which the guard above returns early
        // from — cannot trigger a read of a conversation whose first turn is
        // still streaming and therefore not persisted yet.
        loadHistory({ id, keepLive: true });

        // A conversation created on the project screen arrives with its files waiting
        // too. Claimed here rather than from an effect on the route input, because the
        // gate below is evaluated in this same call stack: an attach one change-detection
        // pass later would find `settled$()` already answered, open the stream, and
        // ground the answer in nothing. Binding precedes attaching — reversed, the target
        // change clears the chips just added.
        const handedOver = id === null ? [] : store._attachments.claim(id);
        if (id !== null && handedOver.length > 0) {
          store._uploads.bindConversation(id);
          store._uploads.attach(handedOver);
        } else {
          // Landing anywhere else — including the bare chat route — means that handoff was
          // abandoned: a refused guard, a reader who clicked away mid-navigation. Held
          // `File` handles are bytes, not a string like the prompt beside them, so they are
          // released rather than left in root state until sign-out.
          store._attachments.clear();
        }

        // The first prompt, waiting the same way. Claimed after the reset, so the send
        // lands on the binding it belongs to, and single-shot inside the store, so a
        // refresh of the same URL does not send the prompt twice.
        const pending = id === null ? null : store._pending.claim(id);
        const pendingText = pending?.trim() ?? '';
        if (pendingText !== '') {
          const selection = store._settings.streamSelection();
          if (selection === null) {
            // No model to send with — the catalogue failed, or the reader landed
            // here before it resolved. The prompt goes to the composer rather than
            // being dropped, so the work is still theirs to send.
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
