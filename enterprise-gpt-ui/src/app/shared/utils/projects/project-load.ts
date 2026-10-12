/** The server's clamp ceiling, so a drain takes the fewest round trips. */
export const PROJECT_PAGE_SIZE = 100;

/**
 * A fully materialised set lets the sort be correct rather than reorder one page. Past it the
 * surface states what it shows instead of passing the shortfall off as a complete list.
 */
export const PROJECT_LOAD_CEILING = 500;

/**
 * The server's `EF.Functions.Like(name, '%term%')` under a case-insensitive collation, shared so
 * the grid and the picker narrow exactly as `?name=` would.
 */
export function matchesProjectTerm(name: string, term: string): boolean {
  const needle = term.trim().toLocaleLowerCase();

  return needle === '' || name.toLocaleLowerCase().includes(needle);
}
