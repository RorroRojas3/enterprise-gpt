/**
 * The app's only layout edges; a component reflowing its own contents keeps its width local.
 * The `.98` maxima avoid a dead zone at fractional widths (zoom, display scaling) that matches
 * neither query; `_breakpoints.scss` mirrors these and `npm run lint` keeps them in step. A
 * derived mode must read `desktop` when both are false, which is what the test fake answers.
 */

/** Below this width tables become cards and the sidebar leaves the flow. */
export const MOBILE_VIEWPORT = '(max-width: 767.98px)';

/** The band where the sidebar is a strip that expands as an overlay. */
export const TABLET_VIEWPORT = '(min-width: 768px) and (max-width: 1023.98px)';

/**
 * For code comparing a measured width, such as {@link anchoredPosition}.
 * `scripts/check-tokens.mjs` holds both equal to `_breakpoints.scss`.
 */
export const TABLET_MIN_WIDTH = 768;

/** @see TABLET_MIN_WIDTH */
export const DESKTOP_MIN_WIDTH = 1024;
