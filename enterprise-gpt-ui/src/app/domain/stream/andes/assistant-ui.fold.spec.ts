import { describe, expect, it } from 'vitest';
import { assistantEvent, fullTurnEvents } from '@testing/stream-frames';
import {
  AssistantStatusSnapshot,
  AssistantUiEvent,
  createInitialSnapshot,
  foldAssistantEvents,
} from './assistant-ui.contract';

/**
 * Fold semantics for the vendored reducer, delegated here by the vendoring
 * spec. These exercise the package's `foldAssistantEvents` — never a
 * reimplementation — against recorded fixtures: the PRD's acceptance threshold
 * is all eight kinds folded correctly plus an unknown kind ignored.
 */
describe('foldAssistantEvents', () => {
  function fold(events: Parameters<typeof foldAssistantEvents>[1][]): AssistantStatusSnapshot {
    return events.reduce(foldAssistantEvents, createInitialSnapshot());
  }

  it('sets the request status line from Status', () => {
    const snapshot = fold([assistantEvent('Status', { message: 'Reasoning…' })]);

    expect(snapshot.assistantStatus).toBe('Reasoning…');
    expect(snapshot.phase).toBe('Running');
  });

  it('appends a root activity from ActivityStarted', () => {
    const snapshot = fold([assistantEvent('ActivityStarted', { scopeId: 'a1' })]);

    expect(snapshot.activities).toHaveLength(1);
    expect(snapshot.activities[0]).toMatchObject({
      scopeId: 'a1',
      displayName: 'Andes Test MCP',
      kind: 'McpTool',
      source: 'Weather',
      state: 'Running',
      subStatuses: [],
      children: [],
    });
  });

  it('nests an ActivityStarted under its parent scope at any depth', () => {
    const snapshot = fold([
      assistantEvent('ActivityStarted', { scopeId: 'a1' }),
      assistantEvent('ActivityStarted', { scopeId: 'a2', parentScopeId: 'a1' }),
      assistantEvent('ActivityStarted', { scopeId: 'a3', parentScopeId: 'a2' }),
    ]);

    expect(snapshot.activities).toHaveLength(1);
    expect(snapshot.activities[0]?.children[0]?.scopeId).toBe('a2');
    expect(snapshot.activities[0]?.children[0]?.children[0]?.scopeId).toBe('a3');
  });

  it('promotes an ActivityStarted with an unknown parent to a new root', () => {
    const snapshot = fold([
      assistantEvent('ActivityStarted', { scopeId: 'a1' }),
      assistantEvent('ActivityStarted', { scopeId: 'orphan', parentScopeId: 'never-seen' }),
    ]);

    expect(snapshot.activities.map((activity) => activity.scopeId)).toEqual(['a1', 'orphan']);
  });

  it('appends a sub-status from ActivityProgress and ignores an unknown scope', () => {
    const snapshot = fold([
      assistantEvent('ActivityStarted', { scopeId: 'a1' }),
      assistantEvent('ActivityProgress', {
        scopeId: 'a1',
        message: 'Step 1',
        progress: 1,
        progressTotal: 3,
      }),
      assistantEvent('ActivityProgress', { scopeId: 'never-seen', message: 'Lost' }),
    ]);

    expect(snapshot.activities[0]?.subStatuses).toEqual([
      { message: 'Step 1', progress: 1, progressTotal: 3 },
    ]);
    expect(snapshot.activities).toHaveLength(1);
  });

  it('completes and fails activities with their durations', () => {
    const snapshot = fold([
      assistantEvent('ActivityStarted', { scopeId: 'a1' }),
      assistantEvent('ActivityStarted', { scopeId: 'a2' }),
      assistantEvent('ActivityCompleted', { scopeId: 'a1', durationSeconds: 1.4 }),
      assistantEvent('ActivityFailed', { scopeId: 'a2', durationSeconds: 0.6 }),
    ]);

    expect(snapshot.activities[0]).toMatchObject({ state: 'Completed', durationSeconds: 1.4 });
    expect(snapshot.activities[1]).toMatchObject({ state: 'Failed', durationSeconds: 0.6 });
    // A failed activity does not fail the turn; only the events decide the phase.
    expect(snapshot.phase).toBe('Running');
  });

  it('accumulates TextDelta into text and ReasoningDelta into reasoningText, separately', () => {
    const snapshot = fold([
      assistantEvent('ReasoningDelta', { text: 'Consider ' }),
      assistantEvent('TextDelta', { text: 'Hello, ' }),
      assistantEvent('ReasoningDelta', { text: 'the weather.' }),
      assistantEvent('TextDelta', { text: 'world.' }),
    ]);

    expect(snapshot.text).toBe('Hello, world.');
    expect(snapshot.reasoningText).toBe('Consider the weather.');
  });

  it('marks the turn completed and records usage from Finished', () => {
    const snapshot = fold([
      assistantEvent('TextDelta'),
      assistantEvent('Finished', { usage: { inputTokens: 10, outputTokens: 20, totalTokens: 30 } }),
    ]);

    expect(snapshot.phase).toBe('Completed');
    expect(snapshot.usage).toEqual({ inputTokens: 10, outputTokens: 20, totalTokens: 30 });
  });

  it('returns the snapshot unchanged for an unknown kind', () => {
    const before = fold([assistantEvent('TextDelta')]);
    const unknown = {
      ...assistantEvent('Status'),
      kind: 'ToolPreamble',
    } as unknown as AssistantUiEvent;

    expect(foldAssistantEvents(before, unknown)).toBe(before);
  });

  it('never mutates the input snapshot', () => {
    const initial = createInitialSnapshot();
    const first = foldAssistantEvents(
      initial,
      assistantEvent('ActivityStarted', { scopeId: 'a1' }),
    );
    foldAssistantEvents(first, assistantEvent('ActivityCompleted', { scopeId: 'a1' }));

    expect(initial).toEqual({ phase: 'Running', activities: [] });
    expect(first.activities[0]?.state).toBe('Running');
  });

  it('folds a full recorded turn into the expected final snapshot', () => {
    const snapshot = fold(fullTurnEvents());

    expect(snapshot.phase).toBe('Completed');
    expect(snapshot.assistantStatus).toBe('Reasoning…');
    expect(snapshot.text).toBe('Hello, world.');
    expect(snapshot.reasoningText).toBe('The user asked about the weather. ');
    expect(snapshot.usage).toEqual({ inputTokens: 1842, outputTokens: 512, totalTokens: 2354 });

    const [mcp, fn] = snapshot.activities;
    expect(snapshot.activities).toHaveLength(2);
    expect(mcp).toMatchObject({ scopeId: 'mcp-1', state: 'Completed', kind: 'McpTool' });
    expect(mcp?.subStatuses).toHaveLength(1);
    expect(mcp?.children).toHaveLength(1);
    expect(mcp?.children[0]).toMatchObject({
      scopeId: 'agent-1',
      state: 'Completed',
      kind: 'Agent',
    });
    expect(fn).toMatchObject({ scopeId: 'fn-1', state: 'Failed', kind: 'Function' });
  });
});
