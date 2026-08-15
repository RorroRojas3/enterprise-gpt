/**
 * Whether a search term change should replace the history entry rather than push one.
 *
 * Refining a term replaces; adding or removing one pushes. Both extremes fail in their
 * own way: always replacing means the back button leaves the screen instead of
 * restoring the unfiltered list, and always pushing walks the user back through their
 * own typing one debounce at a time. Pushing at exactly the two transitions a reader
 * thinks of as a state change — filter on, filter off — makes `/conversations` →
 * `?name=quarterly` → back → `/conversations`, and makes clearing a search undoable too.
 *
 * In `core/` rather than beside either screen because both the conversations library
 * (US-701) and the projects grid (US-901) navigate on the same contract, and
 * `features → features` is not a direction this app has. US-703's favourites toggle
 * reuses it by passing its own before and after; both of its transitions are an add or
 * a remove, so both push.
 *
 * @param previous The term the URL currently carries.
 * @param next The term the user just committed.
 */
export function shouldReplaceHistory(previous: string, next: string): boolean {
  return previous !== '' && next !== '';
}
