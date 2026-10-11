/**
 * Doubles from 500ms to a 5s cap: quick files settle in a poll or two and long OCR jobs do not
 * hammer the API. The cap also bounds how stale "Ready to use" can look.
 *
 * @param attempt Zero-based index of the poll that has just completed.
 */
export function pollDelayMs(attempt: number): number {
  const index = Math.max(0, Math.trunc(attempt));

  return Math.min(FIRST_DELAY_MS * 2 ** index, MAX_DELAY_MS);
}

/** The wait before the first poll, and the base the schedule doubles from. */
export const FIRST_DELAY_MS = 500;

export const MAX_DELAY_MS = 5_000;

/**
 * The server's `message` is used as-is since it already includes any counts. The raw counts are
 * the fallback, read per poll rather than latched because a report without them clears them.
 */
export function subStatusOf(status: {
  readonly message: string | null;
  readonly completedUnits: number | null;
  readonly totalUnits: number | null;
}): string {
  if (status.message !== null && status.message.trim() !== '') {
    return status.message;
  }

  const { completedUnits, totalUnits } = status;
  if (completedUnits !== null && totalUnits !== null && totalUnits > 0) {
    return `${completedUnits} of ${totalUnits}`;
  }

  return 'Processing…';
}
