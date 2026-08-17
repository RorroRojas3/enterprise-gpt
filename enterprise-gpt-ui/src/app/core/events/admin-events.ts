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
 * US-1207 and US-1208 add `modelCatalogChanged` and the MCP server events to this group.
 * Those have a second observer this one does not: the chat picker's root catalogue,
 * which must not go on offering a model an administrator has just retired.
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
  },
});
