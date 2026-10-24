import { McpDto, McpServerDto } from '@shared/models/api/mcp.model';

/** A rejected key already reads as absent server-side, so this covers a refused key too. */
export function needsUserApiKey(server: McpDto): boolean {
  return server.requiresUserApiKey && !server.hasUserApiKey;
}

/** Named apart from `model-status.ts`'s `isRetired` because specs and fixtures import both. */
export function isMcpServerRetired(server: McpServerDto): boolean {
  return (server.dateDeactivated ?? null) !== null;
}
