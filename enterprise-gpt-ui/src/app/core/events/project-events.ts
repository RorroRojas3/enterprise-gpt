import { type } from '@ngrx/signals';
import { eventGroup } from '@ngrx/signals/events';
import { ProjectDto } from '@domain/api/project';

/**
 * Events describing server-confirmed changes to a project.
 *
 * The Events plugin earns its place here on reachability, as it does for
 * `conversationEvents`: `ProjectActionsStore` is root-scoped, while the grid's
 * `ProjectListStore` and the detail screen's `ProjectStore` are both scoped to their
 * routes — and a delete has to reach a *third* store in another feature entirely
 * (`ConversationListStore`, see {@link projectEvents.deleted}). Four observers across
 * three scopes is exactly the case a method call cannot serve.
 *
 * Every payload is **server-confirmed**. Optimistic patches and their rollbacks stay
 * inside the store that made them, so an observer never has to undo one.
 */
export const projectEvents = eventGroup({
  source: 'Projects',
  events: {
    /**
     * `POST api/projects` returned 201; the payload is the DTO it created (US-903).
     * Creation is never optimistic — it mints a server id — so this is the first
     * moment any list may show the row.
     */
    created: type<ProjectDto>(),

    /**
     * `PUT api/projects/{id}` returned 200; the payload is the DTO it returned, with
     * the server's own `dateModified` rather than a local guess. Dispatched by the
     * rename/describe flow and by US-904's instructions save.
     */
    updated: type<ProjectDto>(),

    /**
     * `DELETE api/projects/{id}` returned 204. The payload is the project id.
     *
     * This one carries an obligation beyond removing a card. The server soft-deletes
     * the project and its documents but **releases its conversations**, setting their
     * `ProjectId` to null — so every client-side conversation still holding that id is
     * now holding a dead one. `toUpdateBody` echoes `projectId` on a rename, so a
     * conversation left un-released would PUT a deleted project id and take a 404 on
     * the next rename. `ConversationListStore` and `ConversationStore` both clear it.
     */
    deleted: type<string>(),
  },
});
