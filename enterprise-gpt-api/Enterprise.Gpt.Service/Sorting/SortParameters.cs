using Enterprise.Gpt.Common.Enums;
using FluentValidation;
using FluentValidation.Results;

namespace Enterprise.Gpt.Service.Sorting;

/// <summary>Parses the <c>?sort=</c> and <c>?dir=</c> query parameters the paginated list routes accept.</summary>
/// <remarks>
/// Unrecognised values are rejected, keyed by the query-parameter name, because a misspelt field would otherwise
/// return a plausible page in the wrong order. A listing's enum, display list and lookup must use identical
/// tokens; <c>SortKeysTests</c> enforces it.
/// </remarks>
public static class SortParameters
{
    /// <summary>The <c>errors</c> key a rejected sort field is reported under.</summary>
    public const string SortKey = "sort";

    /// <summary>The <c>errors</c> key a rejected sort direction is reported under.</summary>
    public const string DirectionKey = "dir";

    private const string AscendingName = "asc";
    private const string DescendingName = "desc";

    private static readonly Dictionary<string, SortDirections> DirectionsByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [AscendingName] = SortDirections.Ascending,
            [DescendingName] = SortDirections.Descending
        };

    public static string SupportedDirectionList { get; } = $"'{AscendingName}', '{DescendingName}'";

    #region Public static methods

    /// <summary>Reports whether neither <c>?sort=</c> nor <c>?dir=</c> was supplied.</summary>
    /// <remarks>
    /// Callers then emit their original <c>ORDER BY</c> untouched: older clients rely on it, and even an added
    /// tie-break can move rows that compare equal.
    /// </remarks>
    public static bool IsDefaultOrder(string? sort, string? dir) =>
        string.IsNullOrWhiteSpace(sort) && string.IsNullOrWhiteSpace(dir);

    /// <summary>
    /// Resolves a <c>?sort=</c> value against one listing's fields, or <paramref name="fallback"/> when blank.
    /// </summary>
    /// <remarks>
    /// Build <paramref name="byName"/> with <see cref="StringComparer.OrdinalIgnoreCase"/>, or
    /// <c>?sort=NAME</c> is rejected by a message listing <c>'name'</c> as supported.
    /// </remarks>
    /// <exception cref="ValidationException">
    /// <paramref name="sort"/> names no accepted field; surfaces as a 400 keyed <see cref="SortKey"/>.
    /// </exception>
    public static TKey ResolveKey<TKey>(
        string? sort,
        IReadOnlyDictionary<string, TKey> byName,
        TKey fallback,
        string supportedList)
        where TKey : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return fallback;
        }

        if (byName.TryGetValue(sort.Trim(), out var resolved))
        {
            return resolved;
        }

        throw new ValidationException(
        [
            new ValidationFailure(
                SortKey,
                $"'{sort}' is not a supported sort field. Supported fields are {supportedList}.")
        ]);
    }

    /// <summary>
    /// Resolves a <c>?dir=</c> value, or <paramref name="natural"/> when blank: the direction the field
    /// reads most usefully in.
    /// </summary>
    /// <exception cref="ValidationException">
    /// <paramref name="dir"/> is neither <c>asc</c> nor <c>desc</c>; surfaces as a 400 keyed
    /// <see cref="DirectionKey"/>.
    /// </exception>
    public static SortDirections ResolveDirection(string? dir, SortDirections natural)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return natural;
        }

        if (DirectionsByName.TryGetValue(dir.Trim(), out var resolved))
        {
            return resolved;
        }

        throw new ValidationException(
        [
            new ValidationFailure(
                DirectionKey,
                $"'{dir}' is not a supported sort direction. Supported directions are {SupportedDirectionList}.")
        ]);
    }

    #endregion
}
