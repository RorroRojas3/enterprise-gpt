import { PaginatedResponseDto } from '../app/domain/api/paginated-response';
import { PermissionDto, UserDto } from '../app/domain/api/user';
import { UPLOAD_FILE_GRANT } from './session';

let sequence = 0;

/**
 * A directory row exactly as `GET api/users` returns one.
 *
 * Distinct from `userFixture`, which is the *signed-in* user and has a fixed id: a list
 * needs as many unique ids as it has rows, because a repeated one collapses entities
 * silently and trips NG0955 once rendered.
 */
export function directoryUserFixture(overrides: Partial<UserDto> = {}): UserDto {
  const index = sequence++;
  const firstName = overrides.firstName ?? `First${index}`;
  const lastName = overrides.lastName ?? `Last${index}`;

  return {
    id: `${String(index).padStart(8, '0')}-2222-4333-8444-555555555555`,
    firstName,
    lastName,
    email: `user${index}@example.com`,
    permissions: [UPLOAD_FILE_GRANT],
    fullName: `${firstName} ${lastName}`.trim(),
    ...overrides,
  };
}

/** A grantable permission as `GET api/permissions` returns one. */
export function permissionFixture(overrides: Partial<PermissionDto> = {}): PermissionDto {
  return {
    id: '9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d',
    name: 'Voice input',
    description: null,
    isDefault: false,
    mcpServerId: null,
    ...overrides,
  };
}

/**
 * A `PaginatedResponseDto` envelope with the server's own arithmetic.
 *
 * `pageSize` echoes the clamped `take` and `currentPage` is `(skip / take) + 1`, both
 * computed server-side — a fixture that invents them would let a store pass against a
 * shape the API never sends. Pass the store's own page size: the default here is the
 * API's, not the screen's.
 */
export function userPage(
  items: UserDto[],
  { totalCount = items.length, pageSize = 20, skip = 0 } = {},
): PaginatedResponseDto<UserDto> {
  return {
    items,
    totalCount,
    pageSize,
    currentPage: Math.floor(skip / pageSize) + 1,
    totalPages: Math.ceil(totalCount / pageSize),
  };
}
