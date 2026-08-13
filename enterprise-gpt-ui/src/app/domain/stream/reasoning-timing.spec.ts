import { describe, expect, it } from 'vitest';
import { assistantEvent } from '@testing/stream-frames';
import {
  ReasoningTiming,
  createInitialReasoningTiming,
  foldReasoningTiming,
  hasModelReasoning,
  modelReasoningText,
  reasoningSeconds,
} from './reasoning-timing';

/**
 * US-503. The pill's duration is measured here rather than read off the wire,
 * and the server's seeded first delta is measured out of the text, so these
 * specs are where "what the number means" and "whose words those are" are
 * pinned.
 */
describe('foldReasoningTiming', () => {
  /** The opening pair every turn carries (streaming contract §4.3). */
  const SEED = 'Reviewing the request and preparing a response.\n\n';

  function fold(steps: readonly (readonly [ReturnType<typeof assistantEvent>, number])[]) {
    return steps.reduce<ReasoningTiming>(
      (timing, [event, now]) => foldReasoningTiming(timing, event, now),
      createInitialReasoningTiming(),
    );
  }

  it('measures from the model’s first delta to the first renderable content', () => {
    const timing = fold([
      [assistantEvent('ReasoningDelta', { text: SEED }), 500],
      [assistantEvent('ReasoningDelta', { text: 'Weighing the options. ' }), 1_000],
      [assistantEvent('ReasoningDelta', { text: 'The rotation gates it. ' }), 2_000],
      [assistantEvent('TextDelta', { text: 'Hello' }), 5_200],
    ]);

    expect(reasoningSeconds(timing)).toBe(4.2);
  });

  it('does not start the clock on the server’s seeded delta', () => {
    // The seed arrives ahead of the model's first event, so opening on it would
    // report the stream's own latency as thinking time.
    const timing = fold([
      [assistantEvent('ReasoningDelta', { text: SEED }), 500],
      [assistantEvent('TextDelta', { text: 'Hello' }), 9_000],
    ]);

    expect(hasModelReasoning(timing)).toBe(false);
    expect(reasoningSeconds(timing)).toBeNull();
  });

  it('leaves the seed out of the reasoning it shows', () => {
    const timing = fold([
      [assistantEvent('ReasoningDelta', { text: SEED }), 500],
      [assistantEvent('ReasoningDelta', { text: 'Weighing the options.' }), 1_000],
    ]);

    expect(modelReasoningText(timing, `${SEED}Weighing the options.`)).toBe(
      'Weighing the options.',
    );
  });

  it('shows nothing for a turn whose only reasoning was the seed', () => {
    const timing = fold([[assistantEvent('ReasoningDelta', { text: SEED }), 500]]);

    expect(modelReasoningText(timing, SEED)).toBe('');
  });

  it('stops at an activity too — a tool call is the model having started work', () => {
    const timing = fold([
      [assistantEvent('ReasoningDelta', { text: SEED }), 500],
      [assistantEvent('ReasoningDelta', { text: 'Weighing.' }), 1_000],
      [assistantEvent('ActivityStarted', { scopeId: 'mcp-1' }), 3_500],
      [assistantEvent('TextDelta', { text: 'Hello' }), 9_000],
    ]);

    expect(reasoningSeconds(timing)).toBe(2.5);
  });

  it('does not restart or extend when reasoning resumes mid-answer', () => {
    const timing = fold([
      [assistantEvent('ReasoningDelta', { text: SEED }), 500],
      [assistantEvent('ReasoningDelta', { text: 'Weighing.' }), 1_000],
      [assistantEvent('TextDelta', { text: 'Hello' }), 2_000],
      [assistantEvent('ReasoningDelta', { text: 'On reflection.' }), 6_000],
      [assistantEvent('TextDelta', { text: ' again' }), 7_000],
    ]);

    expect(reasoningSeconds(timing)).toBe(1);
  });

  it('closes an open window on Finished, so a reasoning-only turn still reports', () => {
    const timing = fold([
      [assistantEvent('ReasoningDelta', { text: SEED }), 500],
      [assistantEvent('ReasoningDelta', { text: 'Weighing.' }), 1_000],
      [assistantEvent('Finished'), 4_000],
    ]);

    expect(reasoningSeconds(timing)).toBe(3);
  });

  it('claims nothing for a turn that streamed no reasoning at all', () => {
    const timing = fold([
      [assistantEvent('TextDelta', { text: 'Hello' }), 1_000],
      [assistantEvent('Finished'), 2_000],
    ]);

    expect(timing).toEqual({ seedLength: null, startedAt: null, endedAt: null });
    expect(hasModelReasoning(timing)).toBe(false);
  });

  it('claims nothing while the window is still open', () => {
    const timing = fold([
      [assistantEvent('ReasoningDelta', { text: SEED }), 500],
      [assistantEvent('ReasoningDelta', { text: 'Weighing.' }), 1_000],
    ]);

    expect(hasModelReasoning(timing)).toBe(true);
    expect(reasoningSeconds(timing)).toBeNull();
  });

  it('returns the same reference for an event that carries no timing', () => {
    const timing = createInitialReasoningTiming();

    expect(foldReasoningTiming(timing, assistantEvent('Status'), 1_000)).toBe(timing);
    expect(foldReasoningTiming(timing, assistantEvent('ActivityCompleted'), 1_000)).toBe(timing);
  });
});
