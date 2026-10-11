namespace Enterprise.Gpt.Dto.Projects;

/// <summary>
/// The subset of a project returned by listings, where the instructions are not needed.
/// </summary>
public class ProjectSummaryDto
{
    public Guid Id { get; set; }

    /// <summary>Gets or sets the project name, unique among the owner's active projects.</summary>
    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    /// <summary>Whether the caller has marked the project as a favourite.</summary>
    public bool IsFavorite { get; set; }

    public DateTimeOffset DateCreated { get; set; }

    public DateTimeOffset DateModified { get; set; }
}
