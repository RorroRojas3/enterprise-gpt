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
 * The query parameter the library orders on (US-706).
 *
 * One key, not the API's two, and it spells an *order* rather than a field: the control
 * is a single select and each option carries its own direction, so `?sort=oldest` is
 * what the reader chose while `sort=created&dir=asc` is what the request needs. It
 * replaces US-705's `ORDER_EXPLANATION`, which stated the order because no endpoint
 * accepted one.
 */
export const SORT_PARAM = 'sort';

/** One entry per option in the library's sort select (US-706). */
export interface ConversationSortOption {
  /** The `?sort=` value, and the option's DOM value. */
  readonly value: string;
  /** The label the select renders. */
  readonly label: string;
  /** What `GET api/conversations/search` is sent for this option. */
  readonly wire: { readonly sort: string; readonly dir: string };
}

/**
 * The orders the library offers.
 *
 * No favourites order, because the toolbar beside it already carries a favourites
 * *filter*, and no "recently active": `dateModified` moves on a rename, a star, a model
 * change and a move between projects as readily as on a turn, so an option built on it
 * would promise something the column cannot answer.
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

/**
 * The order a link that names none is read as.
 *
 * "Newest first" — the order US-705 used to state in prose, so a bare `/conversations`
 * is the screen it has always been.
 */
export const DEFAULT_CONVERSATION_SORT = NEWEST_FIRST;

/**
 * Reads a `?sort=` into one of {@link CONVERSATION_SORTS}.
 *
 * Restricted to the offered set rather than passed through, so the select always shows
 * the order in force and the server never sees a value it would answer with a 400 the
 * reader never asked for.
 */
export function toConversationSort(value: string | undefined): ConversationSortOption {
  return CONVERSATION_SORTS.find((option) => option.value === value) ?? DEFAULT_CONVERSATION_SORT;
}

/**
 * The value {@link SORT_PARAM} holds for an order, or `''` for the default.
 *
 * `''` rather than the default's own name because that is what the URL holds — the key is
 * removed, not spelled out — which is what lets `shouldReplaceHistory` read a sort change
 * on exactly the terms it reads a search term.
 */
export function sortKey(option: ConversationSortOption): string {
  return option.value === DEFAULT_CONVERSATION_SORT.value ? '' : option.value;
}

/**
 * Re-exported so this screen's route contract still reads from one file.
 *
 * The function itself moved to `core/navigation/` when US-901's projects grid needed
 * the same rule: `features → features` is not a direction this app has, and a second
 * copy is a second thing to get wrong.
 */
export { shouldReplaceHistory } from '@core/navigation/search-history';
