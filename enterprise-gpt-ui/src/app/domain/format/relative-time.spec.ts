import { describe, expect, it } from 'vitest';
import { formatRelativeTime, formatShortDate } from './relative-time';

/**
 * A fixed clock, because the function is pure in `now` precisely so it can be.
 *
 * Local time, not `Z`: the day boundary the function tests is the reader's calendar
 * day, so a UTC literal would make these assertions depend on the runner's time zone.
 */
const NOW = Date.parse('2026-08-14T15:00:00');

function at(iso: string): string {
  return formatRelativeTime(iso, NOW);
}

describe('formatRelativeTime', () => {
  it('reads the first minute as "Just now"', () => {
    expect(at('2026-08-14T14:59:30')).toBe('Just now');
  });

  it('counts minutes, then hours, inside the day', () => {
    expect(at('2026-08-14T14:20:00')).toBe('40m ago');
    expect(at('2026-08-14T13:00:00')).toBe('2h ago');
    expect(at('2026-08-13T16:00:00')).toBe('23h ago');
  });

  it('names yesterday past the 24-hour mark, by the calendar', () => {
    // 25 hours back is still yesterday's date here; 26 hours back is the day before,
    // which is the distinction an elapsed-time test alone cannot make.
    expect(at('2026-08-13T14:00:00')).toBe('Yesterday');
    expect(at('2026-08-12T23:00:00')).not.toBe('Yesterday');
  });

  it('falls back to a date once it is older than yesterday', () => {
    // The month and day are formatted for the reader's locale; the year is what this
    // asserts, because that is the branch.
    expect(at('2026-08-05T09:00:00')).not.toContain('2026');
    expect(at('2025-11-05T09:00:00')).toContain('2025');
  });

  it('reads a future timestamp as "Just now" rather than as a negative age', () => {
    // Clock skew between a server stamp and the browser is normal and must not render
    // as "-3m ago".
    expect(at('2026-08-14T15:05:00')).toBe('Just now');
  });

  it('answers empty for a value it cannot parse', () => {
    expect(at('not a date')).toBe('');
    expect(formatShortDate('not a date')).toBe('');
  });
});
