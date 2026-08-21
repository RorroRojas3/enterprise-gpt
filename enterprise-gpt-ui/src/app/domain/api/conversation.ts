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
 * One persisted message. `text` and `role` are the whole shape: there is no
 * id, no timestamp, and no usage until US-1101 adds them.
 */
export interface ConversationMessageDto {
  readonly text: string;
  readonly role: ChatRole;
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
