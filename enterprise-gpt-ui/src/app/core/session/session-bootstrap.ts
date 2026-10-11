import { Injectable, inject } from '@angular/core';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { ProjectLookupStore } from '@core/projects/project-lookup-store';
import { SessionStore } from './session-store';

/**
 * `POST api/users/me` goes first: it provisions the user and warms the API's permission cache
 * the catalogs are read from. The rest load unawaited; every screen handles their absence.
 */
@Injectable({ providedIn: 'root' })
export class SessionBootstrap {
  private readonly _session = inject(SessionStore);
  private readonly _models = inject(ModelCatalogStore);
  private readonly _mcps = inject(McpCatalogStore);
  private readonly _conversations = inject(ConversationListStore);
  private readonly _projects = inject(ProjectLookupStore);

  /**
   * Idempotent: each store memoizes its own load.
   *
   * @returns False when `POST api/users/me` failed; route to the startup-failure screen.
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
