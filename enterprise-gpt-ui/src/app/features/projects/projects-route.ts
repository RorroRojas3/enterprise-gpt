/** The query parameter the projects grid filters on. */
export const NAME_PARAM = 'name';

/**
 * One key, not the API's two: each option carries its direction, and the URL is a contract with
 * the reader, not a proxy for the request.
 */
export const SORT_PARAM = 'sort';

/** One option in the grid's sort select. */
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

const BY_UPDATED: ProjectSortOption = {
  value: 'updated',
  label: 'Last updated',
  wire: { sort: 'updated', dir: 'desc' },
  order: 'most recently updated first',
};

/**
 * The design's "Pinned" is spelled Favourites: the row carries a server favourite flag, and
 * "Pinned" beside a card's "Unfavourite" star would be inconsistent.
 */
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
 * Not the server's own default: the cards render "Updated …", so the request always spells the
 * order out.
 */
export const DEFAULT_PROJECT_SORT = BY_UPDATED;

/**
 * Restricted to the offered set so the select always shows the order in force, and a hand-edited
 * `?sort=zzz` never earns a 400.
 */
export function toProjectSort(value: string | undefined): ProjectSortOption {
  return PROJECT_SORTS.find((option) => option.value === value) ?? DEFAULT_PROJECT_SORT;
}

/**
 * `''` for the default, whose key is removed from the URL, so `shouldReplaceHistory` reads a sort
 * change exactly as it reads a search term.
 */
export function sortKey(option: ProjectSortOption): string {
  return option.value === DEFAULT_PROJECT_SORT.value ? '' : option.value;
}

/** Re-exported so this screen's route contract reads from one file. */
export { shouldReplaceHistory } from '@core/navigation/search-history';
