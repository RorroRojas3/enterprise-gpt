namespace Enterprise.Gpt.Dto.Projects;

/// <summary>
/// A project as returned by the single-project routes.
/// </summary>
public class ProjectDto : ProjectSummaryDto
{
    /// <summary>
    /// Gets or sets the standing instructions applied to every conversation in the project. Free
    /// text authored by the owner; may be <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Delivered as a system message immediately <em>after</em> the platform system prompt, never
    /// before it, so it cannot outrank the platform's safety and privacy rules. Deliberately absent
    /// from <see cref="ProjectSummaryDto"/>: it is the largest field on the type and no listing
    /// renders it.
    /// </remarks>
    public string? Instructions { get; set; }
}
