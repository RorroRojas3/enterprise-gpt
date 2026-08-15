/**
 * How many projects each request asks for — the server's own clamp ceiling, so a drain
 * costs the fewest possible round trips.
 */
export const PROJECT_PAGE_SIZE = 100;

/**
 * How many projects a drain loads before it stops.
 *
 * The PRD's regime **A**: a fully materialised set, so US-902 can offer a sort that is
 * correct rather than one that reorders whichever page happened to be in hand. Five
 * requests at most. Past it the surface states what it is showing rather than letting
 * the shortfall pass for a complete list.
 */
export const PROJECT_LOAD_CEILING = 500;

/**
 * Whether a name matches a search term the way the server matches it.
 *
 * `EF.Functions.Like(name, '%term%')` under a case-insensitive collation, which is a
 * case-insensitive substring test. An empty term matches everything, because a client
 * that has no term omits the parameter entirely.
 *
 * Shared rather than duplicated: the projects grid uses it to decide whether a created
 * or renamed project still belongs to its active search, and US-307's picker filters its
 * list with the same rule so a typed term narrows the panel exactly as `?name=` would.
 */
export function matchesProjectTerm(name: string, term: string): boolean {
  const needle = term.trim().toLocaleLowerCase();

  return needle === '' || name.toLocaleLowerCase().includes(needle);
}
