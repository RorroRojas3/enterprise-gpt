import { UsageGrouping, GROUPING_NOUNS } from './admin-reports-route';

/**
 * A fixed locale rather than the reader's, for the reason `models/model-format.ts` records: the
 * app ships no `LOCALE_ID` and no i18n, so one consistent grouping beats a per-browser one.
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

/** A plain count, thousands-separated — the board's `1,842`. */
export function formatCount(value: number): string {
  return Number.isFinite(value) ? GROUPED.format(value) : NO_VALUE;
}

/**
 * A token count abbreviated the way frame `5j` writes it — `48,200,000` reads `48.2M`.
 *
 * The tiles and the bar values are narrow and every figure in them is seven digits or more, so the
 * abbreviation is what makes them comparable at a glance. Below a thousand the exact number is
 * both shorter and more useful, so it is left alone.
 */
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
 * An estimated cost, or {@link NO_VALUE} when there was nothing to price.
 *
 * Null is rendered as an em dash rather than `$0`, which is the whole reason the server models it
 * as nullable: a deployment whose models carry no price has not spent nothing.
 *
 * Sub-$10 figures keep their cents. The board only ever draws `$1,284`, but a quiet week on a
 * cheap model rounds to `$0` without them, and a cost report that says zero when money was spent
 * is worse than one that is two characters wider.
 */
export function formatCost(value: number | null): string {
  if (value === null || !Number.isFinite(value)) {
    return NO_VALUE;
  }

  return Math.abs(value) < 10 ? SMALL_MONEY.format(value) : MONEY.format(value);
}

/** A share of a whole, rounded to the board's precision — `78%`. Zero denominators read `0%`. */
export function formatPercent(part: number, whole: number): string {
  return `${percentOf(part, whole)}%`;
}

/** The rounded share itself, for the donut geometry as well as its labels. */
export function percentOf(part: number, whole: number): number {
  return whole <= 0 ? 0 : Math.round((part / whole) * 100);
}

/**
 * The change from the prior period, as a signed percentage, or `null` when there is nothing to
 * compare against.
 *
 * A prior period of zero has no percentage — every change from nothing is infinite — so the tile
 * renders a caption saying so rather than a number that would be invented. Growth from zero is a
 * real event, and `+∞%` is not a way to report it.
 */
export function deltaPercent(current: number, prior: number): number | null {
  if (!Number.isFinite(current) || !Number.isFinite(prior) || prior <= 0) {
    return null;
  }

  return Math.round(((current - prior) / prior) * 100);
}

/** The delta rendered as the board writes it — `+12%`, `-3%`, `0%`. */
export function formatDelta(delta: number | null): string {
  if (delta === null) {
    return NO_VALUE;
  }

  return delta > 0 ? `+${delta}%` : `${delta}%`;
}

/**
 * The window, as frame `5j`'s chip writes it — `Jul 9 – Aug 8, 2026`.
 *
 * The joiner is a parameter because the same range reads in two places: as a chip, where an en
 * dash is the notation, and mid-sentence in the empty state, where "between Jul 9 – Aug 8" is not
 * a sentence.
 *
 * The end is the last day *inside* the window, not the exclusive bound the server reports: a
 * 30-day range ending at midnight would otherwise name a day it does not cover. The year is
 * printed once when both ends share it, and on both when they do not, so a range spanning New Year
 * is never ambiguous.
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

/** One axis label under the area chart — `Jul 9`. */
export function formatDay(date: string): string {
  const parsed = new Date(`${date}T00:00:00Z`);

  return Number.isNaN(parsed.getTime()) ? date : DAY.format(parsed);
}

/** How the empty state and the count summary name the rows of one grouping. */
export function groupingNoun(grouping: UsageGrouping, count: number): string {
  const noun = GROUPING_NOUNS[grouping];

  return count === 1 ? noun.one : noun.many;
}
