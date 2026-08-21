using FluentValidation;
using FluentValidation.Results;

namespace Enterprise.Gpt.Service.Sorting;

/// <summary>
/// Parses the <c>?sort=</c> and <c>?dir=</c> query parameters the paginated list routes accept, and
/// rejects an unrecognised value as a validation problem naming the parameter.
/// </summary>
/// <remarks>
/// Silently ignoring a value nobody recognises is the failure mode this exists to prevent: a caller
/// who misspells a sort field would otherwise get a correct-looking page in the wrong order and no
/// way to tell. The rejection is keyed by the <em>query-parameter</em> name rather than a
/// <c>nameof</c>, because that is the token the caller sent and the one a client can render against
/// its own control — the same choice the export route's <c>format</c> failure makes.
/// <para>
/// Each listing's key table appears three times over — as an enumeration, as a display list, and as
/// a lookup — and a token must be the same string in all three, or the supported set a rejection
/// advertises and the set it actually accepts drift apart. <c>SortKeysTests</c> is what keeps them in
/// step.
/// </para>
/// </remarks>
public static class SortParameters
{
    /// <summary>
    /// The <c>errors</c> key a rejected sort field is reported under.
    /// </summary>
    public const string SortKey = "sort";

    /// <summary>
    /// The <c>errors</c> key a rejected sort direction is reported under.
    /// </summary>
    public const string DirectionKey = "dir";

    private const string AscendingName = "asc";
    private const string DescendingName = "desc";

    private static readonly Dictionary<string, SortDirection> DirectionsByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [AscendingName] = SortDirection.Ascending,
            [DescendingName] = SortDirection.Descending
        };

    /// <summary>
    /// Gets the accepted <c>?dir=</c> values rendered for a validation message.
    /// </summary>
    public static string SupportedDirectionList { get; } = $"'{AscendingName}', '{DescendingName}'";

    /// <summary>
    /// Reports whether the caller asked for no particular order.
    /// </summary>
    /// <param name="sort">The raw <c>?sort=</c> value.</param>
    /// <param name="dir">The raw <c>?dir=</c> value.</param>
    /// <returns><see langword="true"/> when neither parameter was supplied.</returns>
    /// <remarks>
    /// Callers branch on this to emit their listing's original <c>ORDER BY</c> untouched. That
    /// ordering is a promise to clients written before these parameters existed, and even a tie-break
    /// added to it can move rows that compare equal.
    /// </remarks>
    public static bool IsDefaultOrder(string? sort, string? dir) =>
        string.IsNullOrWhiteSpace(sort) && string.IsNullOrWhiteSpace(dir);

    /// <summary>
    /// Resolves a <c>?sort=</c> value against the fields one listing accepts.
    /// </summary>
    /// <typeparam name="TKey">The listing's sort-field enumeration.</typeparam>
    /// <param name="sort">The requested field, or <see langword="null"/> when the caller sent none.</param>
    /// <param name="byName">
    /// The accepted values. Build it with <see cref="StringComparer.OrdinalIgnoreCase"/>: a table
    /// built with the default comparer would reject <c>?sort=NAME</c> with a message listing
    /// <c>'name'</c> as supported.
    /// </param>
    /// <param name="fallback">The field used when no value was sent.</param>
    /// <param name="supportedList">The accepted values rendered for a rejection message.</param>
    /// <returns>The resolved field.</returns>
    /// <exception cref="ValidationException">
    /// <paramref name="sort"/> names no accepted field. Surfaces as a 400 keyed <see cref="SortKey"/>.
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
    /// Resolves a <c>?dir=</c> value.
    /// </summary>
    /// <param name="dir">The requested direction, or <see langword="null"/> when the caller sent none.</param>
    /// <param name="natural">
    /// The direction used when no value was sent — the one the field reads most usefully in, so a
    /// caller asking for <c>sort=created</c> alone gets newest first rather than oldest.
    /// </param>
    /// <returns>The resolved direction.</returns>
    /// <exception cref="ValidationException">
    /// <paramref name="dir"/> is neither <c>asc</c> nor <c>desc</c>. Surfaces as a 400 keyed
    /// <see cref="DirectionKey"/>.
    /// </exception>
    public static SortDirection ResolveDirection(string? dir, SortDirection natural)
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
}
