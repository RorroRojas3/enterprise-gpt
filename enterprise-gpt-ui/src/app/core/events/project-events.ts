import { type } from '@ngrx/signals';
import { eventGroup } from '@ngrx/signals/events';
import { ProjectDto } from '@domain/api/project';

/** The payload of {@link projectEvents.favorited}: what one idempotent SET decided. */
export interface ProjectFavoriteChange {
  readonly id: string;
  readonly isFavorite: boolean;
}

/**
 * Server-confirmed project changes. Events, because the root `ProjectActionsStore` must reach
 * route-scoped stores in two features; optimistic rollbacks stay in the store that made them.
 */
export const projectEvents = eventGroup({
  source: 'Projects',
  events: {
    /** `POST api/projects` returned 201. Never optimistic: the server mints the id. */
    created: type<ProjectDto>(),

    /** `PUT api/projects/{id}` returned 200, with the server's own `dateModified`. */
    updated: type<ProjectDto>(),

    /**
     * `DELETE api/projects/{id}` returned 204; the payload is the id. The server releases its
     * conversations, so their stores must clear the id or the next rename PUTs a dead project.
     */
    deleted: type<string>(),

    /**
     * `PUT api/projects/{id}/favorite` returned 204. Not `updated` with a synthesized DTO: it
     * would invent `instructions`, which `ProjectStore` adopts wholesale and would blank.
     */
    favorited: type<ProjectFavoriteChange>(),
  },
});
