/**
 * A fixed locale rather than the reader's, deliberately.
 *
 * The app ships no `LOCALE_ID` and no i18n, so the reader's locale is whatever their
 * browser reports — which would make `32,768` render as `32.768` for a German reader,
 * beside a deployment name and a context size that are not localised at all. One
 * consistent grouping is the honest answer until there is a translation to belong to.
 */
const GROUPED = new Intl.NumberFormat('en-US');

/**
 * A context window, abbreviated as frame `5e` writes it — `400000` reads `400k`.
 *
 * The column is 90px and every value in it is six digits, so the abbreviation is what
 * makes the numbers comparable at a glance rather than a decoration. Values below a
 * thousand are left alone, and a fractional one keeps a single decimal: the column is
 * `decimal(18,2)` server-side, so a model seeded outside this screen can hold one.
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

/** A token count in full, thousands-separated — frame `5e`'s `32,768`. */
export function formatTokens(tokens: number): string {
  return Number.isFinite(tokens) ? GROUPED.format(tokens) : '—';
}
