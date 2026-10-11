namespace Enterprise.Gpt.Dto.Projects;

/// <summary>
/// A project as returned by the single-project routes.
/// </summary>
public class ProjectDto : ProjectSummaryDto
{
    /// <summary>Owner-authored standing instructions applied to every conversation in the project.</summary>
    /// <remarks>Omitted from <see cref="ProjectSummaryDto"/>: it is the largest field and no listing renders it.</remarks>
    public string? Instructions { get; set; }
}
