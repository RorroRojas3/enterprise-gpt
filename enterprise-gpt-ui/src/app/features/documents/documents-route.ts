/** The query parameter the documents library filters on (US-1003). */
export const NAME_PARAM = 'name';

/**
 * The query parameter the view mode lives in (US-1003).
 *
 * The URL records only the deviation: grouped is the default frame `4j` draws, so a
 * grouped listing is the bare `/documents` and only `?view=flat` is ever written.
 * Anything other than the one value this screen writes reads as grouped — the rule the
 * conversations library applies to a hand-edited `?favorites=yes` — so a URL the
 * client never produces cannot put the switch and the listing out of step.
 */
export const VIEW_PARAM = 'view';

/** The only value {@link VIEW_PARAM} is ever written with. */
export const VIEW_FLAT = 'flat';

/**
 * The query parameter the origin filter lives in.
 *
 * Same rule as {@link VIEW_PARAM}, and the same reason: every origin is the default, so
 * only the narrowing is written and anything but the one value below reads as
 * unfiltered. That is also what keeps the client from ever sending the server a
 * `?type=` it would reject.
 */
export const SOURCE_PARAM = 'source';

/** The only value {@link SOURCE_PARAM} is ever written with. */
export const SOURCE_GENERATED = 'generated';

/**
 * Re-exported so this screen's route contract still reads from one file, the shape
 * `conversations-route.ts` set. The view toggle does not use it: on/off is the
 * add-or-remove transition the function exists to push at, so the toggle always
 * pushes and back undoes it in both directions.
 */
export { shouldReplaceHistory } from '@core/navigation/search-history';
