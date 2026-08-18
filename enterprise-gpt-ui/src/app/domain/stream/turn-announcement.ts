/*
 * What a screen reader is told while a turn runs (US-1402).
 *
 * The turn's own live region is deferred by `aria-busy` for the length of the
 * turn — that is what keeps a polite region over streaming text from reading
 * every delta — so nothing inside the transcript can speak while the answer
 * arrives. This string is rendered *outside* that container and is the only
 * thing a reader hears between Send and the answer.
 *
 * The criterion it satisfies is "status changes are announced, individual text
 * deltas are not", and it satisfies it **by construction rather than by
 * throttling**: every branch below is a fact that changes only when the turn's
 * state changes. The one branch that reads `text` reads it as a boolean, so a
 * thousand deltas produce one transition — empty to non-empty — and the binding
 * writes once.
 *
 * Nothing here is a fabricated model status. US-406 refuses to render a status
 * line the model did not send, and that still holds: what this reports is the
 * client's own knowledge — a request is out, an activity is running, text has
 * started — never an invented account of what the model is thinking. The one
 * exception proves the rule: "Reasoning" is announced only past the server's
 * seeded delta, on the same `hasModelReasoning` test the visible region uses.
 */
import {
  ActivityState,
  AssistantActivity,
  AssistantStatusSnapshot,
} from './andes/assistant-ui.contract';

export interface TurnAnnouncementContext {
  /** `POST api/conversations` is in flight for a first prompt. */
  readonly creating: boolean;
  /** The model is reasoning in its own words, past the server's seed (US-503). */
  readonly reasoning: boolean;
}

/**
 * How each state is spoken.
 *
 * Words rather than the em dash the visible cards use: at a verbose punctuation
 * level some screen readers read "—" out as "em dash", and this string has no
 * reader but a live region.
 */
const STATE_PHRASE: Record<ActivityState, string> = {
  Running: 'is running',
  Completed: 'completed',
  Failed: 'failed',
};

/**
 * Derives the in-flight announcement for a folded snapshot. Pure, so it tests in
 * Node beside the other reducers.
 *
 * Returns `''` when there is nothing to say, which is also what empties the live
 * region between turns — a region whose content never changes announces nothing,
 * so the empty string is load-bearing rather than a default.
 */
export function turnAnnouncement(
  snapshot: AssistantStatusSnapshot,
  context: TurnAnnouncementContext,
): string {
  if (context.creating) {
    return 'Starting the conversation';
  }

  const primary = describeProgress(snapshot, context);

  // A failure is the one status change that must never be lost, and ranking it
  // against the others loses it either way: put it first and the region freezes
  // on it for the rest of the turn, put it last and a later activity — or the
  // answer starting — buries it. So it is additive instead. It is a count rather
  // than a name because the snapshot records no order among terminal states, and
  // a count is the one thing that can be derived honestly.
  //
  // Net of the one the headline already named, or two failures where the newest
  // is one of them would be heard as three.
  const failures = countFailed(snapshot.activities) - (primary.namesAFailure ? 1 : 0);
  if (failures === 0) {
    return primary.text;
  }

  const more = primary.namesAFailure ? 'more ' : '';

  return `${primary.text}. ${failures} ${more}step${failures === 1 ? '' : 's'} failed`;
}

/**
 * The turn's headline: the pre-order-last activity, else the answer, else the wait.
 *
 * **The pre-order-last activity, not the innermost running one.** Both orderings
 * announce every start; only this one avoids announcing the parent a second time
 * when a nested child settles inside it, which is the same string twice with one
 * thing between and no new information in it.
 *
 * Be precise about the cost, because the next reader will decide from this
 * whether their case is covered: **only the pre-order-last node's transitions
 * are announced at all.** A parent is never that node once it has a child, so
 * its completion is silent whether or not a later sibling exists; an earlier
 * root's completion is silent once a later one exists. That is deliberate — the
 * alternative repeats strings — and it is safe only because the two transitions
 * worth guaranteeing survive it another way: a failure at any depth is carried
 * by {@link countFailed}, and the answer's arrival takes the headline itself.
 */
function describeProgress(
  snapshot: AssistantStatusSnapshot,
  context: TurnAnnouncementContext,
): { readonly text: string; readonly namesAFailure: boolean } {
  const latest = lastActivity(snapshot.activities);

  // What is happening *now* outranks the answer: an activity that starts partway
  // through a reply is news, and the reply's own arrival was announced already.
  if (latest?.state === 'Running') {
    return { text: `${latest.displayName} ${STATE_PHRASE.Running}`, namesAFailure: false };
  }

  // Ahead of the settled activity below, so a completed tool call gives way to
  // the answer rather than leaving "… completed" standing for the rest of the
  // turn. Read as a boolean: one transition, not one per delta.
  if ((snapshot.text ?? '') !== '') {
    return { text: 'Writing the answer', namesAFailure: false };
  }

  // Everything has settled and no text has arrived yet — the gap between a tool
  // call finishing and the answer starting, which is otherwise silence.
  if (latest !== null) {
    return {
      text: `${latest.displayName} ${STATE_PHRASE[latest.state]}`,
      namesAFailure: latest.state === 'Failed',
    };
  }

  if (context.reasoning) {
    return { text: 'Reasoning', namesAFailure: false };
  }

  return { text: 'Waiting for a response', namesAFailure: false };
}

/** The last activity in pre-order — the most recent one to have reported anything. */
function lastActivity(activities: readonly AssistantActivity[]): AssistantActivity | null {
  const last = activities[activities.length - 1];
  if (last === undefined) {
    return null;
  }

  return lastActivity(last.children) ?? last;
}

/** How many activities in the tree failed, at any depth. */
function countFailed(activities: readonly AssistantActivity[]): number {
  return activities.reduce(
    (total, activity) =>
      total + (activity.state === 'Failed' ? 1 : 0) + countFailed(activity.children),
    0,
  );
}
