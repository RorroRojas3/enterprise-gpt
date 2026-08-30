import { type } from '@ngrx/signals';
import { eventGroup } from '@ngrx/signals/events';
import { McpCredentialStatusDto } from '@domain/api/mcp';

/**
 * Events describing server-confirmed changes to the caller's own MCP credentials.
 *
 * Its own group rather than a member of `adminEvents`: nothing here is administrative.
 * A user saving their GitHub token changes what *they* may select, and no other user's
 * catalogue moves.
 *
 * The payload is what the API returned, so an observer adopts a fact rather than a
 * guess — and it carries enough to patch one row, which is why this does not force the
 * refetch `adminEvents.mcpCatalogChanged` does.
 */
export const mcpEvents = eventGroup({
  source: 'Mcp',
  events: {
    /**
     * `PUT api/mcps/{id}/credential` returned 200, or `DELETE` returned 204 (in which
     * case the payload is the empty status the client builds for that id, since a 204
     * carries no body and the request was an unconditional removal).
     */
    credentialChanged: type<McpCredentialStatusDto>(),
  },
});
