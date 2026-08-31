/**
 * The application's three layout modes, as media queries. US-1403.
 *
 * The design boards restructure at two widths and only two: 768px, where tables become
 * cards and the sidebar leaves the flow, and 1024px, where the sidebar stops being
 * persistent. Everything else in `src/` that carries a width — the two form dialogs at
 * 480px, the project files row at 575px, the auth interstitial at 576px — is a
 * component reflowing its own contents, not the application changing mode, and
 * deliberately does not belong here.
 *
 * **The fractional maximum is load-bearing, and pairing it with a whole-number minimum
 * is the bug it exists to prevent.** `(max-width: 767px)` and `(min-width: 768px)` leave
 * a dead zone: a viewport reported as 767.5px — browser zoom, or Windows display
 * scaling, which this application ships on — matches *neither*. Under US-1403 that
 * would mean no sidebar in flow and no navbar either. `src/styles/_breakpoints.scss`
 * carries the identical numbers for the stylesheets, and `npm run lint` keeps the two
 * in step.
 *
 * **The two queries are mutually exclusive on purpose.** A `mode` derived from them
 * must read `desktop` when both are false, because the test fake answers `false` for
 * every query a spec has not set — so "nothing configured" has to mean the mode the
 * existing suite was written against.
 */

/** Phone: below the width at which tables become cards and the sidebar leaves the flow. */
export const MOBILE_VIEWPORT = '(max-width: 767.98px)';

/** Tablet: frame `3g`'s band, where the sidebar is a strip that expands as an overlay. */
export const TABLET_VIEWPORT = '(min-width: 768px) and (max-width: 1023.98px)';

/**
 * The same two edges as numbers, for code that compares against a measured width rather
 * than subscribing to a query — {@link anchoredPosition}, which is a pure function and
 * takes the viewport as an argument. `scripts/check-tokens.mjs` reads both to hold them
 * equal to `_breakpoints.scss`.
 */
export const TABLET_MIN_WIDTH = 768;

/** @see TABLET_MIN_WIDTH */
export const DESKTOP_MIN_WIDTH = 1024;
