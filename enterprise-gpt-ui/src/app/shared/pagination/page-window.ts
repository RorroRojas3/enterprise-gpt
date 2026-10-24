/** A gap in the page list, rendered as an ellipsis. */
export const PAGE_GAP = 'gap' as const;

export type PageWindowEntry = number | typeof PAGE_GAP;

/**
 * The page numbers to render for 1-based `current`, keeping `edge` pages at each end and `around`
 * either side, with gaps where the run breaks.
 */
export function pageWindow(
  current: number,
  total: number,
  edge = 1,
  around = 1,
): readonly PageWindowEntry[] {
  if (total <= 0) {
    return [];
  }

  const shown = new Set<number>();
  for (let page = 1; page <= Math.min(edge, total); page++) {
    shown.add(page);
  }
  for (let page = Math.max(1, total - edge + 1); page <= total; page++) {
    shown.add(page);
  }
  for (
    let page = Math.max(1, current - around);
    page <= Math.min(total, current + around);
    page++
  ) {
    shown.add(page);
  }

  const pages = [...shown].sort((a, b) => a - b);
  const entries: PageWindowEntry[] = [];

  pages.forEach((page, index) => {
    const previous = pages[index - 1];
    if (previous !== undefined) {
      // A hole of exactly one page renders as that page: an ellipsis hiding a single
      // number is longer than the number and hides a click.
      if (page - previous === 2) {
        entries.push(previous + 1);
      } else if (page - previous > 2) {
        entries.push(PAGE_GAP);
      }
    }
    entries.push(page);
  });

  return entries;
}
