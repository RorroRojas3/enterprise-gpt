import { type } from '@ngrx/signals';
import { eventGroup } from '@ngrx/signals/events';
import { McpCredentialStatusDto } from '@shared/models/api/mcp.model';

/**
 * The caller's own MCP credential changes, which are not administrative. The payload patches
 * one row, so unlike `adminEvents.mcpCatalogChanged` it needs no refetch.
 */
export const mcpEvents = eventGroup({
  source: 'Mcp',
  events: {
    /** The saved status, or for `DELETE`'s bodiless 204 the empty status built for that id. */
    credentialChanged: type<McpCredentialStatusDto>(),
  },
});
