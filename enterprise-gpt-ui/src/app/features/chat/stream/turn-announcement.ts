/*
 * The transcript's live region is `aria-busy` during a turn, so this is all a reader hears. Each
 * branch changes only with turn state (text is read as a boolean), never once per delta.
 */
import {
  ActivityState,
  AssistantActivity,
  AssistantStatusSnapshot,
} from '@shared/models/andes/assistant-ui.contract';
import { activityLabel } from './activity-label';

export interface TurnAnnouncementContext {
  /** `POST api/conversations` is in flight for a first prompt. */
  readonly creating: boolean;
  /** The model is reasoning in its own words, past the server's seed. */
  readonly reasoning: boolean;
}

/** Words, not the cards' em dash, which some screen readers read out as "em dash". */
const STATE_PHRASE: Record<ActivityState, string> = {
  Running: 'is running',
  Completed: 'completed',
  Failed: 'failed',
};

/** `''` when there is nothing to say; that is also what clears the live region between turns. */
export function turnAnnouncement(
  snapshot: AssistantStatusSnapshot,
  context: TurnAnnouncementContext,
): string {
  if (context.creating) {
    return 'Starting the conversation';
  }

  const primary = describeProgress(snapshot, context);

  // Failures are appended as a count: ranking one would freeze or bury it, and the snapshot has
  // no order among settled states. Net of the headline's own, or two would sound like three.
  const failures = countFailed(snapshot.activities) - (primary.namesAFailure ? 1 : 0);
  if (failures === 0) {
    return primary.text;
  }

  const more = primary.namesAFailure ? 'more ' : '';

  return `${primary.text}. ${failures} ${more}step${failures === 1 ? '' : 's'} failed`;
}

/**
 * Follows the pre-order-last activity, so a settling child never re-announces its parent. Other
 * nodes' transitions go unspoken; failures still surface through {@link countFailed}.
 */
function describeProgress(
  snapshot: AssistantStatusSnapshot,
  context: TurnAnnouncementContext,
): { readonly text: string; readonly namesAFailure: boolean } {
  const latest = lastActivity(snapshot.activities);

  // An activity starting mid-reply is news; the reply's arrival was already announced.
  if (latest?.state === 'Running') {
    return { text: `${activityLabel(latest)} ${STATE_PHRASE.Running}`, namesAFailure: false };
  }

  // Ahead of a settled activity, so the answer replaces "… completed". Read as a boolean.
  if ((snapshot.text ?? '') !== '') {
    return { text: 'Writing the answer', namesAFailure: false };
  }

  // Settled with no text yet: without this, the gap before the answer would be silent.
  if (latest !== null) {
    return {
      text: `${activityLabel(latest)} ${STATE_PHRASE[latest.state]}`,
      namesAFailure: latest.state === 'Failed',
    };
  }

  if (context.reasoning) {
    return { text: 'Reasoning', namesAFailure: false };
  }

  return { text: 'Waiting for a response', namesAFailure: false };
}

function lastActivity(activities: readonly AssistantActivity[]): AssistantActivity | null {
  const last = activities[activities.length - 1];
  if (last === undefined) {
    return null;
  }

  return lastActivity(last.children) ?? last;
}

function countFailed(activities: readonly AssistantActivity[]): number {
  return activities.reduce(
    (total, activity) =>
      total + (activity.state === 'Failed' ? 1 : 0) + countFailed(activity.children),
    0,
  );
}
