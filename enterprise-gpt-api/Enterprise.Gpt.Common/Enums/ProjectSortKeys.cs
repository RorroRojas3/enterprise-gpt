namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// The fields <c>GET api/projects</c> can order on.
/// </summary>
public enum ProjectSortKeys
{
    /// <summary>When the project was created.</summary>
    Created,

    /// <summary>When the project was last edited.</summary>
    Updated,

    /// <summary>The project name.</summary>
    Name,

    /// <summary>Whether the owner has starred the project.</summary>
    Favorite
}
