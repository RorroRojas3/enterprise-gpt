import { PERMISSION_ID } from '../app/domain/api/permission-ids';
import { PermissionDto, UserDto } from '../app/domain/api/user';

/** The `Upload File` grant, seeded as a default for every user. */
export const UPLOAD_FILE_GRANT: PermissionDto = {
  id: PERMISSION_ID.uploadFile,
  name: 'Upload File',
  description: 'Upload documents into a conversation or a project.',
  isDefault: true,
  mcpServerId: null,
};

/** The `Administrator` grant. Never a default. */
export const ADMINISTRATOR_GRANT: PermissionDto = {
  id: PERMISSION_ID.administrator,
  name: 'Administrator',
  description: 'Full administrative access.',
  isDefault: false,
  mcpServerId: null,
};

/** A grant created for an MCP server — dynamic, and matchable only by id. */
export const MCP_GRANT: PermissionDto = {
  id: '3f2a1b0c-9d8e-4c7b-a6f5-e4d3c2b1a098',
  name: 'Weather MCP',
  description: null,
  isDefault: false,
  mcpServerId: 'a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d',
};

/**
 * A `UserDto` exactly as `POST api/users/me` returns it.
 *
 * `permissions` defaults to the seeded `Upload File` grant alone; pass `[]` for the
 * zero-grants case US-202 requires to be a success rather than a failure.
 */
export function userFixture(overrides: Partial<UserDto> = {}): UserDto {
  const firstName = overrides.firstName ?? 'Ada';
  const lastName = overrides.lastName ?? 'Lovelace';

  return {
    id: 'f1e2d3c4-b5a6-4978-8a9b-0c1d2e3f4a5b',
    firstName,
    lastName,
    email: 'ada.lovelace@example.com',
    permissions: [UPLOAD_FILE_GRANT],
    fullName: `${firstName} ${lastName}`.trim(),
    ...overrides,
  };
}

/** A user who additionally holds `Administrator`. */
export function administratorFixture(overrides: Partial<UserDto> = {}): UserDto {
  return userFixture({ permissions: [UPLOAD_FILE_GRANT, ADMINISTRATOR_GRANT], ...overrides });
}
