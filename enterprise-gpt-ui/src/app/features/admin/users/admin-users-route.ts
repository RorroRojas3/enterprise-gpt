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
 * Why the directory states its order (US-1201).
 *
 * The order is named rather than left to be inferred from the absence of a control, and
 * the sentence carries the other thing frame `5a` gives the reader no way to discover:
 * that one field searches names and email addresses alike.
 *
 * **It no longer says the list cannot be reordered.** US-706 put `sort=` and `dir=` on
 * `GET api/users`, so the endpoint would honour a control — the directory simply has not
 * built one yet, and a caption claiming an incapacity the API does not have is worse than
 * one that only states the order. Adding the control is a follow-up on this tab, and it
 * retires this constant the way US-706 retired the conversations library's.
 *
 * It reads as the second half of a sentence the `<p>` beside it begins ("By last name."),
 * so it carries only what that statement leaves out.
 */
export const ORDER_EXPLANATION = 'Search covers names and email addresses.';

/**
 * The permission filter (US-1206).
 *
 * `permission`, not the API's own `permissionId`, for the reason {@link SIZE_PARAM}
 * records: the URL is a contract with the reader. The wire keeps `permissionId`.
 */
export const PERMISSION_PARAM = 'permission';

/** The label the filter's "no filter" option carries, as frame `5a` words it. */
export const ANY_PERMISSION_LABEL = 'Permission: any';

/**
 * Reads a `?permission=` into an id to look up, or `null` for no filter.
 *
 * A shape check and nothing more. {@link toPageSize} can restrict `?size=` to the set the
 * select offers because that set is a constant; permission ids are rows an administrator
 * creates, so whether one exists is a question only `GET api/permissions` can answer. The
 * screen settles it against the loaded catalog and reads an id the catalog does not list
 * as no filter — the same invariant, enforced one step later.
 */
export function toPermissionFilter(value: string | undefined): string | null {
  // Narrowed rather than trusted. `withComponentInputBinding()` hands over the raw
  // `Params` value, which is an **array** for a repeated key — `?permission=a&permission=b`
  // would call `.trim()` on it and throw inside the transform, failing the navigation.
  const trimmed = typeof value === 'string' ? value.trim() : '';

  return trimmed === '' ? null : trimmed;
}

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
