namespace Enterprise.Gpt.Service.Sorting;

/// <summary>
/// The fields <c>GET api/projects</c> can order on.
/// </summary>
public enum ProjectSortKey
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

/// <summary>
/// The <c>?sort=</c> values <c>GET api/projects</c> accepts.
/// </summary>
public static class ProjectSortKeys
{
    /// <summary>
    /// Gets the accepted values, in the order a rejection message lists them.
    /// </summary>
    public static IReadOnlyList<string> Supported { get; } = ["created", "updated", "name", "favorite"];

    private static readonly Dictionary<string, ProjectSortKey> ByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["created"] = ProjectSortKey.Created,
            ["updated"] = ProjectSortKey.Updated,
            ["name"] = ProjectSortKey.Name,
            ["favorite"] = ProjectSortKey.Favorite
        };

    /// <summary>
    /// Gets the accepted values rendered for a validation message.
    /// </summary>
    public static string SupportedList { get; } = string.Join(", ", Supported.Select(name => $"'{name}'"));

    /// <summary>
    /// Resolves the <c>?sort=</c> and <c>?dir=</c> pair a caller sent.
    /// </summary>
    /// <param name="sort">The requested field, or <see langword="null"/>.</param>
    /// <param name="dir">The requested direction, or <see langword="null"/>.</param>
    /// <returns>The ordering to apply.</returns>
    /// <exception cref="FluentValidation.ValidationException">Either value is unrecognised.</exception>
    public static SortSelection<ProjectSortKey> Resolve(string? sort, string? dir)
    {
        var key = SortParameters.ResolveKey(sort, ByName, ProjectSortKey.Created, SupportedList);

        return new SortSelection<ProjectSortKey>(key, SortParameters.ResolveDirection(dir, NaturalDirection(key)));
    }

    // Dates and the favourite flag read most usefully newest-and-starred first; a name reads A-Z.
    private static SortDirection NaturalDirection(ProjectSortKey key) => key switch
    {
        ProjectSortKey.Name => SortDirection.Ascending,
        _ => SortDirection.Descending
    };
}
