import { UserDocumentDto } from '../app/domain/api/document';
import { PaginatedResponseDto } from '../app/domain/api/paginated-response';

let sequence = 0;

/** A `UserDocumentDto` exactly as `GET api/documents` returns one. */
export function userDocumentFixture(overrides: Partial<UserDocumentDto> = {}): UserDocumentDto {
  const index = sequence++;

  return {
    // No modulo: a repeating id collapses entities silently (`setAllEntities` keeps
    // one, `addEntities` no-ops) and, once rendered, trips NG0955 on `@for … track id`.
    id: `${String(index).padStart(8, '0')}-2222-4333-8444-555555555555`,
    conversationId: `${String(index).padStart(8, '0')}-aaaa-4bbb-8ccc-dddddddddddd`,
    name: `document-${index}.pdf`,
    extension: '.pdf',
    mimeType: 'application/pdf',
    size: 2_411_724,
    dateCreated: '2026-08-11T09:00:00+00:00',
    conversationName: `Conversation ${index}`,
    ...overrides,
  };
}

/**
 * A `PaginatedResponseDto` envelope with the server's own arithmetic.
 *
 * `pageSize` echoes the clamped `take` and `currentPage` is `(skip / take) + 1`, both
 * computed server-side — a fixture that invents them would let a store pass against a
 * shape the API never sends. The default `pageSize` is the documents drain's own
 * `take=100`, which is the server's clamp ceiling.
 */
export function documentPage(
  items: UserDocumentDto[],
  { totalCount = items.length, pageSize = 100, skip = 0 } = {},
): PaginatedResponseDto<UserDocumentDto> {
  return {
    items,
    totalCount,
    pageSize,
    currentPage: Math.floor(skip / pageSize) + 1,
    totalPages: Math.ceil(totalCount / pageSize),
  };
}
