/** The query parameter the conversations library filters on (US-701). */
export const NAME_PARAM = 'name';

/**
 * The query parameter the favourites filter lives in (US-703).
 *
 * `favorites=1`, not the API's own `isFavorite=true`. The URL is a contract with the
 * reader, not a proxy for the request underneath it, and `isFavorite=false` is a
 * meaningful value on the wire that this screen never sends — a URL that could spell
 * it would invite a link that means something the client cannot honour.
 */
export const FAVORITES_PARAM = 'favorites';

/** The only value {@link FAVORITES_PARAM} is ever written with. */
export const FAVORITES_ON = '1';

/**
 * Why the library offers no sort control (US-705).
 *
 * `GET api/conversations/search` orders by `dateCreated` descending and accepts no
 * sort parameter, so the PRD's regime **B** applies: server order, stated rather than
 * left to be inferred. A control that reordered only the loaded rows would put a
 * second sorted run underneath the first on the next Load more, which is the failure
 * the regime exists to avoid.
 *
 * US-706 is the enabler that replaces this with a real control.
 */
export const ORDER_EXPLANATION =
  "Conversations are listed newest first. This list can't be reordered.";

/**
 * Re-exported so this screen's route contract still reads from one file.
 *
 * The function itself moved to `core/navigation/` when US-901's projects grid needed
 * the same rule: `features → features` is not a direction this app has, and a second
 * copy is a second thing to get wrong.
 */
export { shouldReplaceHistory } from '@core/navigation/search-history';
