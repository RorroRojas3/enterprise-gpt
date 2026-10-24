const MINUTE_MS = 60_000;
const HOUR_MS = 60 * MINUTE_MS;
const DAY_MS = 24 * HOUR_MS;

/**
 * "2h ago" within a day, "Yesterday" by calendar day, then a date. `now` is a parameter so the
 * label stays pure and cannot change between two checks of the same tick.
 */
export function formatRelativeTime(iso: string, now: number): string {
  const then = Date.parse(iso);
  if (Number.isNaN(then)) {
    return '';
  }

  const elapsed = now - then;

  // Clock skew or a slightly future timestamp reads as "Just now", not a negative age.
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

/** "Aug 5", or "Aug 5, 2025" unless `omitYear`; empty when `iso` cannot be parsed. */
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
