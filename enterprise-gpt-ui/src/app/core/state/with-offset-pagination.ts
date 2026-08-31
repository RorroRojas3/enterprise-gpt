import { computed } from '@angular/core';
import { PartialStateUpdater, signalStoreFeature, withComputed, withState } from '@ngrx/signals';
import { PaginatedResponseDto } from '@domain/api/paginated-response';

/**
 * The largest page the API will serve. `take` is clamped server-side to 1–100, so
 * asking for more silently gets less — and a drain that assumed it got `take`
 * items would never terminate.
 */
export const MAX_PAGE_SIZE = 100;

export interface OffsetPaginationState {
  /** Offset for the **next** request: how many items have already been loaded. */
  readonly skip: number;
  /** Page size to request. */
  readonly take: number;
  /** Total matching rows the server reports, across all pages. */
  readonly totalCount: number;
}

/**
 * Offset pagination over the API's `PaginatedResponseDto` envelope.
 *
 * `skip` advances by the number of items actually returned rather than by `take`,
 * and `hasMore` compares it against `totalCount` rather than reading the
 * envelope's `currentPage`/`totalPages`. Both choices exist for one reason: a
 * short final page, a page size the server clamped, and a `totalCount` that is an
 * exact multiple of the page size then all land on `hasMore === false` with no
 * special case.
 *
 * `isFullyLoaded` is the negation of `hasMore` and therefore reads `true` for a
 * store that has not fetched anything yet — offset state alone cannot tell "no
 * rows exist" from "nothing has been asked for". Compose `withRequestStatus` and
 * gate on `isFulfilled` where that distinction matters, as `withClientQuery`'s
 * `isAuthoritative` does.
 *
 * @param take Page size to request. Clamped to {@link MAX_PAGE_SIZE}.
 */
export function withOffsetPagination(take = MAX_PAGE_SIZE) {
  return signalStoreFeature(
    withState<OffsetPaginationState>({
      skip: 0,
      take: clampPageSize(take),
      totalCount: 0,
    }),
    withComputed(({ skip, totalCount }) => ({
      hasMore: computed(() => skip() < totalCount()),
      isFullyLoaded: computed(() => skip() >= totalCount()),
    })),
  );
}

/**
 * Records a loaded page: advances the offset and adopts the server's total.
 *
 * A page that returns nothing caps `totalCount` at the offset reached rather than
 * adopting the server's figure. Without that cap, a total that disagrees with the
 * page query — a count that sees rows the page does not, or concurrent inserts
 * shifting the window — leaves `skip` stationary and `hasMore` true, and a
 * `while (hasMore())` drain reissues the identical request forever.
 */
export function setPage<T>(
  response: PaginatedResponseDto<T>,
): PartialStateUpdater<OffsetPaginationState> {
  return ({ skip }) => {
    const nextSkip = skip + response.items.length;

    return {
      skip: nextSkip,
      totalCount:
        response.items.length === 0 ? Math.min(response.totalCount, nextSkip) : response.totalCount,
    };
  };
}

/**
 * Records the first page of a fresh query, discarding any previous offset and
 * adopting the page size the server actually used.
 *
 * Separate from {@link setPage} because a search that narrows the result set must
 * not carry the old offset forward — that is how a list comes back empty until the
 * user pages backwards.
 */
export function setFirstPage<T>(
  response: PaginatedResponseDto<T>,
): PartialStateUpdater<OffsetPaginationState> {
  return ({ take }) => ({
    skip: response.items.length,
    // A server that reports a nonsensical page size must not wedge the drain at a
    // zero-length step.
    take: response.pageSize > 0 ? clampPageSize(response.pageSize) : take,
    // Capped on an empty first page for the same reason {@link setPage} caps: a
    // total above a stationary offset leaves `hasMore` true, costing one wasted
    // request and briefly reporting an empty result as an incomplete set.
    totalCount:
      response.items.length === 0
        ? Math.min(response.totalCount, response.items.length)
        : response.totalCount,
  });
}

function clampPageSize(take: number): number {
  // NaN survives Math.min/Math.max, and a `take=NaN` query string is a 400 the
  // caller cannot explain.
  return Number.isFinite(take)
    ? Math.min(Math.max(Math.trunc(take), 1), MAX_PAGE_SIZE)
    : MAX_PAGE_SIZE;
}
