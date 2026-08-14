/**
 * The delay before the *next* poll of an upload job, in milliseconds.
 *
 * US-802's staged schedule: `500 → 1s → 2s → 4s`, then held at 5s. A fixed interval is
 * the thing this replaces — 500ms forever hammers the API for a 20-minute OCR job, and
 * 5s forever makes a 300ms text file look slow. Doubling from 500 covers the common
 * case in one or two polls and settles into a cheap heartbeat for the long one.
 *
 * The cap is 5s rather than 8s because the ceiling is also the worst case for how stale
 * the "Ready to use" state can be, and the server's own retention window (60 minutes by
 * default) is far longer than any schedule this reaches.
 *
 * A pure function of the attempt index rather than a stateful generator, so the whole
 * sequence is provable in one assertion with no fake timers.
 *
 * @param attempt Zero-based index of the poll that has just completed.
 */
export function pollDelayMs(attempt: number): number {
  const index = Math.max(0, Math.trunc(attempt));

  return Math.min(FIRST_DELAY_MS * 2 ** index, MAX_DELAY_MS);
}

/** The wait before the first poll, and the base the schedule doubles from. */
export const FIRST_DELAY_MS = 500;

/** The ceiling the schedule holds at. */
export const MAX_DELAY_MS = 5_000;

/**
 * The sub-status line for a job that is still running.
 *
 * The server's `message` is preferred over the raw counts and is not appended to,
 * because it already reads as a sentence and already contains them where they exist —
 * "Embedded 12 of 24 chunk(s)". Composing a second "12 of 24" onto that would say the
 * same thing twice. The counts are the fallback for a stage that reports numbers with
 * no words, and they are read on every poll rather than latched, because any report
 * that does not supply them clears them.
 *
 * @param status The latest poll.
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
