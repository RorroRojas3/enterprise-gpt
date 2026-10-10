using System.Linq.Expressions;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Project;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Service.Mappers;

/// <summary>
/// Maps between <see cref="Project"/> and its DTOs, in both the materialized and the
/// server-side-projection forms.
/// </summary>
public static class ProjectMapper
{
    /// <summary>
    /// Maps a materialized <see cref="Project"/> entity to <see cref="ProjectDto"/>.
    /// </summary>
    /// <param name="project">The loaded project entity.</param>
    /// <returns>The mapped DTO.</returns>
    public static ProjectDto MapToProjectDto(this Project project)
    {
        return new ProjectDto
        {
            Id = project.Id,
            Name = project.Name,
            Description = project.Description,
            Instructions = project.Instructions,
            IsFavorite = project.IsFavorite,
            DateCreated = project.DateCreated,
            DateModified = project.DateModified
        };
    }

    /// <summary>
    /// LINQ projection from <see cref="Project"/> to <see cref="ProjectDto"/> for use inside
    /// <c>IQueryable.Select(...)</c>.
    /// </summary>
    public static Expression<Func<Project, ProjectDto>> MapToProjectDtoExpression { get; } =
        project => new ProjectDto
        {
            Id = project.Id,
            Name = project.Name,
            Description = project.Description,
            Instructions = project.Instructions,
            IsFavorite = project.IsFavorite,
            DateCreated = project.DateCreated,
            DateModified = project.DateModified
        };

    /// <summary>
    /// LINQ projection from <see cref="Project"/> to <see cref="ProjectSummaryDto"/> for listings.
    /// </summary>
    /// <remarks>
    /// Deliberately omits <see cref="ProjectDto.Instructions"/>: a page of projects would otherwise
    /// carry up to a hundred instruction bodies that no listing renders.
    /// </remarks>
    public static Expression<Func<Project, ProjectSummaryDto>> MapToProjectSummaryDtoExpression { get; } =
        project => new ProjectSummaryDto
        {
            Id = project.Id,
            Name = project.Name,
            Description = project.Description,
            IsFavorite = project.IsFavorite,
            DateCreated = project.DateCreated,
            DateModified = project.DateModified
        };

    /// <summary>
    /// Creates a new <see cref="Project"/> entity from a <see cref="CreateProjectActionDto"/>.
    /// Callers are responsible for setting <c>Id</c>, <c>UserId</c> and the audit columns
    /// (e.g. <c>DateCreated</c>) before saving.
    /// </summary>
    /// <param name="dto">The action DTO carrying the new values.</param>
    /// <returns>The new, untracked entity.</returns>
    public static Project FromCreateProjectActionDtoToProject(this CreateProjectActionDto dto)
    {
        return new Project
        {
            Name = dto.Name,
            Description = dto.Description,
            Instructions = dto.Instructions
        };
    }

    /// <summary>
    /// Applies an <see cref="UpdateProjectActionDto"/> to an existing <see cref="Project"/> entity in
    /// place. Callers are responsible for setting audit columns (e.g. <c>DateModified</c>) and saving
    /// changes.
    /// </summary>
    /// <param name="dto">The action DTO carrying the new values.</param>
    /// <param name="project">The tracked entity to mutate.</param>
    /// <remarks>
    /// <c>IsFavorite</c> is deliberately not assigned here. This PUT is a full representation and the
    /// update DTO does not carry the flag, so writing it would clear the star on every rename — the
    /// trap <see cref="SetProjectFavoriteActionDto"/> exists as a separate route to avoid.
    /// </remarks>
    public static void FromUpdateProjectActionDtoToProject(this UpdateProjectActionDto dto, Project project)
    {
        project.Name = dto.Name;
        project.Description = dto.Description;
        project.Instructions = dto.Instructions;
    }
}
