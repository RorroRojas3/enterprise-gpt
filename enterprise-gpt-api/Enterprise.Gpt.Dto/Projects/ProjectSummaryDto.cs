namespace Enterprise.Gpt.Dto.Projects;

/// <summary>
/// The subset of a project returned by listings, where the instructions are not needed.
/// </summary>
public class ProjectSummaryDto
{
    /// <summary>Gets or sets the unique identifier of the project.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the project name, unique among the owner's active projects.</summary>
    public string Name { get; set; } = null!;

    /// <summary>Gets or sets the optional free-text description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets a value that indicates whether the caller has marked the project as a favourite.</summary>
    /// <remarks>
    /// On <see cref="ProjectSummaryDto"/> rather than <see cref="ProjectDto"/> so every project
    /// route carries it: the detail shape inherits from the listing shape here.
    /// </remarks>
    public bool IsFavorite { get; set; }

    /// <summary>Gets or sets when the project was created.</summary>
    public DateTimeOffset DateCreated { get; set; }

    /// <summary>Gets or sets when the project was last modified.</summary>
    public DateTimeOffset DateModified { get; set; }
}
