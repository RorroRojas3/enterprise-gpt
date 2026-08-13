import { AssistantActivity, AssistantUiEvent } from './andes/assistant-ui.contract';

/**
 * One entry in a turn's arrival-order index (US-501).
 *
 * A `text` node is a half-open `[start, end)` slice into the folded snapshot's
 * single `text` string — the timeline never copies answer text, so a delta
 * costs O(1) here regardless of transcript length. An `activity` node is a
 * `scopeId` reference into the folded snapshot's activity tree, resolved at
 * render time with {@link findActivity}.
 */
export type TimelineNode =
  | { readonly kind: 'text'; readonly start: number; readonly end: number }
  | { readonly kind: 'activity'; readonly scopeId: string; readonly parentScopeId?: string };

/**
 * The arrival-order half of a streamed turn.
 *
 * `foldAssistantEvents` owns every piece of activity *state* — labels, kinds,
 * progress, completion — but its snapshot is hierarchical and unordered across
 * text: it cannot say whether a card arrived before or after a paragraph. This
 * structure records only that interleaving, which is why it holds offsets and
 * scope ids rather than text or activities: neither structure duplicates the
 * other, per US-501.
 */
export interface TurnTimeline {
  readonly nodes: readonly TimelineNode[];
  /** Total answer text folded so far; the next text block opens here. */
  readonly textLength: number;
}

/** Returns the empty timeline to fold events into, mirroring `createInitialSnapshot`. */
export function createInitialTimeline(): TurnTimeline {
  return { nodes: [], textLength: 0 };
}

/**
 * Folds one event into the arrival-order index. Pure; returns the same
 * reference when the event carries no ordering information.
 *
 * Every `ActivityStarted` becomes its own node regardless of depth — US-501
 * renders nested work as separate chronological cards. `parentScopeId` is
 * recorded now so US-502 can drop child nodes and render them inside their
 * parent's card without touching this reducer.
 */
export function foldTimeline(timeline: TurnTimeline, event: AssistantUiEvent): TurnTimeline {
  switch (event.kind) {
    case 'TextDelta': {
      // `?? ''` matches the fold exactly, so offsets always index `snapshot.text`.
      const length = (event.text ?? '').length;
      if (length === 0) {
        return timeline;
      }

      const end = timeline.textLength + length;
      const last = timeline.nodes[timeline.nodes.length - 1];
      const nodes: readonly TimelineNode[] =
        last !== undefined && last.kind === 'text'
          ? [...timeline.nodes.slice(0, -1), { kind: 'text', start: last.start, end }]
          : [...timeline.nodes, { kind: 'text', start: timeline.textLength, end }];

      return { nodes, textLength: end };
    }

    case 'ActivityStarted': {
      // `?? ''` mirrors the fold's own scopeId fallback, so the render-time
      // join cannot miss on an event the fold accepted.
      const node: TimelineNode = {
        kind: 'activity',
        scopeId: event.scopeId ?? '',
        parentScopeId: event.parentScopeId,
      };

      return { ...timeline, nodes: [...timeline.nodes, node] };
    }

    default:
      return timeline;
  }
}

/**
 * Resolves a timeline node's `scopeId` against the folded snapshot's activity
 * tree, at any depth. Null when the scope is unknown — a defensive case only,
 * since both structures are fed the same events.
 */
export function findActivity(
  activities: readonly AssistantActivity[],
  scopeId: string,
): AssistantActivity | null {
  for (const activity of activities) {
    if (activity.scopeId === scopeId) {
      return activity;
    }

    const nested = findActivity(activity.children, scopeId);
    if (nested !== null) {
      return nested;
    }
  }

  return null;
}
