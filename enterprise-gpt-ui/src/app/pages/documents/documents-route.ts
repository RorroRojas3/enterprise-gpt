/** The query parameter the documents library filters on. */
export const NAME_PARAM = 'name';

/**
 * Grouped is the default, so only `?view=flat` is ever written; anything else reads as grouped,
 * so a URL the client never produces cannot put the switch and the listing out of step.
 */
export const VIEW_PARAM = 'view';

/** The only value {@link VIEW_PARAM} is ever written with. */
export const VIEW_FLAT = 'flat';

/**
 * As {@link VIEW_PARAM}: only the narrowing is written and anything else reads as unfiltered,
 * so the client never sends a `?type=` the server would reject.
 */
export const SOURCE_PARAM = 'source';

/** The only value {@link SOURCE_PARAM} is ever written with. */
export const SOURCE_GENERATED = 'generated';

/** Re-exported so this screen's route contract reads from one file. */
export { shouldReplaceHistory } from '@shared/utils/navigation/search-history';
