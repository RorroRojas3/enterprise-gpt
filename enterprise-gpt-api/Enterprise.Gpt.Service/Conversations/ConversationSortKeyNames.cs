namespace Enterprise.Gpt.Service.Sorting;

/// <summary>
/// The fields <c>GET api/conversations/search</c> can order on.
/// </summary>
/// <remarks>
/// <c>DateModified</c> is deliberately absent. It moves on a rename, a favourite, a model change and
/// a move between projects as readily as on a turn, so a "recently active" sort built on it would
/// promise something the column cannot answer — the transcript, which is where activity actually
/// lands, lives in Cosmos and is not queryable from here.
/// </remarks>
public enum ConversationSortKey
{
    /// <summary>When the conversation was started.</summary>
    Created,

    /// <summary>The conversation name.</summary>
    Name
}

/// <summary>
/// The <c>?sort=</c> values <c>GET api/conversations/search</c> accepts.
/// </summary>
public static class ConversationSortKeys
{
    /// <summary>
    /// Gets the accepted values, in the order a rejection message lists them.
    /// </summary>
    public static IReadOnlyList<string> Supported { get; } = ["created", "name"];

    private static readonly Dictionary<string, ConversationSortKey> ByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["created"] = ConversationSortKey.Created,
            ["name"] = ConversationSortKey.Name
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
    public static SortSelection<ConversationSortKey> Resolve(string? sort, string? dir)
    {
        var key = SortParameters.ResolveKey(sort, ByName, ConversationSortKey.Created, SupportedList);

        return new SortSelection<ConversationSortKey>(key, SortParameters.ResolveDirection(dir, NaturalDirection(key)));
    }

    private static SortDirection NaturalDirection(ConversationSortKey key) => key switch
    {
        ConversationSortKey.Name => SortDirection.Ascending,
        _ => SortDirection.Descending
    };
}
