const MINUTE_MS = 60_000;
const HOUR_MS = 60 * MINUTE_MS;
const DAY_MS = 24 * HOUR_MS;

/**
 * Formats an ISO timestamp the way frames `4c` and `4g` write it — "2h ago",
 * "Yesterday", "Aug 5".
 *
 * Three bands, because they answer three different questions. Inside a day the reader
 * wants elapsed time; the day before, a name; older than that, a date — and a date
 * loses its year only within the current one, so "Aug 5" can never be ambiguous.
 *
 * `now` is a parameter rather than a `Date.now()` read inside the function so the
 * result is a pure function of its inputs: a component computes these into a memoized
 * `computed()` over its rows, and `provideCheckNoChangesConfig({ exhaustive: true })`
 * would flag a label that changed between two passes of the same tick.
 *
 * The day comparison is calendar-based, not `< 48h`: 23:59 yesterday is "Yesterday" at
 * 00:01 today, and 00:01 today is not "Yesterday" at 23:59 today.
 *
 * @param iso An ISO 8601 timestamp with offset, as every API date field carries.
 * @param now Milliseconds since the epoch to measure against.
 * @returns The label, or an empty string when `iso` cannot be parsed.
 */
export function formatRelativeTime(iso: string, now: number): string {
  const then = Date.parse(iso);
  if (Number.isNaN(then)) {
    return '';
  }

  const elapsed = now - then;

  // A clock skew or a server timestamp a moment in the future reads as "Just now"
  // rather than as a negative age.
  if (elapsed < MINUTE_MS) {
    return 'Just now';
  }

  if (elapsed < HOUR_MS) {
    return `${Math.floor(elapsed / MINUTE_MS)}m ago`;
  }

  if (elapsed < DAY_MS) {
    return `${Math.floor(elapsed / HOUR_MS)}h ago`;
  }

  const date = new Date(then);
  const today = new Date(now);

  if (isSameCalendarDay(date, addDays(today, -1))) {
    return 'Yesterday';
  }

  return formatShortDate(iso, date.getFullYear() === today.getFullYear());
}

/**
 * Formats an ISO timestamp as a date — "Aug 5", or "Aug 5, 2025" outside this year.
 *
 * Frame `4f`'s file rows use this directly: a document's created date is a fact about
 * when it was ingested, not a duration the reader is tracking.
 *
 * @param iso An ISO 8601 timestamp with offset.
 * @param omitYear Whether to leave the year off, for dates inside the current year.
 * @returns The label, or an empty string when `iso` cannot be parsed.
 */
export function formatShortDate(iso: string, omitYear = false): string {
  const parsed = Date.parse(iso);
  if (Number.isNaN(parsed)) {
    return '';
  }

  return new Date(parsed).toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
    ...(omitYear ? {} : { year: 'numeric' }),
  });
}

function addDays(date: Date, days: number): Date {
  const shifted = new Date(date.getTime());
  shifted.setDate(shifted.getDate() + days);

  return shifted;
}

function isSameCalendarDay(a: Date, b: Date): boolean {
  return (
    a.getFullYear() === b.getFullYear() &&
    a.getMonth() === b.getMonth() &&
    a.getDate() === b.getDate()
  );
}
