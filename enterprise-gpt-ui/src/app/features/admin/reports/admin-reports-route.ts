/**
 * Range options in days. The URL carries the span, not timestamps, so a shared link keeps meaning
 * "the last 30 days"; the server echoes back the instants it measured.
 */
export const USAGE_RANGES = [7, 30, 90] as const;

export type UsageRange = (typeof USAGE_RANGES)[number];

export const DEFAULT_USAGE_RANGE: UsageRange = 30;

/** The dimensions `GET api/reports/usage` groups by, in the order the select offers them. */
export const USAGE_GROUPINGS = ['model', 'provider', 'user', 'day'] as const;

export type UsageGrouping = (typeof USAGE_GROUPINGS)[number];

export const DEFAULT_USAGE_GROUPING: UsageGrouping = 'model';

export const RANGE_PARAM = 'range';

/** `group`, not the API's `groupBy`: the URL is a contract with the reader, not a request proxy. */
export const GROUP_PARAM = 'group';

export const USAGE_PAGE_SIZE = 5;

export const GROUPING_COLUMN_LABELS: Readonly<Record<UsageGrouping, string>> = {
  model: 'Model',
  provider: 'Provider',
  user: 'User',
  day: 'Day',
};

/** Lower-case because they read mid-sentence: "No models used between …". */
export const GROUPING_NOUNS: Readonly<Record<UsageGrouping, { one: string; many: string }>> = {
  model: { one: 'model', many: 'models' },
  provider: { one: 'provider', many: 'providers' },
  user: { one: 'user', many: 'users' },
  day: { one: 'day', many: 'days' },
};

/**
 * Restricted to {@link USAGE_RANGES}, not clamped, so the control never shows a span the URL
 * contradicts. Repairable synchronously because the set is a compile-time constant.
 */
export function toUsageRange(value: string | undefined): UsageRange {
  // Narrowed, not trusted: a repeated key binds as an array, and `Number([7, 30])` is NaN.
  const days = typeof value === 'string' ? Number(value) : Number.NaN;

  return (USAGE_RANGES as readonly number[]).includes(days)
    ? (days as UsageRange)
    : DEFAULT_USAGE_RANGE;
}

/** Reads a `?group=` into one of {@link USAGE_GROUPINGS}, on the same terms. */
export function toUsageGrouping(value: string | undefined): UsageGrouping {
  const token = typeof value === 'string' ? value.trim().toLowerCase() : '';

  return (USAGE_GROUPINGS as readonly string[]).includes(token)
    ? (token as UsageGrouping)
    : DEFAULT_USAGE_GROUPING;
}
