using System.Linq.Expressions;
using Enterprise.Gpt.Dto.Actions.Projects;
using Enterprise.Gpt.Dto.Projects;
using Enterprise.Gpt.Entity.Projects;

namespace Enterprise.Gpt.Service.Projects;

public static class ProjectMapper
{
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
    /// Listing projection; omits <see cref="ProjectDto.Instructions"/>, which no listing renders.
    /// </summary>
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

    #region Public static methods

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

    /// <summary>Creates an untracked project; callers set <c>Id</c> and <c>UserId</c>.</summary>
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
    /// Deliberately leaves <c>IsFavorite</c> alone: the update DTO does not carry it, so assigning it would
    /// clear the star on every rename. <see cref="SetProjectFavoriteActionDto"/> has its own route.
    /// </summary>
    public static void FromUpdateProjectActionDtoToProject(this UpdateProjectActionDto dto, Project project)
    {
        project.Name = dto.Name;
        project.Description = dto.Description;
        project.Instructions = dto.Instructions;
    }

    #endregion
}
