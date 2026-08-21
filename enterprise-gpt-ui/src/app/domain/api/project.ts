/**
 * A project as `GET api/projects` returns one.
 *
 * Mirrors `Enterprise.Gpt.Dto.ProjectSummaryDto`. Asked for no order the listing route
 * returns **`dateCreated` descending** (with `id` breaking ties), not `dateModified` — so
 * "newest first" there means newest *created*. Since US-706 it also accepts `sort=` and
 * `dir=`, and both stores over it send one: the grid the reader's choice, the root lookup
 * a fixed favourites-first. Either way the client renders the server's order and never
 * re-sorts a page. `name` is unique among the owner's active projects,
 * which is the constraint behind the duplicate-name validation problem.
 */
export interface ProjectSummaryDto {
  readonly id: string;
  readonly name: string;
  readonly description: string | null;
  /**
   * Whether the owner has starred the project (US-909).
   *
   * Set through `PUT api/projects/{id}/favorite`, never through {@link ProjectWriteBody}
   * — that PUT is a full representation, so a rename carrying the flag would be one
   * field away from clearing it. The server deliberately does **not** bump
   * `dateModified` for it, which is what lets a client hold the row across a toggle.
   */
  readonly isFavorite: boolean;
  /** ISO 8601 with offset. */
  readonly dateCreated: string;
  /** ISO 8601 with offset. */
  readonly dateModified: string;
}

/**
 * One project as `GET api/projects/{id}`, `POST api/projects` and
 * `PUT api/projects/{id}` return it.
 *
 * The only source of {@link instructions}: the listing projection omits the column
 * server-side, so on a `ProjectSummaryDto` the property is **absent from the JSON
 * entirely**, not null. Reading it off a listing row would therefore report "no
 * instructions" for a project that has them — the same trap `ConversationDetailDto`
 * documents for `mcpServerIds`, and the reason {@link toProjectSummary} exists.
 */
export interface ProjectDto extends ProjectSummaryDto {
  readonly instructions: string | null;
}

/**
 * Narrows a full `ProjectDto` to the listing shape.
 *
 * A list store holds `ProjectSummaryDto` entities. Spreading a detail DTO into one
 * would carry `instructions` along, and the next reader of that row would have no way
 * to know whether the field is authoritative or a leftover from whichever row happened
 * to be opened. Explicit fields rather than destructured rest, so a field added to the
 * listing shape is a compile error here rather than a silent omission.
 *
 * @param project The full project, from any single-project route.
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
 * The server's `MaximumLength` rules, mirroring `Enterprise.Gpt.Dto.ProjectFieldLengths`.
 *
 * `name` and `description` match column lengths; `instructions` does not — its column
 * is `nvarchar(max)` and the cap exists because the text is sent to the model on every
 * turn, so an uncapped project could consume the context window on every message.
 */
export const PROJECT_FIELD_LENGTHS = {
  name: 256,
  description: 1024,
  instructions: 8000,
} as const;

/**
 * The body of `POST api/projects` and `PUT api/projects/{id}`.
 *
 * **A full representation, on both verbs.** `UpdateProjectActionDto` assigns every
 * field unconditionally, so omitting `description` or `instructions` on an update
 * *clears* the stored value. There is no PATCH. Build it through `toProjectBody`
 * rather than by hand — that is the single place that cannot forget a field.
 *
 * Note the id is **not** in the body: `PUT api/projects/{id}` carries it in the route,
 * unlike `PUT api/conversations`, which carries it in the body.
 */
export interface ProjectWriteBody {
  readonly name: string;
  readonly description: string | null;
  readonly instructions: string | null;
}

/**
 * A document uploaded into a project, as `GET api/projects/{id}/documents` returns one.
 *
 * Mirrors `Enterprise.Gpt.Dto.ProjectDocumentDto`. Two things about that route are easy
 * to get wrong: it answers with a **bare array**, not a `PaginatedResponseDto`, and it
 * emits both `id` and a read-only `documentId` alias of the same value, so a client can
 * key it the same way it keys the upload-status response. Only {@link id} is modelled
 * here — a second name for one value in store state is a bug waiting to be written.
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
