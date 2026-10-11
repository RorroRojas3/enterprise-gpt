/**
 * Refining a term replaces; adding or removing one pushes, so Back restores the unfiltered list
 * without walking back through every keystroke.
 */
export function shouldReplaceHistory(previous: string, next: string): boolean {
  return previous !== '' && next !== '';
}
