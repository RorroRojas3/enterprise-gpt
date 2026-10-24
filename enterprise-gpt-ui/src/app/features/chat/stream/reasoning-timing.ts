import { AssistantUiEvent } from '@shared/models/andes/assistant-ui.contract';

/**
 * The first `ReasoningDelta` is server-seeded and skipped by position, not text. The wire carries
 * no reasoning duration, so it is timed from the model's first delta, excluding stream latency.
 */
export interface ReasoningTiming {
  /** Length of the server's seeded delta, or null before it has arrived. */
  readonly seedLength: number | null;
  /** When the model's first reasoning delta arrived, or null before one has. */
  readonly startedAt: number | null;
  /** When the first renderable content arrived after it, or null while none has. */
  readonly endedAt: number | null;
}

export function createInitialReasoningTiming(): ReasoningTiming {
  return { seedLength: null, startedAt: null, endedAt: null };
}

/**
 * Reasoning ends at the first renderable content, since a model resuming mid-answer has already
 * answered. `Finished` closes an open window, so a reasoning-only turn still reports.
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

/** The accumulated reasoning minus the server's seed; empty until the model reasons. */
export function modelReasoningText(timing: ReasoningTiming, reasoningText: string): string {
  return timing.startedAt === null ? '' : reasoningText.slice(timing.seedLength ?? 0);
}

/** Seconds reasoned, or null while open or never opened: no reasoning is not zero seconds. */
export function reasoningSeconds(timing: ReasoningTiming): number | null {
  const { startedAt, endedAt } = timing;

  return startedAt === null || endedAt === null ? null : (endedAt - startedAt) / 1000;
}
