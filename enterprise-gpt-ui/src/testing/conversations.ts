import {
  CHAT_ROLE,
  ConversationDetailDto,
  ConversationDto,
  ConversationMessageDto,
} from '../app/domain/api/conversation';
import { PaginatedResponseDto } from '../app/domain/api/paginated-response';

let sequence = 0;
let messageSequence = 0;

/**
 * A `ConversationMessageDto` as `GET api/conversations/{id}/messages` returns one.
 *
 * Ids are distinct per call for the same reason conversation ids are: a repeated one
 * would let a spec pass while two messages shared a rating, which is precisely the bug
 * a per-message anchor exists to prevent.
 */
export function messageFixture(
  overrides: Partial<ConversationMessageDto> = {},
): ConversationMessageDto {
  const index = messageSequence++;

  return {
    id: `${String(index).padStart(8, '0')}-aaaa-4bbb-8ccc-dddddddddddd`,
    text: `Message ${index}`,
    role: CHAT_ROLE.assistant,
    feedback: null,
    ...overrides,
  };
}

/** A `ConversationDto` exactly as `GET api/conversations/search` returns one. */
export function conversationFixture(overrides: Partial<ConversationDto> = {}): ConversationDto {
  const index = sequence++;

  return {
    // No modulo: a repeating id collapses entities silently (`setAllEntities` keeps
    // one, `addEntities` no-ops) and, once rendered, trips NG0955 on `@for … track id`.
    id: `${String(index).padStart(8, '0')}-1111-4222-8333-444444444444`,
    projectId: null,
    modelId: null,
    name: `Conversation ${index}`,
    isFavorite: false,
    dateCreated: '2026-08-11T09:00:00+00:00',
    dateModified: '2026-08-11T09:00:00+00:00',
    ...overrides,
  };
}

/** A `ConversationDetailDto` as `GET api/conversations/{id}` returns one. */
export function conversationDetailFixture(
  overrides: Partial<ConversationDetailDto> = {},
): ConversationDetailDto {
  return { ...conversationFixture(overrides), mcpServerIds: [], ...overrides };
}

/**
 * A `PaginatedResponseDto` envelope with the server's own arithmetic.
 *
 * `pageSize` echoes the clamped `take` and `currentPage` is `(skip / take) + 1`, both
 * computed server-side — a fixture that invents them would let a store pass against a
 * shape the API never sends.
 */
export function conversationPage(
  items: ConversationDto[],
  { totalCount = items.length, pageSize = 50, skip = 0 } = {},
): PaginatedResponseDto<ConversationDto> {
  return {
    items,
    totalCount,
    pageSize,
    currentPage: Math.floor(skip / pageSize) + 1,
    totalPages: Math.ceil(totalCount / pageSize),
  };
}
