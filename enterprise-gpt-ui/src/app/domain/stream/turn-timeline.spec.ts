import { describe, expect, it } from 'vitest';
import { assistantEvent, fullTurnEvents } from '@testing/stream-frames';
import {
  AssistantStatusSnapshot,
  createInitialSnapshot,
  foldAssistantEvents,
} from './andes/assistant-ui.contract';
import { TurnTimeline, createInitialTimeline, foldTimeline } from './turn-timeline';

/**
 * US-501: the ordering index beside the vendored fold. The fold owns activity
 * state; this structure owns only the order things arrived in — these specs
 * pin that split and the recorded-turn ordering criterion.
 */
describe('foldTimeline', () => {
  function foldBoth(events: ReturnType<typeof fullTurnEvents>): {
    snapshot: AssistantStatusSnapshot;
    timeline: TurnTimeline;
  } {
    return {
      snapshot: events.reduce(foldAssistantEvents, createInitialSnapshot()),
      timeline: events.reduce(foldTimeline, createInitialTimeline()),
    };
  }

  it('indexes the recorded full turn in exact arrival order', () => {
    const { timeline } = foldBoth(fullTurnEvents());

    // Every ActivityStarted — the nested agent-1 included — is its own node,
    // in the order it arrived; the two text deltas coalesce into one block.
    expect(timeline.nodes).toEqual([
      { kind: 'activity', scopeId: 'mcp-1', parentScopeId: undefined },
      { kind: 'activity', scopeId: 'agent-1', parentScopeId: 'mcp-1' },
      { kind: 'activity', scopeId: 'fn-1', parentScopeId: undefined },
      { kind: 'text', start: 0, end: 13 },
    ]);
    expect(timeline.textLength).toBe(13);
  });

  it('splits text into blocks around an activity, each slicing back to its own deltas', () => {
    const events = [
      assistantEvent('TextDelta', { text: 'Checking the forecast. ' }),
      assistantEvent('ActivityStarted', { scopeId: 'mcp-1' }),
      assistantEvent('TextDelta', { text: 'Sunny, ' }),
      assistantEvent('TextDelta', { text: 'with a light breeze.' }),
    ];
    const { snapshot, timeline } = foldBoth(events);

    expect(timeline.nodes).toHaveLength(3);
    const [head, activity, tail] = timeline.nodes;
    expect(activity).toEqual({ kind: 'activity', scopeId: 'mcp-1', parentScopeId: undefined });

    // The offsets are the join to the snapshot's single accumulated string —
    // the timeline holds no text of its own.
    const text = snapshot.text ?? '';
    expect(head).toEqual({ kind: 'text', start: 0, end: 23 });
    expect(text.slice(0, 23)).toBe('Checking the forecast. ');
    expect(tail).toEqual({ kind: 'text', start: 23, end: 50 });
    expect(text.slice(23, 50)).toBe('Sunny, with a light breeze.');
  });

  it('ignores events that carry no ordering information, returning the same reference', () => {
    const seeded = foldTimeline(
      createInitialTimeline(),
      assistantEvent('TextDelta', { text: 'Hello' }),
    );

    for (const kind of [
      'Status',
      'ActivityProgress',
      'ActivityCompleted',
      'ActivityFailed',
      'ReasoningDelta',
      'Finished',
    ] as const) {
      expect(foldTimeline(seeded, assistantEvent(kind, { scopeId: 'mcp-1' }))).toBe(seeded);
    }
  });

  it('ignores an empty text delta', () => {
    const timeline = createInitialTimeline();

    expect(foldTimeline(timeline, assistantEvent('TextDelta', { text: '' }))).toBe(timeline);
  });

  it('does not mutate the input timeline', () => {
    const before = foldTimeline(
      createInitialTimeline(),
      assistantEvent('TextDelta', { text: 'Hi' }),
    );
    const nodesBefore = [...before.nodes];

    foldTimeline(before, assistantEvent('TextDelta', { text: ' there' }));
    foldTimeline(before, assistantEvent('ActivityStarted', { scopeId: 'mcp-1' }));

    expect(before.nodes).toEqual(nodesBefore);
    expect(before.textLength).toBe(2);
  });
});
