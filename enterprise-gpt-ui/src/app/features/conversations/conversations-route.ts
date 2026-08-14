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
 * Whether a search term change should replace the history entry rather than push one.
 *
 * Refining a term replaces; adding or removing one pushes. Both extremes fail the
 * criterion in their own way: always replacing means the back button leaves the screen
 * instead of restoring the unfiltered list, and always pushing walks the user back
 * through their own typing one debounce at a time. Pushing at exactly the two
 * transitions a reader thinks of as a state change — filter on, filter off — makes
 * `/conversations` → `?name=quarterly` → back → `/conversations`, and makes clearing a
 * search undoable too.
 *
 * US-703's favourites toggle reuses this by passing its own before and after. Both of
 * its transitions are an add or a remove, so both push — which is what makes turning
 * the filter on undoable with the back button.
 */
export function shouldReplaceHistory(previous: string, next: string): boolean {
  return previous !== '' && next !== '';
}
