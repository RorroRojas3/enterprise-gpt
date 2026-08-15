import { Injectable, inject } from '@angular/core';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { ProjectLookupStore } from '@core/projects/project-lookup-store';
import { SessionStore } from './session-store';

/**
 * Resolves everything a signed-in user needs before a screen renders.
 *
 * The sequencing is the point (US-202). `POST api/users/me` is awaited first: it is
 * what self-provisions the user, returns their grants, and warms the API's own
 * permission cache, so a catalog request issued alongside it could be answered from
 * a cache that has not been populated yet. Only once it has returned are the
 * catalogs requested.
 *
 * The catalogs and the conversation list are *not* awaited. They are independent GETs
 * whose absence each screen already has to handle — the sidebar has a skeleton, an
 * empty state and an error panel of its own — and holding the startup shell on screen
 * for them would trade a rendered app for a spinner.
 *
 * A guard calls this rather than reaching into five stores itself. The project load
 * joined the others with US-307, which is the first surface to need a root project list:
 * the picker's searchable panel and the project chip both read it, and a chip cannot
 * name a project from `ConversationDto`'s bare `projectId` without it.
 */
@Injectable({ providedIn: 'root' })
export class SessionBootstrap {
  private readonly _session = inject(SessionStore);
  private readonly _models = inject(ModelCatalogStore);
  private readonly _mcps = inject(McpCatalogStore);
  private readonly _conversations = inject(ConversationListStore);
  private readonly _projects = inject(ProjectLookupStore);

  /**
   * Ensures the session is loaded and the catalog loads are under way.
   *
   * Idempotent: each store memoizes its own load, so calling this from every
   * navigation costs nothing after the first.
   *
   * @returns Whether the session resolved. False means `POST api/users/me` failed
   *   and the caller should route to the startup-failure screen.
   */
  async ensureSession(): Promise<boolean> {
    if (!(await this._session.ensureLoaded())) {
      return false;
    }

    void this._models.ensureLoaded();
    void this._mcps.ensureLoaded();
    this._conversations.ensureLoaded();
    this._projects.ensureLoaded();

    return true;
  }
}
