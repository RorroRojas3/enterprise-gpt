using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Sorting;
using Enterprise.Gpt.Service.Sorting.Models;

namespace Enterprise.Gpt.Service.Conversations;

/// <summary>
/// The <c>?sort=</c> values <c>GET api/conversations/search</c> accepts.
/// </summary>
public static class ConversationSortKeyNames
{
    /// <summary>
    /// Gets the accepted values, in the order a rejection message lists them.
    /// </summary>
    public static IReadOnlyList<string> Supported { get; } = ["created", "name"];

    private static readonly Dictionary<string, ConversationSortKeys> ByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["created"] = ConversationSortKeys.Created,
            ["name"] = ConversationSortKeys.Name
        };

    /// <summary>
    /// Gets the accepted values rendered for a validation message.
    /// </summary>
    public static string SupportedList { get; } = string.Join(", ", Supported.Select(name => $"'{name}'"));

    #region Private methods

    private static SortDirections NaturalDirection(ConversationSortKeys key) => key switch
    {
        ConversationSortKeys.Name => SortDirections.Ascending,
        _ => SortDirections.Descending
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
    public static SortSelection<ConversationSortKeys> Resolve(string? sort, string? dir)
    {
        var key = SortParameters.ResolveKey(sort, ByName, ConversationSortKeys.Created, SupportedList);

        return new SortSelection<ConversationSortKeys>(key, SortParameters.ResolveDirection(dir, NaturalDirection(key)));
    }

    #endregion
}
