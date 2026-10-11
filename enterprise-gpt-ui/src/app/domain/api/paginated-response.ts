/**
 * Pagination state derives from `totalCount`, not `totalPages`. The server clamps `take` to
 * 1–100, so `pageSize` is the size it actually used.
 */
export interface PaginatedResponseDto<T> {
  /** Mutable, since `readonly` would force a copy at every `setAllEntities(response.items)`. */
  readonly items: T[];
  readonly totalCount: number;
  readonly pageSize: number;
  /** One-based. */
  readonly currentPage: number;
  readonly totalPages: number;
}
