import { PaginatedResponseDto } from '../app/shared/models/api/paginated-response.model';
import {
  ProjectDocumentDto,
  ProjectDto,
  ProjectSummaryDto,
} from '../app/shared/models/api/project.model';

let sequence = 0;

/** A `ProjectSummaryDto` exactly as `GET api/projects` returns one — no `instructions`. */
export function projectFixture(overrides: Partial<ProjectSummaryDto> = {}): ProjectSummaryDto {
  const index = sequence++;

  return {
    // No modulo: a repeating id collapses entities silently (`setAllEntities` keeps
    // one, `addEntities` no-ops) and, once rendered, trips NG0955 on `@for … track id`.
    id: `${String(index).padStart(8, '0')}-5555-4666-8777-888888888888`,
    name: `Project ${index}`,
    description: `Description ${index}`,
    isFavorite: false,
    dateCreated: '2026-08-11T09:00:00+00:00',
    dateModified: '2026-08-11T09:00:00+00:00',
    ...overrides,
  };
}

/** A `ProjectDto` as the single-project routes return one. */
export function projectDetailFixture(overrides: Partial<ProjectDto> = {}): ProjectDto {
  return { ...projectFixture(overrides), instructions: null, ...overrides };
}

/**
 * A `PaginatedResponseDto` with the server's own arithmetic. `pageSize` defaults to the grid's
 * `take=100`, the server's clamp ceiling.
 */
export function projectPage(
  items: ProjectSummaryDto[],
  { totalCount = items.length, pageSize = 100, skip = 0 } = {},
): PaginatedResponseDto<ProjectSummaryDto> {
  return {
    items,
    totalCount,
    pageSize,
    currentPage: Math.floor(skip / pageSize) + 1,
    totalPages: Math.ceil(totalCount / pageSize),
  };
}

/** A `ProjectDocumentDto` as `GET api/projects/{id}/documents` returns one. */
export function projectDocumentFixture(
  overrides: Partial<ProjectDocumentDto> = {},
): ProjectDocumentDto {
  const index = sequence++;

  return {
    id: `${String(index).padStart(8, '0')}-9999-4aaa-8bbb-cccccccccccc`,
    projectId: '00000000-5555-4666-8777-888888888888',
    name: `document-${index}.pdf`,
    extension: '.pdf',
    mimeType: 'application/pdf',
    size: 2_411_724,
    dateCreated: '2026-08-11T09:00:00+00:00',
    ...overrides,
  };
}
