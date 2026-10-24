import { computed } from '@angular/core';
import { PartialStateUpdater, signalStoreFeature, withComputed, withState } from '@ngrx/signals';
import { PaginatedResponseDto } from '@shared/models/api/paginated-response.model';

/** The server clamps `take` to 1–100, so a larger request silently gets less. */
export const MAX_PAGE_SIZE = 100;

export interface OffsetPaginationState {
  /** Offset for the **next** request: how many items have already been loaded. */
  readonly skip: number;
  readonly take: number;
  /** Total matching rows the server reports, across all pages. */
  readonly totalCount: number;
}

/**
 * `skip` advances by items returned and is compared to `totalCount`, so short, clamped and
 * exact-multiple final pages need no special case. `isFullyLoaded` reads true before any fetch;
 * gate it on `isFulfilled` where that matters.
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
 * Records a loaded page. An empty page caps `totalCount` at the offset reached; otherwise a total
 * the page query disagrees with keeps `hasMore` true and a drain loops forever.
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
 * Records the first page of a fresh query, adopting the server's page size and dropping the old
 * offset, which would leave a narrowed search empty.
 */
export function setFirstPage<T>(
  response: PaginatedResponseDto<T>,
): PartialStateUpdater<OffsetPaginationState> {
  return ({ take }) => ({
    skip: response.items.length,
    // A server that reports a nonsensical page size must not wedge the drain at a
    // zero-length step.
    take: response.pageSize > 0 ? clampPageSize(response.pageSize) : take,
    // Capped as in `setPage`, or an empty first page costs a wasted request and briefly reads
    // as an incomplete set.
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
