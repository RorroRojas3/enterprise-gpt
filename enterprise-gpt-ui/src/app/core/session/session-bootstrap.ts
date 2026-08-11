import { Injectable, inject } from '@angular/core';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
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
 * The catalogs are *not* awaited. They are two independent GETs whose absence each
 * screen already has to handle, and holding the startup shell on screen for them
 * would trade a rendered app for a spinner.
 *
 * A guard calls this rather than reaching into three stores itself, so EP-3 and EP-9
 * have one obvious place to add the conversation and project loads.
 */
@Injectable({ providedIn: 'root' })
export class SessionBootstrap {
  private readonly _session = inject(SessionStore);
  private readonly _models = inject(ModelCatalogStore);
  private readonly _mcps = inject(McpCatalogStore);

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

    return true;
  }
}
