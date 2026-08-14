import { AssistantUiEvent } from './andes/assistant-ui.contract';

/**
 * What a turn's reasoning stream amounts to, beside the vendored fold (US-503).
 *
 * Two things have to be tracked here rather than read off the snapshot.
 *
 * **Where the model's own reasoning starts.** Every turn's *first*
 * `ReasoningDelta` is server-authored — the service seeds
 * `"Reviewing the request and preparing a response.\n\n"` ahead of the model's
 * first event (streaming contract §4.1, §4.3) — and the vendored fold
 * accumulates it into `reasoningText` like any other. This app already refuses
 * to render a request-level status line it did not get from the model (US-406's
 * ridgeline), so the seed is measured out by length and left off the screen.
 * Identifying it by *position* rather than by its text is what keeps the client
 * from hard-coding the server's copy.
 *
 * **How long the model reasoned.** The wire carries no such figure:
 * `ReasoningDelta` leaves `timestamp` at its default (§4.2) and the only
 * `durationSeconds` on `Finished` is the whole turn's, which the fold discards.
 * So it is measured from arrival times — which is what a reader is watching
 * anyway — and measured from the model's first delta, not the seed's, or it
 * would report network and queue latency as thinking time.
 */
export interface ReasoningTiming {
  /**
   * The length of the server's seeded delta, or null before it has arrived.
   * `reasoningText.slice(seedLength)` is the model's own reasoning.
   */
  readonly seedLength: number | null;
  /** When the model's first reasoning delta arrived, or null before one has. */
  readonly startedAt: number | null;
  /** When the first renderable content arrived after it, or null while none has. */
  readonly endedAt: number | null;
}

/** The empty timing to fold into, mirroring `createInitialSnapshot`. */
export function createInitialReasoningTiming(): ReasoningTiming {
  return { seedLength: null, startedAt: null, endedAt: null };
}

/**
 * Folds one event into the timing. Pure — the clock is a parameter, so this
 * tests in Node beside the other reducers and a spec can pin a turn's seconds
 * without owning a fake clock.
 *
 * Reasoning **ends at the first renderable content**, not at the last
 * `ReasoningDelta`: what the pill reports is how long the model thought before
 * it started answering, and a model that resumes reasoning mid-answer has
 * already answered. `Finished` closes an open window, so a turn that reasoned
 * and then produced nothing still reports rather than losing its only figure.
 *
 * Returns the same reference when the event carries no timing information.
 */
export function foldReasoningTiming(
  timing: ReasoningTiming,
  event: AssistantUiEvent,
  nowMs: number,
): ReasoningTiming {
  if (event.kind === 'ReasoningDelta') {
    if (timing.seedLength === null) {
      return { seedLength: (event.text ?? '').length, startedAt: null, endedAt: null };
    }

    return timing.startedAt === null ? { ...timing, startedAt: nowMs } : timing;
  }

  const ends =
    event.kind === 'TextDelta' || event.kind === 'ActivityStarted' || event.kind === 'Finished';
  if (ends && timing.startedAt !== null && timing.endedAt === null) {
    return { ...timing, endedAt: nowMs };
  }

  return timing;
}

/** Whether the model streamed reasoning of its own, past the server's seed. */
export function hasModelReasoning(timing: ReasoningTiming): boolean {
  return timing.startedAt !== null;
}

/**
 * The model's own reasoning summary — the accumulated text with the server's
 * seeded first delta measured off the front. Empty until the model reasons.
 */
export function modelReasoningText(timing: ReasoningTiming, reasoningText: string): string {
  return timing.startedAt === null ? '' : reasoningText.slice(timing.seedLength ?? 0);
}

/**
 * The elapsed reasoning time in seconds, or null while the window is still
 * open or was never opened — a turn whose only reasoning was the seed claims
 * nothing, for the same reason an unreported token count is not a zero.
 */
export function reasoningSeconds(timing: ReasoningTiming): number | null {
  const { startedAt, endedAt } = timing;

  return startedAt === null || endedAt === null ? null : (endedAt - startedAt) / 1000;
}
