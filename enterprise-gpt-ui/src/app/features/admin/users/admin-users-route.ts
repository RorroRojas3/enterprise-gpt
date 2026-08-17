/** The query parameter the user directory searches on (US-1201). */
export const NAME_PARAM = 'name';

/** The 1-based page, matching `PaginatedResponseDto.currentPage`. */
export const PAGE_PARAM = 'page';

/**
 * The page size.
 *
 * `size`, not the API's own `take`. The URL is a contract with the reader rather than
 * a proxy for the request underneath it — the same reason the conversations library
 * spells its favourites filter `favorites=1` and not `isFavorite=true`.
 */
export const SIZE_PARAM = 'size';

/** The sizes frame `5a`'s "N / page" select offers. All inside the server's 1–100 clamp. */
export const USER_PAGE_SIZES = [25, 50, 100] as const;

/** The size a link that names none is read as. */
export const DEFAULT_USER_PAGE_SIZE = USER_PAGE_SIZES[0];

/**
 * Why the directory offers no sort control (US-1201).
 *
 * `GET api/users` orders by last name then first name and accepts no sort parameter,
 * so the PRD's regime **D** applies: server order over a *paged* set, stated rather
 * than left to be inferred from an absence. A control that reordered one page would
 * sort twenty-five rows out of three hundred and read as if it had sorted all of them.
 *
 * US-706 is the enabler that would replace this with a real control.
 */
export const ORDER_EXPLANATION =
  "Users are listed by last name. This list can't be reordered, and search covers names and email addresses.";

/**
 * Reads a `?page=` into a 1-based page number.
 *
 * Anything that is not a whole number at or above 1 reads as the first page: a
 * hand-edited `?page=0` would send `skip=-25`, and `?page=abc` a `skip=NaN` the caller
 * could not explain.
 */
export function toPage(value: string | undefined): number {
  const page = Number(value);

  return Number.isInteger(page) && page >= 1 ? page : 1;
}

/**
 * Reads a `?size=` into one of {@link USER_PAGE_SIZES}.
 *
 * Restricted to the offered set rather than merely clamped to the server's 1–100, so
 * the select always shows the size that is in force. A hand-edited `?size=7` would
 * otherwise leave the control displaying a value the URL contradicts — the same
 * reasoning that makes `?favorites=yes` read as off in the conversations library.
 */
export function toPageSize(value: string | undefined): number {
  const size = Number(value);

  return (USER_PAGE_SIZES as readonly number[]).includes(size) ? size : DEFAULT_USER_PAGE_SIZE;
}

export { shouldReplaceHistory } from '@core/navigation/search-history';
