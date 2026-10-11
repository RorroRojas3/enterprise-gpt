import { HttpTestingController } from '@angular/common/http/testing';
import { PaginatedResponseDto } from '../app/domain/api/paginated-response';
import { PermissionDto, UserDto } from '../app/domain/api/user';
import { ADMINISTRATOR_GRANT, UPLOAD_FILE_GRANT } from './session';

let sequence = 0;

/**
 * A directory row as `GET api/users` returns one. Unlike `userFixture`, ids are unique per call,
 * since a repeated one collapses entities and trips NG0955.
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
 * Flushes the permission catalog with rows: `ensurePermissions()` reads `[]` as not loaded, so a
 * later form would issue a second request and fail `verify()`.
 */
export function flushPermissions(
  backend: HttpTestingController,
  permissions: PermissionDto[] = [
    permissionFixture(),
    permissionFixture({ id: ADMINISTRATOR_GRANT.id, name: ADMINISTRATOR_GRANT.name }),
  ],
  { optional = false }: { optional?: boolean } = {},
): number {
  // `match`, not `expectOne`: the root store caches the catalog, so a second mount sends none and
  // passes `optional`. Otherwise zero requests throws, or a screen that stopped asking would pass.
  const requests = backend.match((request) => request.url.endsWith('/api/permissions'));

  if (!optional && requests.length === 0) {
    throw new Error('Expected a GET api/permissions request; none was made.');
  }

  for (const request of requests) {
    request.flush(permissions);
  }

  return requests.length;
}

/**
 * A `PaginatedResponseDto` with the server's own arithmetic. Pass the store's page size: the
 * default is the API's, not the screen's.
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
