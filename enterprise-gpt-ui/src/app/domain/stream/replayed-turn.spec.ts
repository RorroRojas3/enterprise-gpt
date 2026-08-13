import { describe, expect, it } from 'vitest';
import { createInitialSnapshot } from './andes/assistant-ui.contract';
import { replayAssistantText } from './replayed-turn';

describe('replayAssistantText (US-410)', () => {
  it('puts the stored answer where the transcript reads a live one from', () => {
    const { snapshot, timeline } = replayAssistantText('It is sunny.');

    expect(snapshot.text).toBe('It is sunny.');
    // One text node spanning the whole answer, so the render-time join over
    // `[start, end)` offsets works exactly as it does for a streamed turn.
    expect(timeline.nodes).toEqual([{ kind: 'text', start: 0, end: 'It is sunny.'.length }]);
    expect(timeline.textLength).toBe('It is sunny.'.length);
  });

  it('claims no activities and no completion, because the message records none', () => {
    const { snapshot } = replayAssistantText('It is sunny.');

    // The stream that produced the activity timeline was never persisted
    // (streaming contract §6.2). Inventing a Completed phase or an empty
    // activity list with a duration would be a claim the stored message
    // cannot support.
    expect(snapshot.activities).toEqual([]);
    expect(snapshot.phase).toBe(createInitialSnapshot().phase);
    expect(snapshot.usage).toEqual(createInitialSnapshot().usage);
  });

  it('carries an empty message through without inventing a text node', () => {
    const { snapshot, timeline } = replayAssistantText('');

    expect(snapshot.text ?? '').toBe('');
    expect(timeline.nodes).toEqual([]);
  });
});
