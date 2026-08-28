/**
 * A conversation as `GET api/conversations/search` returns it.
 *
 * Mirrors `Enterprise.Gpt.Dto.ConversationDto`. Asked for no order the search route
 * returns **`dateCreated` descending**, not `dateModified` — a rename or a new turn does
 * not move a row to the top — so "newest first" there means newest *created*. Since US-706
 * it also accepts `sort=` and `dir=`: the library screen sends the reader's choice, the
 * sidebar sends none. Either way the client renders the server's order and never re-sorts
 * a page.
 *
 * `modelId` and `projectId` are nullable on a conversation that has never had a turn
 * or belongs to no project. `name` is server-generated after the first turn.
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
 * One conversation as `GET api/conversations/{id}` returns it.
 *
 * The only source of `mcpServerIds`, which US-410 restores into the MCP picker.
 * `GET api/conversations/{id}/messages` returns a `ConversationDto` shape too, but
 * builds it from the Cosmos document and leaves `projectId`, `modelId` and
 * `isFavorite` at null/null/false regardless of the truth — so it must never be used
 * to hydrate any of the three.
 */
export interface ConversationDetailDto extends ConversationDto {
  readonly mcpServerIds: readonly string[];
}

/**
 * The role a stored message was written under.
 *
 * Numeric because the API registers no `JsonStringEnumConverter`, so
 * `Enterprise.Gpt.Common.Enums.ChatRoles` goes onto the wire as its integer
 * value. `System` never appears — the server filters it out of the response —
 * and `Tool` is a model-to-model artefact the transcript does not render.
 */
export const CHAT_ROLE = {
  system: 1,
  assistant: 2,
  user: 3,
  tool: 4,
} as const;

export type ChatRole = (typeof CHAT_ROLE)[keyof typeof CHAT_ROLE];

/**
 * The two verdicts a reader can record against an assistant message (US-1102).
 *
 * PascalCase because the API applies a string-enum converter to this property alone, and
 * its parameterless form writes the enum member names as declared. The transcript
 * document underneath stores the same values camel-cased; that is storage, this is the
 * wire, and they are separate contracts.
 */
export const MESSAGE_FEEDBACK_RATING = {
  up: 'Up',
  down: 'Down',
} as const;

export type MessageFeedbackRating =
  (typeof MESSAGE_FEEDBACK_RATING)[keyof typeof MESSAGE_FEEDBACK_RATING];

/**
 * A rating already recorded against a message (US-1102).
 *
 * A nested object rather than a bare field, so a later story can add a comment beside the
 * verdict without changing the message shape.
 */
export interface ConversationMessageFeedbackDto {
  readonly rating: MessageFeedbackRating;
  /** ISO 8601 with offset. */
  readonly dateModified: string;
}

/**
 * One file an assistant message introduced.
 *
 * An identity and never a credential: the download route mints a short-lived link when the
 * chip is clicked, so nothing here expires and nothing here is worth intercepting.
 */
export interface MessageAttachmentDto {
  readonly id: string;
  readonly name: string;
  /** Lower-cased, including the leading dot. */
  readonly extension: string;
  readonly mimeType: string;
  readonly size: number;
}

/**
 * One persisted message.
 *
 * `id` is the anchor a rating is addressed to (US-1101), and the only reason it is read
 * here. The rest of what US-1101 put on the wire — `dateCreated`, `htmlContent`,
 * `tokens`, `tokenAccuracy` and the turn's `usage` — stays deliberately unread: a
 * replayed turn still shows no token counts, and closing that belongs to US-1101's own
 * frontend half rather than to this story.
 *
 * `feedback` is optional as well as nullable because the API omits nothing today but is
 * free to; every reader normalizes it with `?? null`.
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
 * The stored transcript from `GET api/conversations/{id}/messages` (US-410).
 *
 * It extends `ConversationDto` on the wire, but only {@link messages} may be
 * read from it: the rest is rebuilt from the Cosmos document, which reports
 * `projectId`, `modelId` and `isFavorite` as null/null/false whatever the
 * truth — see the warning on {@link ConversationDetailDto}. Typed as its own
 * shape rather than an extension so that trap is unreachable through the type.
 */
export interface ChatConversationDto {
  readonly id: string;
  readonly name: string;
  readonly messages: readonly ConversationMessageDto[];
}
