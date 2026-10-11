using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Sorting;
using Enterprise.Gpt.Service.Sorting.Models;

namespace Enterprise.Gpt.Service.Users;

/// <summary>
/// The <c>?sort=</c> values <c>GET api/users</c> accepts.
/// </summary>
public static class UserSortKeyNames
{
    /// <summary>
    /// Gets the accepted values, in the order a rejection message lists them.
    /// </summary>
    public static IReadOnlyList<string> Supported { get; } = ["name", "email", "created"];

    private static readonly Dictionary<string, UserSortKeys> ByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = UserSortKeys.Name,
            ["email"] = UserSortKeys.Email,
            ["created"] = UserSortKeys.Created
        };

    /// <summary>
    /// Gets the accepted values rendered for a validation message.
    /// </summary>
    public static string SupportedList { get; } = string.Join(", ", Supported.Select(name => $"'{name}'"));

    #region Private methods

    // A directory reads A-Z; a provisioning date reads newest first.
    private static SortDirections NaturalDirection(UserSortKeys key) => key switch
    {
        UserSortKeys.Created => SortDirections.Descending,
        _ => SortDirections.Ascending
    };

    #endregion

    #region Public static methods

    /// <summary>
    /// Resolves the <c>?sort=</c> and <c>?dir=</c> pair a caller sent.
    /// </summary>
    /// <param name="sort">The requested field, or <see langword="null"/>.</param>
    /// <param name="dir">The requested direction, or <see langword="null"/>.</param>
    /// <returns>The ordering to apply.</returns>
    /// <exception cref="FluentValidation.ValidationException">Either value is unrecognised.</exception>
    public static SortSelection<UserSortKeys> Resolve(string? sort, string? dir)
    {
        var key = SortParameters.ResolveKey(sort, ByName, UserSortKeys.Name, SupportedList);

        return new SortSelection<UserSortKeys>(key, SortParameters.ResolveDirection(dir, NaturalDirection(key)));
    }

    #endregion
}
