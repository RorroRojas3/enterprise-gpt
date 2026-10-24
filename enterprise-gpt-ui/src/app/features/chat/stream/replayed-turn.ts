import {
  AssistantStatusSnapshot,
  createInitialSnapshot,
  foldAssistantEvents,
} from '@core/api/assistant-ui-contract';
import { textDeltaEvent } from './stream-codec';
import { TurnTimeline, createInitialTimeline, foldTimeline } from './turn-timeline';

/** A past assistant answer in the shape the transcript renders live turns from. */
export interface ReplayedTurn {
  readonly snapshot: AssistantStatusSnapshot;
  readonly timeline: TurnTimeline;
}

/**
 * Folds stored text through the live reducers so a replayed answer shares the live render path. A
 * stored message holds only text, so there are no activities or usage and `phase` stays initial.
 */
export function replayAssistantText(text: string): ReplayedTurn {
  const event = textDeltaEvent(text);

  return {
    snapshot: foldAssistantEvents(createInitialSnapshot(), event),
    timeline: foldTimeline(createInitialTimeline(), event),
  };
}
