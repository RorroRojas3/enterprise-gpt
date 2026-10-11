/**
 * A conversation from `GET api/conversations/search`. With no `sort=`, the route returns newest
 * created first, not newest modified. Render the server's order; never re-sort a page.
 * `name` is generated server-side after the first turn.
 */
export interface ConversationDto {
  readonly id: string;
  readonly projectId: string | null;
  readonly modelId: string | null;
  readonly name: string;
  readonly isFavorite: boolean;
  /** ISO 8601 with offset. */
  readonly dateCreated: string;
  /** ISO 8601 with offset. Favoriting deliberately does not bump it server-side. */
  readonly dateModified: string;
}

/**
 * The only source of `mcpServerIds`. The `ConversationDto` that `.../messages` returns reports
 * `projectId`, `modelId` and `isFavorite` as null/null/false, so never hydrate them from it.
 */
export interface ConversationDetailDto extends ConversationDto {
  readonly mcpServerIds: readonly string[];
}

/**
 * Numeric: the API registers no `JsonStringEnumConverter`. `System` is filtered out
 * server-side, and the transcript never renders `Tool`.
 */
export const CHAT_ROLE = {
  system: 1,
  assistant: 2,
  user: 3,
  tool: 4,
} as const;

export type ChatRole = (typeof CHAT_ROLE)[keyof typeof CHAT_ROLE];

/**
 * PascalCase: the API converts this property alone to enum member names. The stored
 * transcript camel-cases them, which is a separate contract.
 */
export const MESSAGE_FEEDBACK_RATING = {
  up: 'Up',
  down: 'Down',
} as const;

export type MessageFeedbackRating =
  (typeof MESSAGE_FEEDBACK_RATING)[keyof typeof MESSAGE_FEEDBACK_RATING];

/** Nested so a comment can later sit beside the verdict without reshaping the message. */
export interface ConversationMessageFeedbackDto {
  readonly rating: MessageFeedbackRating;
  /** ISO 8601 with offset. */
  readonly dateModified: string;
}

/** An identity, never a credential: the download link is minted when the chip is clicked. */
export interface MessageAttachmentDto {
  readonly id: string;
  readonly name: string;
  /** Lower-cased, including the leading dot. */
  readonly extension: string;
  readonly mimeType: string;
  readonly size: number;
}

/**
 * `id` addresses a rating. The other wire fields (`tokens`, `usage`, ...) are deliberately
 * unread. `feedback` is optional because the API may omit it; readers normalize with `?? null`.
 */
export interface ConversationMessageDto {
  readonly id: string;
  readonly text: string;
  readonly role: ChatRole;
  readonly feedback?: ConversationMessageFeedbackDto | null;
  /** Optional for the same reason `feedback` is: a message that introduced none may omit it. */
  readonly attachments?: readonly MessageAttachmentDto[] | null;
}

/**
 * The stored transcript. Its own shape rather than a `ConversationDto`, so the wire's unreliable
 * fields (see {@link ConversationDetailDto}) are unreachable through the type.
 */
export interface ChatConversationDto {
  readonly id: string;
  readonly name: string;
  readonly messages: readonly ConversationMessageDto[];
}
