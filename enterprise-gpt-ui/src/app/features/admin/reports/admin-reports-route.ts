/**
 * The range control, in days — frame `5j` draws exactly these three.
 *
 * The URL carries the *span* rather than two timestamps, which is what keeps a shared link
 * meaning "the last 30 days" instead of a window that silently ages into history. The server
 * resolves the span into instants and echoes them back, so the date chip always reads what was
 * actually measured.
 */
export const USAGE_RANGES = [7, 30, 90] as const;

export type UsageRange = (typeof USAGE_RANGES)[number];

/** The range a link that names none is read as — the board's own active state. */
export const DEFAULT_USAGE_RANGE: UsageRange = 30;

/** The dimensions `GET api/reports/usage` groups by, in the order the select offers them. */
export const USAGE_GROUPINGS = ['model', 'provider', 'user', 'day'] as const;

export type UsageGrouping = (typeof USAGE_GROUPINGS)[number];

export const DEFAULT_USAGE_GROUPING: UsageGrouping = 'model';

/** The query parameter the segmented control writes. */
export const RANGE_PARAM = 'range';

/**
 * The query parameter the Group-by select writes.
 *
 * `group`, not the API's own `groupBy`, for the reason the user directory's `?permission=` records:
 * the URL is a contract with the reader rather than a proxy for the request underneath it.
 */
export const GROUP_PARAM = 'group';

/** How many grouped rows the table shows at once. Frame `5j` draws five. */
export const USAGE_PAGE_SIZE = 5;

/** What each grouping calls its rows, for the table's first column header. */
export const GROUPING_COLUMN_LABELS: Readonly<Record<UsageGrouping, string>> = {
  model: 'Model',
  provider: 'Provider',
  user: 'User',
  day: 'Day',
};

/**
 * What each grouping calls its rows in the empty state and the count summary.
 *
 * Lower-case, because they read mid-sentence: "No models used between …".
 */
export const GROUPING_NOUNS: Readonly<Record<UsageGrouping, { one: string; many: string }>> = {
  model: { one: 'model', many: 'models' },
  provider: { one: 'provider', many: 'providers' },
  user: { one: 'user', many: 'users' },
  day: { one: 'day', many: 'days' },
};

/**
 * Reads a `?range=` into one of {@link USAGE_RANGES}.
 *
 * Restricted to the offered set rather than clamped, so the control always shows the range that is
 * in force: a hand-edited `?range=5` would otherwise leave the segmented control displaying a span
 * the URL contradicts. Repairable here, synchronously, because the set is a compile-time constant —
 * unlike the directory's `?permission=`, which only the server can vouch for.
 */
export function toUsageRange(value: string | undefined): UsageRange {
  // Narrowed rather than trusted: `withComponentInputBinding()` hands over the raw `Params` value,
  // which is an array for a repeated key, and `Number([7, 30])` is `NaN` rather than a throw.
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
