import { type } from '@ngrx/signals';
import { eventGroup } from '@ngrx/signals/events';
import { UserDto } from '@domain/api/user';

/**
 * Events describing server-confirmed changes made from the administration area.
 *
 * The Events plugin earns its place here on scope, as it does for `projectEvents`:
 * `UserActionsStore` is root-scoped so a request survives a tab change, while the
 * directory's `AdminUsersStore` is scoped to its own route — and `core/` may not import
 * from `features/`, so a method call is not available in that direction.
 *
 * Every payload is **server-confirmed**. Optimistic patches and their rollbacks stay
 * inside the store that made them, so an observer never has to undo one.
 *
 * US-1208 adds the MCP server events to this group.
 */
export const adminEvents = eventGroup({
  source: 'Admin',
  events: {
    /**
     * `POST api/users` returned 201; the payload is the DTO it created (US-1202).
     *
     * Creation is never optimistic — the row's id is the Entra object id, which only
     * Microsoft Graph can supply — so this is the first moment any list may show it.
     */
    userCreated: type<UserDto>(),

    /**
     * `PUT api/users/{id}` returned 200; the payload is the DTO it returned, with the
     * permission set the server actually stored rather than the one that was asked for
     * (US-1203).
     */
    userUpdated: type<UserDto>(),

    /** `DELETE api/users/{id}` returned 204. The payload is the user id (US-1204). */
    userDeactivated: type<string>(),

    /**
     * A model was created, updated or retired, and the server confirmed it (US-1207).
     *
     * **Void, and every observer refetches** — unlike the three user events, which carry
     * the DTO and patch a row in place. Three facts make a payload the wrong shape here:
     *
     * - `POST` and `PUT` return only the model they saved, while saving one with
     *   `isDefault` **demotes another row the response does not describe**;
     * - `DELETE` additionally clears `IsDefault` on the row it retires, so the catalog
     *   can be left with no default at all;
     * - the catalog is unpaginated and small, so a refetch costs one request and is the
     *   only thing that can show either of the above.
     *
     * This event has a second observer the user events do not: `ModelCatalogStore`, the
     * chat picker's root catalogue, which must not go on offering a model an
     * administrator has just retired.
     */
    modelCatalogChanged: type<void>(),
  },
});
