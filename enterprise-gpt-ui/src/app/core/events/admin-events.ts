import { type } from '@ngrx/signals';
import { eventGroup } from '@ngrx/signals/events';
import { UserDto } from '@shared/models/api/user.model';

/**
 * Server-confirmed admin changes. Events, because the root `UserActionsStore` cannot inject the
 * route-scoped `AdminUsersStore`, which exists only while its page is open; optimistic rollbacks
 * stay in their store.
 */
export const adminEvents = eventGroup({
  source: 'Admin',
  events: {
    /** `POST api/users` returned 201. Never optimistic: only Graph can supply the Entra id. */
    userCreated: type<UserDto>(),

    /** `PUT api/users/{id}` returned 200, with the permissions the server actually stored. */
    userUpdated: type<UserDto>(),

    /** `DELETE api/users/{id}` returned 204. The payload is the user id. */
    userDeactivated: type<string>(),

    /**
     * A model was saved or retired. Void, so observers refetch: saving a default demotes a row
     * the response omits, and retiring one can leave no default.
     */
    modelCatalogChanged: type<void>(),

    /**
     * An MCP server was saved or deactivated. Void, so observers refetch: the list is ordered by
     * name, the 204 carries no `dateDeactivated`, and a rename also renames its permission.
     */
    mcpCatalogChanged: type<void>(),
  },
});
