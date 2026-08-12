/**
 * A conversation as `GET api/conversations/search` returns it.
 *
 * Mirrors `Enterprise.Gpt.Dto.ConversationDto`. The search route orders by
 * **`dateCreated` descending**, not `dateModified` — a rename or a new turn does not
 * move a row to the top — so "newest first" here means newest *created*, and the
 * client renders the server's order rather than re-sorting it.
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
