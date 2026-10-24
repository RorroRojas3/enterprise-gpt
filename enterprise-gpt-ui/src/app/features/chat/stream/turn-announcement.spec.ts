import { describe, expect, it } from 'vitest';
import {
  AssistantUiEvent,
  createInitialSnapshot,
  foldAssistantEvents,
} from '@core/api/assistant-ui-contract';
import { assistantEvent, fullTurnEvents } from '@testing/stream-frames';
import { turnAnnouncement } from './turn-announcement';

/** Folds real events to pin that status changes are announced and individual deltas are not. */
describe('turnAnnouncement', () => {
  function fold(events: readonly AssistantUiEvent[]) {
    return events.reduce(foldAssistantEvents, createInitialSnapshot());
  }

  const idle = { creating: false, reasoning: false };

  it('names the create round trip before any stream exists', () => {
    expect(turnAnnouncement(createInitialSnapshot(), { creating: true, reasoning: false })).toBe(
      'Starting the conversation',
    );
  });

  it('says a response is awaited when nothing has arrived', () => {
    expect(turnAnnouncement(createInitialSnapshot(), idle)).toBe('Waiting for a response');
  });

  it('announces the model reasoning only past the seed, which the caller decides', () => {
    const snapshot = fold([assistantEvent('ReasoningDelta')]);

    expect(turnAnnouncement(snapshot, idle)).toBe('Waiting for a response');
    expect(turnAnnouncement(snapshot, { creating: false, reasoning: true })).toBe('Reasoning');
  });

  it('names the running activity', () => {
    const snapshot = fold([assistantEvent('ActivityStarted', { scopeId: 'mcp-1' })]);

    expect(turnAnnouncement(snapshot, idle)).toBe('Get forecast is running');
  });

  it('follows the newest activity into a nested child', () => {
    const snapshot = fold([
      assistantEvent('ActivityStarted', { scopeId: 'mcp-1' }),
      assistantEvent('ActivityStarted', {
        scopeId: 'agent-1',
        parentScopeId: 'mcp-1',
        displayName: 'Forecast Agent',
        toolKind: 'Agent',
        depth: 2,
      }),
    ]);

    expect(turnAnnouncement(snapshot, idle)).toBe('Forecast Agent is running');
  });

  it('reports the child settling rather than handing back to the parent it repeats', () => {
    const snapshot = fold([
      assistantEvent('ActivityStarted', { scopeId: 'mcp-1' }),
      assistantEvent('ActivityStarted', {
        scopeId: 'agent-1',
        parentScopeId: 'mcp-1',
        displayName: 'Forecast Agent',
        toolKind: 'Agent',
        depth: 2,
      }),
      assistantEvent('ActivityCompleted', { scopeId: 'agent-1', toolKind: 'Agent', depth: 2 }),
    ]);

    // "Get forecast is running" again would repeat a string with nothing new in it.
    expect(turnAnnouncement(snapshot, idle)).toBe('Forecast Agent completed');
  });

  it('reports a settled activity while there is still no answer text', () => {
    const completed = fold([
      assistantEvent('ActivityStarted', { scopeId: 'mcp-1' }),
      assistantEvent('ActivityCompleted', { scopeId: 'mcp-1' }),
    ]);
    const failed = fold([
      assistantEvent('ActivityStarted', { scopeId: 'fn-1', displayName: 'Search Documents' }),
      assistantEvent('ActivityFailed', { scopeId: 'fn-1' }),
    ]);

    expect(turnAnnouncement(completed, idle)).toBe('Get forecast completed');
    // Named, and the count clause suppressed rather than saying "failed" twice.
    expect(turnAnnouncement(failed, idle)).toBe('Search Documents failed');
  });

  it('gives way to the answer rather than leaving a completed tool call standing', () => {
    const snapshot = fold([
      assistantEvent('ActivityStarted', { scopeId: 'mcp-1' }),
      assistantEvent('ActivityCompleted', { scopeId: 'mcp-1' }),
      assistantEvent('TextDelta', { text: 'The forecast is' }),
    ]);

    expect(turnAnnouncement(snapshot, idle)).toBe('Writing the answer');
  });

  it('announces an activity that starts partway through the answer', () => {
    const snapshot = fold([
      assistantEvent('TextDelta', { text: 'Let me check. ' }),
      assistantEvent('ActivityStarted', { scopeId: 'mcp-1' }),
    ]);

    expect(turnAnnouncement(snapshot, idle)).toBe('Get forecast is running');
  });

  it('carries a failure past the point the answer starts arriving', () => {
    const snapshot = fold([
      assistantEvent('ActivityStarted', { scopeId: 'fn-1', displayName: 'Search Documents' }),
      assistantEvent('ActivityFailed', { scopeId: 'fn-1' }),
      assistantEvent('TextDelta', { text: 'I could not search, but' }),
    ]);

    expect(turnAnnouncement(snapshot, idle)).toBe('Writing the answer. 1 step failed');
  });

  it('carries a failure a newer activity would otherwise bury', () => {
    const snapshot = fold([
      assistantEvent('ActivityStarted', { scopeId: 'fn-1', displayName: 'Search Documents' }),
      assistantEvent('ActivityFailed', { scopeId: 'fn-1' }),
      assistantEvent('ActivityStarted', { scopeId: 'mcp-1' }),
    ]);

    expect(turnAnnouncement(snapshot, idle)).toBe('Get forecast is running. 1 step failed');
  });

  it('counts the failures the headline has not already named', () => {
    const snapshot = fold([
      assistantEvent('ActivityStarted', { scopeId: 'mcp-1' }),
      assistantEvent('ActivityFailed', { scopeId: 'mcp-1' }),
      assistantEvent('ActivityStarted', { scopeId: 'fn-1', displayName: 'Search Documents' }),
      assistantEvent('ActivityFailed', { scopeId: 'fn-1' }),
    ]);

    // Not "Search Documents failed. 2 steps failed", which sounds like three failures.
    expect(turnAnnouncement(snapshot, idle)).toBe('Search Documents failed. 1 more step failed');
  });

  it('counts failures at any depth and pluralises them', () => {
    const snapshot = fold([
      assistantEvent('ActivityStarted', { scopeId: 'mcp-1' }),
      assistantEvent('ActivityStarted', {
        scopeId: 'agent-1',
        parentScopeId: 'mcp-1',
        displayName: 'Forecast Agent',
        toolKind: 'Agent',
        depth: 2,
      }),
      assistantEvent('ActivityFailed', { scopeId: 'agent-1', toolKind: 'Agent', depth: 2 }),
      assistantEvent('ActivityFailed', { scopeId: 'mcp-1' }),
      assistantEvent('TextDelta', { text: 'Sorry' }),
    ]);

    expect(turnAnnouncement(snapshot, idle)).toBe('Writing the answer. 2 steps failed');
  });

  it('does not change across a run of text deltas', () => {
    let snapshot = createInitialSnapshot();
    const spoken: string[] = [];

    for (const chunk of ['Hel', 'lo, ', 'wor', 'ld', '.']) {
      snapshot = foldAssistantEvents(snapshot, assistantEvent('TextDelta', { text: chunk }));
      spoken.push(turnAnnouncement(snapshot, idle));
    }

    expect(new Set(spoken)).toEqual(new Set(['Writing the answer']));
  });

  it('produces one announcement per status change over a recorded turn, and no more', () => {
    let snapshot = createInitialSnapshot();
    const spoken: string[] = [];

    for (const event of fullTurnEvents()) {
      snapshot = foldAssistantEvents(snapshot, event);
      const next = turnAnnouncement(snapshot, idle);
      if (next !== spoken[spoken.length - 1]) {
        spoken.push(next);
      }
    }

    // `Get forecast completed` is unspoken by design: the headline follows the pre-order-last
    // activity, which `mcp-1` stopped being once `agent-1` started inside it.
    expect(spoken).toEqual([
      'Waiting for a response',
      'Get forecast is running',
      'Forecast Agent is running',
      'Forecast Agent completed',
      'Search Documents is running',
      'Search Documents failed',
      'Writing the answer. 1 step failed',
    ]);
    expect(new Set(spoken).size).toBe(spoken.length);
  });
});
