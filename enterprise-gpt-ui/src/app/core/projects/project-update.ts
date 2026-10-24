import { ProjectDto, ProjectWriteBody } from '@core/api/project.model';

/** The fields a caller may change on an existing project. */
export interface ProjectChanges {
  readonly name?: string;
  readonly description?: string | null;
  readonly instructions?: string | null;
}

/**
 * The only place a project update body is built: the PUT assigns every field, so `{ name }` alone
 * would clear the description and instructions. `undefined` leaves a field; `null` clears it.
 */
export function toProjectBody(current: ProjectDto, changes: ProjectChanges = {}): ProjectWriteBody {
  return {
    name: changes.name ?? current.name,
    description: changes.description === undefined ? current.description : changes.description,
    instructions: changes.instructions === undefined ? current.instructions : changes.instructions,
  };
}
