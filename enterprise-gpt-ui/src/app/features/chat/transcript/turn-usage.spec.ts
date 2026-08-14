import { describe, expect, it } from 'vitest';
import { formatActivityUsage, formatDurationSeconds, formatTurnUsage } from './turn-usage';

/**
 * US-504. The formatting rule that matters is the one about absence: every
 * count on the wire is optional because a provider may report only some, and a
 * count that was not reported is not a count of zero.
 */
describe('formatTurnUsage', () => {
  it('writes the footer line frames 1f and 1g draw', () => {
    expect(formatTurnUsage({ inputTokens: 1842, outputTokens: 512, totalTokens: 2354 })).toBe(
      '1,842 in · 512 out · 2,354 total',
    );
  });

  it('omits a count the provider did not report rather than showing a zero', () => {
    expect(formatTurnUsage({ inputTokens: 1842, totalTokens: 2354 })).toBe(
      '1,842 in · 2,354 total',
    );
    expect(formatTurnUsage({ outputTokens: 512 })).toBe('512 out');
  });

  it('renders a reported zero, which is a measurement and not an absence', () => {
    expect(formatTurnUsage({ outputTokens: 0 })).toBe('0 out');
  });

  it('claims nothing when the turn never reported usage', () => {
    expect(formatTurnUsage(undefined)).toBeNull();
    expect(formatTurnUsage({})).toBeNull();
  });
});

describe('formatActivityUsage', () => {
  it('writes the split as the frame does, two non-breaking spaces to a column', () => {
    expect(formatActivityUsage({ inputTokens: 1204, outputTokens: 88 }, 3.1)).toEqual([
      'input\u00a0\u00a01,204',
      'output\u00a0\u00a088',
      'duration\u00a0\u00a03.1s',
    ]);
  });

  it('shows the duration alone while no server attributes usage per activity', () => {
    expect(formatActivityUsage(undefined, 1.4)).toEqual(['duration\u00a0\u00a01.4s']);
  });

  it('has nothing to show for an activity that is still running', () => {
    expect(formatActivityUsage(undefined, undefined)).toEqual([]);
  });
});

describe('formatDurationSeconds', () => {
  it('writes one decimal place, and nothing at all before completion', () => {
    expect(formatDurationSeconds(0.6)).toBe('0.6s');
    expect(formatDurationSeconds(12)).toBe('12.0s');
    expect(formatDurationSeconds(undefined)).toBeNull();
  });
});
