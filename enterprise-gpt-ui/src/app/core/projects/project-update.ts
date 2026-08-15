import { ProjectDto, ProjectWriteBody } from '@domain/api/project';

/** The fields a caller may change on an existing project. */
export interface ProjectChanges {
  readonly name?: string;
  readonly description?: string | null;
  readonly instructions?: string | null;
}

/**
 * Builds the body of `PUT api/projects/{id}` from the project as it stands plus the
 * fields being changed.
 *
 * **This is the only place a project update body is built**, for the same reason
 * `toUpdateBody` is for conversations: `UpdateProjectActionDto` assigns every field
 * unconditionally, so a caller that sent `{ name }` alone would silently clear the
 * project's description *and* its standing instructions — and instructions are the
 * thing US-904 exists to keep. A rename from the grid's kebab and a save from the
 * instructions panel are the same request with different arguments.
 *
 * `undefined` means "leave it", `null` means "clear it". They cannot be collapsed: the
 * server distinguishes them, and a `??` here would make clearing a description
 * impossible.
 *
 * @param current The project as the client last saw it from the server.
 * @param changes The fields being changed.
 */
export function toProjectBody(current: ProjectDto, changes: ProjectChanges = {}): ProjectWriteBody {
  return {
    name: changes.name ?? current.name,
    description: changes.description === undefined ? current.description : changes.description,
    instructions: changes.instructions === undefined ? current.instructions : changes.instructions,
  };
}
