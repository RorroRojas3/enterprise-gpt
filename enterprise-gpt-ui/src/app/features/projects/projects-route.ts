/** The query parameter the projects grid filters on (US-901). */
export const NAME_PARAM = 'name';

/**
 * The query parameter the projects grid orders on (US-902).
 *
 * One key, not the API's two. The select has a single value and frame `4c` draws one
 * option per order, so a direction is a property of the option rather than a control of
 * its own — the same reasoning that makes the URL spell `size` where the API says
 * `take`: the URL is a contract with the reader, not a proxy for the request underneath
 * it.
 */
export const SORT_PARAM = 'sort';

/**
 * One entry per option in frame `4c`'s sort select.
 *
 * `wire` is what `GET api/projects` is asked for; `order` completes the ceiling notice,
 * so a truncated grid names the order it truncated rather than claiming a recency it may
 * not be in.
 */
export interface ProjectSortOption {
  /** The `?sort=` value, and the option's DOM value. */
  readonly value: string;
  /** The label the select renders. */
  readonly label: string;
  /** What the API is sent for this option. */
  readonly wire: { readonly sort: string; readonly dir: string };
  /** The clause the ceiling notice ends with. */
  readonly order: string;
}

/**
 * The orders the grid offers, in the order frame `4c` lists them.
 *
 * The board's fourth option reads "Pinned". It is spelled **Favourites** here, because
 * device-local pins were never built — US-909 put a server flag on the row and US-910
 * put a *Favourite projects* section in the sidebar — and a control saying "Pinned"
 * beside a card star saying "Unfavourite Helios" is the inconsistency the conversations
 * toolbar already decided against.
 */
const BY_UPDATED: ProjectSortOption = {
  value: 'updated',
  label: 'Last updated',
  wire: { sort: 'updated', dir: 'desc' },
  order: 'most recently updated first',
};

export const PROJECT_SORTS: readonly ProjectSortOption[] = [
  BY_UPDATED,
  {
    value: 'created',
    label: 'Recently created',
    wire: { sort: 'created', dir: 'desc' },
    order: 'most recently created first',
  },
  {
    value: 'name',
    label: 'Alphabetical',
    wire: { sort: 'name', dir: 'asc' },
    order: 'A–Z',
  },
  {
    value: 'favorites',
    label: 'Favourites first',
    wire: { sort: 'favorite', dir: 'desc' },
    order: 'favourites first',
  },
];

/**
 * The order a link that names none is read as — frame `4c`'s selected option.
 *
 * It is deliberately *not* the order `GET api/projects` returns when asked for none: the
 * cards render "Updated …", so the grid's own default is the one the cards describe. The
 * request therefore always spells the order out rather than relying on the server's.
 */
export const DEFAULT_PROJECT_SORT = BY_UPDATED;

/**
 * Reads a `?sort=` into one of {@link PROJECT_SORTS}.
 *
 * Restricted to the offered set rather than passed through, so the select always shows
 * the order that is in force: a hand-edited `?sort=zzz` would otherwise leave the
 * control displaying a value the URL contradicts, and would take a 400 from the server
 * for a request the reader never made. The same reasoning restricts `?size=` in the
 * admin directory.
 */
export function toProjectSort(value: string | undefined): ProjectSortOption {
  return PROJECT_SORTS.find((option) => option.value === value) ?? DEFAULT_PROJECT_SORT;
}

/**
 * The value {@link SORT_PARAM} holds for an order, or `''` for the default.
 *
 * `''` rather than the default's own name because that is what the URL holds — the key is
 * removed, not spelled out — which is what lets `shouldReplaceHistory` read a sort change
 * on exactly the terms it reads a search term: an order turned on or off is an add or a
 * remove, and one order swapped for another is a refinement.
 */
export function sortKey(option: ProjectSortOption): string {
  return option.value === DEFAULT_PROJECT_SORT.value ? '' : option.value;
}

/**
 * Re-exported so this screen's route contract reads from one file, as the
 * conversations library's does. The rule itself is shared — see
 * `core/navigation/search-history.ts`.
 */
export { shouldReplaceHistory } from '@core/navigation/search-history';
