namespace Enterprise.Gpt.Service.Sorting;

/// <summary>
/// The fields <c>GET api/users</c> can order on.
/// </summary>
public enum UserSortKey
{
    /// <summary>Last name then first name — the directory's own default order.</summary>
    Name,

    /// <summary>The work email address.</summary>
    Email,

    /// <summary>When the row was provisioned, by an administrator or by first sign-in.</summary>
    Created
}

/// <summary>
/// The <c>?sort=</c> values <c>GET api/users</c> accepts.
/// </summary>
public static class UserSortKeys
{
    /// <summary>
    /// Gets the accepted values, in the order a rejection message lists them.
    /// </summary>
    public static IReadOnlyList<string> Supported { get; } = ["name", "email", "created"];

    private static readonly Dictionary<string, UserSortKey> ByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = UserSortKey.Name,
            ["email"] = UserSortKey.Email,
            ["created"] = UserSortKey.Created
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
    public static SortSelection<UserSortKey> Resolve(string? sort, string? dir)
    {
        var key = SortParameters.ResolveKey(sort, ByName, UserSortKey.Name, SupportedList);

        return new SortSelection<UserSortKey>(key, SortParameters.ResolveDirection(dir, NaturalDirection(key)));
    }

    // A directory reads A-Z; a provisioning date reads newest first.
    private static SortDirection NaturalDirection(UserSortKey key) => key switch
    {
        UserSortKey.Created => SortDirection.Descending,
        _ => SortDirection.Ascending
    };
}
