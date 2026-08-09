/**
 * The envelope every paginated API list is wrapped in.
 *
 * Mirrors `Enterprise.Gpt.Dto.PaginatedResponseDto<T>` verbatim. `totalPages` is a
 * computed property server-side and is included here because it is on the wire,
 * but pagination state is derived from `totalCount` rather than from it — see
 * `withOffsetPagination`.
 *
 * `take` is clamped server-side to 1–100, so a requested page size is a hint the
 * server may reduce; `pageSize` is the size it actually used.
 *
 * `items` is deliberately a mutable `T[]`. It is a deserialized wire shape with no
 * immutability to protect, and `readonly T[]` does not satisfy the entity updaters'
 * `Entity[]` parameter — which would force a defensive copy at every
 * `setAllEntities(response.items)`.
 */
export interface PaginatedResponseDto<T> {
  readonly items: T[];
  readonly totalCount: number;
  readonly pageSize: number;
  /** One-based. */
  readonly currentPage: number;
  readonly totalPages: number;
}
