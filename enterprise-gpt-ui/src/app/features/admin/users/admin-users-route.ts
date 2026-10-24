export const NAME_PARAM = 'name';

/** The 1-based page, matching `PaginatedResponseDto.currentPage`. */
export const PAGE_PARAM = 'page';

/** `size`, not the API's `take`: the URL is a contract with the reader, not a request proxy. */
export const SIZE_PARAM = 'size';

/** The sizes the "N / page" select offers, all inside the server's 1–100 clamp. */
export const USER_PAGE_SIZES = [25, 50, 100] as const;

export const DEFAULT_USER_PAGE_SIZE = USER_PAGE_SIZES[0];

/**
 * Completes the "By last name." sentence beside it. It must not claim the list cannot be
 * reordered: the API honours `sort=` and `dir=`, and only the control is missing.
 */
export const ORDER_EXPLANATION = 'Search covers names and email addresses.';

/** `permission`, not the API's `permissionId`, for the reason {@link SIZE_PARAM} gives. */
export const PERMISSION_PARAM = 'permission';

export const ANY_PERMISSION_LABEL = 'Permission: any';

/**
 * A shape check only: permission ids are admin-created rows, so the screen reads an id the loaded
 * catalog does not list as no filter.
 */
export function toPermissionFilter(value: string | undefined): string | null {
  // Narrowed, not trusted: input binding passes an array for a repeated key, and `.trim()` on it
  // would throw and fail the navigation.
  const trimmed = typeof value === 'string' ? value.trim() : '';

  return trimmed === '' ? null : trimmed;
}

/** Anything but a whole number ≥ 1 reads as page 1, so `skip` is never negative or NaN. */
export function toPage(value: string | undefined): number {
  const page = Number(value);

  return Number.isInteger(page) && page >= 1 ? page : 1;
}

/** Restricted to the offered set, not clamped, so the select always shows the size in force. */
export function toPageSize(value: string | undefined): number {
  const size = Number(value);

  return (USER_PAGE_SIZES as readonly number[]).includes(size) ? size : DEFAULT_USER_PAGE_SIZE;
}
