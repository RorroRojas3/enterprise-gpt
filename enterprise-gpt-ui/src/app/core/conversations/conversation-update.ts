import { ConversationDto } from '@domain/api/conversation';

/** The body `PUT api/conversations` expects. The id travels in the body, not the route. */
export interface ConversationUpdateBody {
  readonly id: string;
  readonly name: string;
  readonly projectId: string | null;
}

/** The fields an update may change; anything omitted is echoed from the current row. */
export interface ConversationChanges {
  readonly name?: string;
  readonly projectId?: string | null;
}

/**
 * Builds the `PUT api/conversations` body for a conversation update.
 *
 * The endpoint is a full-representation PUT: a body that omits `projectId` — or sends
 * null — removes the conversation from its project. Every update path goes through
 * this one builder so no caller can unlink a project by forgetting to echo the field
 * (US-307's stated invariant, in force since US-304, the first update path).
 *
 * Only an explicit `null` unlinks. An `undefined` — including one passed explicitly,
 * as `{ projectId: selectedProject()?.id }` naturally produces — echoes the current
 * value, because "looks omitted" behaving as "remove" is exactly the accident this
 * builder exists to prevent.
 *
 * @param current The conversation as last seen from the server.
 * @param changes The fields this update means to change.
 */
export function toUpdateBody(
  current: ConversationDto,
  changes: ConversationChanges = {},
): ConversationUpdateBody {
  return {
    id: current.id,
    name: changes.name ?? current.name,
    projectId: changes.projectId === undefined ? current.projectId : changes.projectId,
  };
}
