/** The query parameter the conversations library filters on. */
export const NAME_PARAM = 'name';

/**
 * `favorites=1`, not the API's `isFavorite=true`: a URL able to spell `isFavorite=false` would
 * invite links meaning something this screen cannot honour.
 */
export const FAVORITES_PARAM = 'favorites';

/** The only value {@link FAVORITES_PARAM} is ever written with. */
export const FAVORITES_ON = '1';

/** One key naming an order (`?sort=oldest`), not the API's field-and-direction pair. */
export const SORT_PARAM = 'sort';

/** One option in the library's sort select. */
export interface ConversationSortOption {
  /** The `?sort=` value, and the option's DOM value. */
  readonly value: string;
  readonly label: string;
  /** What `GET api/conversations/search` is sent for this option. */
  readonly wire: { readonly sort: string; readonly dir: string };
}

/**
 * No favourites order (the toolbar has a favourites filter) and no "recently active":
 * `dateModified` moves on renames, stars and project moves as readily as on a turn.
 */
const NEWEST_FIRST: ConversationSortOption = {
  value: 'newest',
  label: 'Newest first',
  wire: { sort: 'created', dir: 'desc' },
};

export const CONVERSATION_SORTS: readonly ConversationSortOption[] = [
  NEWEST_FIRST,
  { value: 'oldest', label: 'Oldest first', wire: { sort: 'created', dir: 'asc' } },
  { value: 'name', label: 'Alphabetical', wire: { sort: 'name', dir: 'asc' } },
];

/** The order a link that names none is read as. */
export const DEFAULT_CONVERSATION_SORT = NEWEST_FIRST;

/**
 * Reads a `?sort=` into one of {@link CONVERSATION_SORTS}, falling back to the default, so the
 * select always shows the order in force and the server never gets a value it would 400.
 */
export function toConversationSort(value: string | undefined): ConversationSortOption {
  return CONVERSATION_SORTS.find((option) => option.value === value) ?? DEFAULT_CONVERSATION_SORT;
}

/**
 * The value {@link SORT_PARAM} holds for an order, or `''` for the default, whose key is removed
 * from the URL; `shouldReplaceHistory` then reads a sort change like a search term.
 */
export function sortKey(option: ConversationSortOption): string {
  return option.value === DEFAULT_CONVERSATION_SORT.value ? '' : option.value;
}

/** Re-exported so this screen's route contract reads from one file. */
export { shouldReplaceHistory } from '@core/navigation/search-history';
