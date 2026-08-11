/**
 * The two permissions that exist at compile time, mirroring
 * `Enterprise.Gpt.Dto.Enums.PermissionIds`.
 *
 * Every other permission in the system is created per MCP server at run time, so the
 * set as a whole is not enumerable client-side. Membership is therefore always tested
 * by **id** against the caller's own grants — never by `name`, which is display text
 * an administrator can edit.
 *
 * `Administrator` grants nothing else implicitly. An administrator who does not hold
 * `UploadFile` may not upload, and the client must check the two independently
 * (US-204).
 */
export const PERMISSION_ID = {
  administrator: 'a0b1c2d3-e4f5-4a6b-8c7d-9e0f1a2b3c4d',
  uploadFile: 'b1c2d3e4-f5a6-4b7c-8d9e-0f1a2b3c4d5e',
} as const;

/** An id from {@link PERMISSION_ID}, for call sites that must not accept a free string. */
export type KnownPermissionId = (typeof PERMISSION_ID)[keyof typeof PERMISSION_ID];
