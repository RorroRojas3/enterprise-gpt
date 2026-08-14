import { describe, expect, it } from 'vitest';
import { MAX_DELAY_MS, pollDelayMs } from './upload-schedule';

describe('pollDelayMs', () => {
  it('follows US-802s staged schedule and then holds at the cap', () => {
    const schedule = [0, 1, 2, 3, 4, 5, 6].map(pollDelayMs);

    expect(schedule).toEqual([500, 1_000, 2_000, 4_000, 5_000, 5_000, 5_000]);
  });

  it('never exceeds the cap however long a job runs', () => {
    // An OCR job can poll for minutes; the exponent must not run away.
    expect(pollDelayMs(400)).toBe(MAX_DELAY_MS);
  });

  it('treats a negative or fractional attempt as the first one', () => {
    expect(pollDelayMs(-3)).toBe(500);
    expect(pollDelayMs(0.9)).toBe(500);
  });
});
