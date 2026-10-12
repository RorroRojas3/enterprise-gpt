/**
 * A project from `GET api/projects`. With no `sort=`, newest created first. Render the
 * server's order and never re-sort a page. `name` is unique among the owner's active projects.
 */
export interface ProjectSummaryDto {
  readonly id: string;
  readonly name: string;
  readonly description: string | null;
  /**
   * Set only through `PUT api/projects/{id}/favorite`, never {@link ProjectWriteBody}. Toggling
   * it does not bump `dateModified`, so a client can hold the row across a toggle.
   */
  readonly isFavorite: boolean;
  /** ISO 8601 with offset. */
  readonly dateCreated: string;
  /** ISO 8601 with offset. */
  readonly dateModified: string;
}

/**
 * The only source of {@link instructions}: listing rows omit the property entirely, which
 * would read as "no instructions".
 */
export interface ProjectDto extends ProjectSummaryDto {
  readonly instructions: string | null;
}

/**
 * Keeps `instructions` out of list-store rows, where nothing could tell it from a stale
 * leftover. Explicit fields, so a new listing field is a compile error here.
 */
export function toProjectSummary(project: ProjectDto): ProjectSummaryDto {
  return {
    id: project.id,
    name: project.name,
    description: project.description,
    isFavorite: project.isFavorite,
    dateCreated: project.dateCreated,
    dateModified: project.dateModified,
  };
}

/**
 * Mirrors the server's `MaximumLength` rules. `instructions` is capped despite an
 * `nvarchar(max)` column because it is sent to the model on every turn.
 */
export const PROJECT_FIELD_LENGTHS = {
  name: 256,
  description: 1024,
  instructions: 8000,
} as const;

/**
 * A full representation on both verbs: a field omitted on update is cleared, so build it with
 * `toProjectBody`. The id travels in the route, unlike `PUT api/conversations`.
 */
export interface ProjectWriteBody {
  readonly name: string;
  readonly description: string | null;
  readonly instructions: string | null;
}

/**
 * The route returns a bare array, not a `PaginatedResponseDto`. Its duplicate `documentId`
 * alias is deliberately not modelled.
 */
export interface ProjectDocumentDto {
  readonly id: string;
  readonly projectId: string;
  /** The original file name as uploaded. */
  readonly name: string;
  /** Lower-cased, **including the leading dot** — `.pdf`, not `pdf`. */
  readonly extension: string;
  /** The content type the client reported at upload time. */
  readonly mimeType: string;
  /** Bytes. */
  readonly size: number;
  /** ISO 8601 with offset — when ingestion finished, not when the upload started. */
  readonly dateCreated: string;
}
