/**
 * A fixed locale: the app ships no i18n, so the browser's locale would group digits unlike
 * everything around them.
 */
const GROUPED = new Intl.NumberFormat('en-US');

/**
 * `400000` reads `400k`, so six-digit values compare at a glance in a 90px column. Keeps one
 * decimal, since the column is `decimal(18,2)`.
 */
export function formatContext(tokens: number): string {
  if (!Number.isFinite(tokens)) {
    return '—';
  }

  if (Math.abs(tokens) < 1000) {
    return GROUPED.format(tokens);
  }

  const thousands = tokens / 1000;

  return Number.isInteger(thousands) ? `${thousands}k` : `${thousands.toFixed(1)}k`;
}

/** A token count in full, thousands-separated: `32,768`. */
export function formatTokens(tokens: number): string {
  return Number.isFinite(tokens) ? GROUPED.format(tokens) : '—';
}
