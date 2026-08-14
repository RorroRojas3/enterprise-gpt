/** The query parameter the conversations library filters on (US-701). */
export const NAME_PARAM = 'name';

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
 * US-703's favourites toggle is a discrete on/off and reuses this by passing its own
 * before and after.
 */
export function shouldReplaceHistory(previous: string, next: string): boolean {
  return previous !== '' && next !== '';
}
