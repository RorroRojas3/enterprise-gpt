import { describe, expect, it } from 'vitest';
import {
  NO_VALUE,
  deltaPercent,
  formatCost,
  formatCount,
  formatDateRange,
  formatDay,
  formatDelta,
  formatPercent,
  formatTokens,
  groupingNoun,
  percentOf,
} from './usage-format';

describe('usage-format', () => {
  it('abbreviates token counts the way frame 5j writes them', () => {
    expect(formatTokens(48_200_000)).toBe('48.2M');
    expect(formatTokens(1_400_000)).toBe('1.4M');
    expect(formatTokens(2_500_000_000)).toBe('2.5B');
    expect(formatTokens(12_300)).toBe('12.3K');
    // Below a thousand the exact number is both shorter and more useful.
    expect(formatTokens(950)).toBe('950');
    expect(formatTokens(0)).toBe('0');
  });

  it('renders an unpriced figure as an em dash, never as zero', () => {
    // The whole reason cost is nullable: a deployment whose models carry no price has not
    // spent nothing.
    expect(formatCost(null)).toBe(NO_VALUE);
    expect(formatCost(0)).toBe('$0.00');
  });

  it('keeps the cents on a small cost and drops them on a large one', () => {
    expect(formatCost(1284)).toBe('$1,284');
    expect(formatCost(0.42)).toBe('$0.42');
    expect(formatCost(9.99)).toBe('$9.99');
    expect(formatCost(10)).toBe('$10');
  });

  it('groups plain counts', () => {
    expect(formatCount(1842)).toBe('1,842');
    expect(formatCount(216)).toBe('216');
  });

  it('computes a share and survives an empty denominator', () => {
    expect(percentOf(1710, 2193)).toBe(78);
    expect(formatPercent(285, 2193)).toBe('13%');
    expect(percentOf(0, 0)).toBe(0);
    expect(formatPercent(5, 0)).toBe('0%');
  });

  it('has no delta to report when the prior period held nothing', () => {
    // Every change from nothing is infinite, and `+∞%` is not a way to report growth.
    expect(deltaPercent(100, 0)).toBeNull();
    expect(formatDelta(null)).toBe(NO_VALUE);
  });

  it('signs a delta the way the board writes it', () => {
    expect(deltaPercent(112, 100)).toBe(12);
    expect(formatDelta(12)).toBe('+12%');
    expect(formatDelta(-3)).toBe('-3%');
    expect(formatDelta(0)).toBe('0%');
  });

  it('names the last day inside the window, not the exclusive bound', () => {
    // A 30-day range ending at midnight would otherwise name a day it does not cover.
    expect(formatDateRange('2026-07-09T12:00:00+00:00', '2026-08-08T12:00:00+00:00')).toBe(
      'Jul 9 – Aug 8, 2026',
    );
    expect(formatDateRange('2026-08-01T00:00:00+00:00', '2026-08-08T00:00:00+00:00')).toBe(
      'Aug 1 – Aug 7, 2026',
    );
  });

  it('prints both years when a range spans one', () => {
    expect(formatDateRange('2025-12-28T00:00:00+00:00', '2026-01-04T00:00:00+00:00')).toBe(
      'Dec 28, 2025 – Jan 3, 2026',
    );
  });

  it('formats an axis day in UTC, so a browser west of Greenwich does not shift it', () => {
    expect(formatDay('2026-07-09')).toBe('Jul 9');
    expect(formatDay('2026-01-01')).toBe('Jan 1');
  });

  it('names one row and many rows of each grouping', () => {
    expect(groupingNoun('model', 1)).toBe('model');
    expect(groupingNoun('model', 3)).toBe('models');
    expect(groupingNoun('day', 1)).toBe('day');
    expect(groupingNoun('user', 0)).toBe('users');
  });
});
