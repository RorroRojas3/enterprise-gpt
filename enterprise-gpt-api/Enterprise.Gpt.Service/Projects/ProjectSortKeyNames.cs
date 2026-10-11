using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Sorting;
using Enterprise.Gpt.Service.Sorting.Models;

namespace Enterprise.Gpt.Service.Projects;

/// <summary>The <c>?sort=</c> values <c>GET api/projects</c> accepts.</summary>
public static class ProjectSortKeyNames
{
    /// <summary>In the order a rejection message lists them.</summary>
    public static IReadOnlyList<string> Supported { get; } = ["created", "updated", "name", "favorite"];

    private static readonly Dictionary<string, ProjectSortKeys> ByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["created"] = ProjectSortKeys.Created,
            ["updated"] = ProjectSortKeys.Updated,
            ["name"] = ProjectSortKeys.Name,
            ["favorite"] = ProjectSortKeys.Favorite
        };

    public static string SupportedList { get; } = string.Join(", ", Supported.Select(name => $"'{name}'"));

    #region Private methods

    // Dates and the favourite flag read most usefully newest-and-starred first; a name reads A-Z.
    private static SortDirections NaturalDirection(ProjectSortKeys key) => key switch
    {
        ProjectSortKeys.Name => SortDirections.Ascending,
        _ => SortDirections.Descending
    };

    #endregion

    #region Public static methods

    /// <summary>Resolves the <c>?sort=</c> and <c>?dir=</c> pair a caller sent.</summary>
    /// <exception cref="FluentValidation.ValidationException">Either value is unrecognised.</exception>
    public static SortSelection<ProjectSortKeys> Resolve(string? sort, string? dir)
    {
        var key = SortParameters.ResolveKey(sort, ByName, ProjectSortKeys.Created, SupportedList);

        return new SortSelection<ProjectSortKeys>(key, SortParameters.ResolveDirection(dir, NaturalDirection(key)));
    }

    #endregion
}
