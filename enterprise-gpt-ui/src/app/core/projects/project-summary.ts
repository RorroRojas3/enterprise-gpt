import { ProjectDto, ProjectSummaryDto } from '@core/api/project.model';

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
