/**
 * Mirrors `Enterprise.Gpt.Dto.Enums.PermissionIds`; every other permission is created per MCP
 * server at run time. Always test by id, never by the editable `name`. `Administrator` does not
 * imply `UploadFile`.
 */
export const PERMISSION_ID = {
  administrator: 'a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d',
  uploadFile: 'b1c2d3e4-f5a6-4b7c-8d9e-0f1a2b3c4d5e',
} as const;

/** An id from {@link PERMISSION_ID}, for call sites that must not accept a free string. */
export type KnownPermissionId = (typeof PERMISSION_ID)[keyof typeof PERMISSION_ID];
