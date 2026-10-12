import { AssistantUiEvent } from '@shared/models/andes/assistant-ui.contract';

/**
 * A `text` node is a `[start, end)` slice of the snapshot's `text`, never a copy, so a delta costs
 * O(1). An `activity` node references a `scopeId` in the snapshot's activity tree.
 */
export type TimelineNode =
  | { readonly kind: 'text'; readonly start: number; readonly end: number }
  | { readonly kind: 'activity'; readonly scopeId: string; readonly parentScopeId?: string };

/** Records only the text/activity interleaving, which the fold's hierarchical snapshot loses. */
export interface TurnTimeline {
  readonly nodes: readonly TimelineNode[];
  /** Total answer text folded so far; the next text block opens here. */
  readonly textLength: number;
}

export function createInitialTimeline(): TurnTimeline {
  return { nodes: [], textLength: 0 };
}

/**
 * Every `ActivityStarted` becomes a node at any depth; the renderer keeps the snapshot's roots and
 * leaves the rest to their parent cards. Returns the same reference for an event it ignores.
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
      // Mirrors the fold's own scopeId fallback, so the render-time join cannot miss.
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
