namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// The fields <c>GET api/projects</c> can order on.
/// </summary>
public enum ProjectSortKeys
{
    Created,

    Updated,

    Name,

    /// <summary>Whether the owner has starred the project.</summary>
    Favorite
}
