export interface PermissionDto {
  id: string;
  name: string;
  description?: string;
  mcpServerId?: string;
}

/**
 * Fixed id of the seeded Administrator permission. Must match the backend's
 * `PermissionIds.Administrator` (System.Text.Json serializes Guids lowercase).
 */
export const ADMINISTRATOR_PERMISSION_ID = 'a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d';
