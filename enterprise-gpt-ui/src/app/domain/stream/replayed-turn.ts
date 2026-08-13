import {
  AssistantStatusSnapshot,
  createInitialSnapshot,
  foldAssistantEvents,
} from './andes/assistant-ui.contract';
import { TurnTimeline, createInitialTimeline, foldTimeline } from './turn-timeline';
import { textDeltaEvent } from './stream-codec';

/** A past assistant answer in the shape the transcript renders live turns from. */
export interface ReplayedTurn {
  readonly snapshot: AssistantStatusSnapshot;
  readonly timeline: TurnTimeline;
}

/**
 * Rebuilds the folded state of an assistant message read back from
 * `GET api/conversations/{id}/messages` (US-410).
 *
 * The text is pushed through the same two reducers a live turn uses rather
 * than hand-assembling a snapshot: a replayed answer then renders through the
 * existing transcript components with no second code path, and cannot drift
 * from the live one when the vendored contract is upgraded.
 *
 * The result carries no activities and no `Finished` usage, which is honest —
 * the stream is live-only (streaming contract §6.2) and the persisted message
 * genuinely holds nothing but text. It is left at the initial `phase` for the
 * same reason: nothing about a stored message says how its turn ended.
 *
 * @param text The assistant message text.
 */
export function replayAssistantText(text: string): ReplayedTurn {
  const event = textDeltaEvent(text);

  return {
    snapshot: foldAssistantEvents(createInitialSnapshot(), event),
    timeline: foldTimeline(createInitialTimeline(), event),
  };
}
