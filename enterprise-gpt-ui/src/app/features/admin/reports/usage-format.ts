import { GROUPING_NOUNS, UsageGrouping } from './admin-reports-route';

/**
 * A fixed locale: the app ships no `LOCALE_ID` or i18n, so one consistent grouping beats a
 * per-browser one.
 */
const GROUPED = new Intl.NumberFormat('en-US');
const MONEY = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  maximumFractionDigits: 0,
});
const SMALL_MONEY = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});
const DAY = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' });
const DAY_AND_YEAR = new Intl.DateTimeFormat('en-US', {
  month: 'short',
  day: 'numeric',
  year: 'numeric',
  timeZone: 'UTC',
});

/** One day in milliseconds, shared by the request window and the caption that names it. */
export const MS_PER_DAY = 24 * 60 * 60 * 1000;

/** What an unavailable figure reads as. Never a zero, which would read as a measurement. */
export const NO_VALUE = '—';

export function formatCount(value: number): string {
  return Number.isFinite(value) ? GROUPED.format(value) : NO_VALUE;
}

/** `48,200,000` reads `48.2M`, so seven-digit figures compare at a glance; under 1,000 is exact. */
export function formatTokens(value: number): string {
  if (!Number.isFinite(value)) {
    return NO_VALUE;
  }

  const magnitude = Math.abs(value);
  if (magnitude >= 1_000_000_000) {
    return `${(value / 1_000_000_000).toFixed(1)}B`;
  }

  if (magnitude >= 1_000_000) {
    return `${(value / 1_000_000).toFixed(1)}M`;
  }

  if (magnitude >= 1000) {
    return `${(value / 1000).toFixed(1)}K`;
  }

  return GROUPED.format(value);
}

/**
 * Null renders as {@link NO_VALUE}, not `$0`: unpriced is not free. Sub-$10 figures keep their
 * cents so a quiet week on a cheap model does not read as zero.
 */
export function formatCost(value: number | null): string {
  if (value === null || !Number.isFinite(value)) {
    return NO_VALUE;
  }

  return Math.abs(value) < 10 ? SMALL_MONEY.format(value) : MONEY.format(value);
}

/** Rounded to a whole percent; a zero denominator reads `0%`. */
export function formatPercent(part: number, whole: number): string {
  return `${percentOf(part, whole)}%`;
}

export function percentOf(part: number, whole: number): number {
  return whole <= 0 ? 0 : Math.round((part / whole) * 100);
}

/**
 * Null when the prior period held nothing: every change from zero is infinite, so the tile
 * captions it instead of inventing a number.
 */
export function deltaPercent(current: number, prior: number): number | null {
  if (!Number.isFinite(current) || !Number.isFinite(prior) || prior <= 0) {
    return null;
  }

  return Math.round(((current - prior) / prior) * 100);
}

/** Signed: `+12%`, `-3%`, `0%`. */
export function formatDelta(delta: number | null): string {
  if (delta === null) {
    return NO_VALUE;
  }

  return delta > 0 ? `+${delta}%` : `${delta}%`;
}

/**
 * `Jul 9 – Aug 8, 2026`; the joiner varies because the empty state reads mid-sentence. The end is
 * the last day inside the window, not the server's exclusive bound.
 */
export function formatDateRange(from: string, to: string, joiner = '–'): string {
  const start = new Date(from);
  const end = new Date(new Date(to).getTime() - 1);

  if (Number.isNaN(start.getTime()) || Number.isNaN(end.getTime())) {
    return NO_VALUE;
  }

  return start.getUTCFullYear() === end.getUTCFullYear()
    ? `${DAY.format(start)} ${joiner} ${DAY_AND_YEAR.format(end)}`
    : `${DAY_AND_YEAR.format(start)} ${joiner} ${DAY_AND_YEAR.format(end)}`;
}

export function formatDay(date: string): string {
  const parsed = new Date(`${date}T00:00:00Z`);

  return Number.isNaN(parsed.getTime()) ? date : DAY.format(parsed);
}

export function groupingNoun(grouping: UsageGrouping, count: number): string {
  const noun = GROUPING_NOUNS[grouping];

  return count === 1 ? noun.one : noun.many;
}
