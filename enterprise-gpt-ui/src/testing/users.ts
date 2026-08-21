import { HttpTestingController } from '@angular/common/http/testing';
import { PaginatedResponseDto } from '../app/domain/api/paginated-response';
import { PermissionDto, UserDto } from '../app/domain/api/user';
import { ADMINISTRATOR_GRANT, UPLOAD_FILE_GRANT } from './session';

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
 * The catalog the users tab's permission filter and its edit panel both read (US-1206).
 *
 * Answered with rows rather than an empty array on purpose: `ensurePermissions()` treats
 * an empty catalog as "not loaded", so a spec that flushed `[]` and then opened a form
 * would issue a second request and fail `verify()` on it.
 */
export function flushPermissions(
  backend: HttpTestingController,
  permissions: PermissionDto[] = [
    permissionFixture(),
    permissionFixture({ id: ADMINISTRATOR_GRANT.id, name: ADMINISTRATOR_GRANT.name }),
  ],
  { optional = false }: { optional?: boolean } = {},
): number {
  // `match`, not `expectOne`: the catalog is cached in the root store, so a spec that
  // mounts the tab twice issues one request across both mounts, and the second mount has
  // none to answer. Those callers pass `optional`; everyone else gets the assertion,
  // because a helper that silently passes on zero requests would keep every spec green if
  // the screen stopped asking for the catalog at all.
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
