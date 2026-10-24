import { ConversationDto } from '@core/api/conversation.model';

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
 * The endpoint is a full-representation PUT, so every update goes through here: an omitted
 * `projectId` would unlink. Only an explicit `null` unlinks; `undefined`, as
 * `{ projectId: selectedProject()?.id }` produces, echoes the current value.
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
