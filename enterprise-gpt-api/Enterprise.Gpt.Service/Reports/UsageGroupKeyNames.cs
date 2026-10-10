using FluentValidation;
using FluentValidation.Results;

namespace Enterprise.Gpt.Service.Reports;

/// <summary>
/// The dimensions <c>GET api/reports/usage</c> can group by.
/// </summary>
public enum UsageGroupKey
{
    /// <summary>The catalog model that served each call.</summary>
    Model,

    /// <summary>The provider that served each call.</summary>
    Provider,

    /// <summary>The user each call was charged to.</summary>
    User,

    /// <summary>The UTC day each call finished on.</summary>
    Day
}

/// <summary>
/// The <c>?groupBy=</c> values <c>GET api/reports/usage</c> accepts.
/// </summary>
/// <remarks>
/// The same three-part shape as the sort-key tables in
/// <see cref="Enterprise.Gpt.Service.Sorting"/>, and for the same reason: a token has to be the
/// same string in the enumeration, the display list and the lookup, or the set a rejection
/// advertises drifts away from the set that is actually accepted. <c>UsageGroupKeysTests</c> is
/// what keeps the three in step.
/// <para>
/// It does not reuse <c>SortParameters.ResolveKey</c>, which reports a rejection under the
/// <c>sort</c> key. This parameter is not a sort, and a client cannot render an error against a
/// control it does not have.
/// </para>
/// </remarks>
public static class UsageGroupKeys
{
    /// <summary>
    /// The <c>errors</c> key a rejected grouping is reported under: the query-parameter name the
    /// caller sent, not a <c>nameof</c>, because that is the token the Group-by control owns.
    /// </summary>
    public const string GroupByKey = "groupBy";

    /// <summary>
    /// Gets the accepted values, in the order a rejection message lists them.
    /// </summary>
    public static IReadOnlyList<string> Supported { get; } = ["model", "provider", "user", "day"];

    private static readonly Dictionary<string, UsageGroupKey> ByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["model"] = UsageGroupKey.Model,
            ["provider"] = UsageGroupKey.Provider,
            ["user"] = UsageGroupKey.User,
            ["day"] = UsageGroupKey.Day
        };

    private static readonly Dictionary<UsageGroupKey, string> NamesByKey =
        ByName.ToDictionary(entry => entry.Value, entry => entry.Key);

    /// <summary>
    /// Gets the accepted values rendered for a validation message.
    /// </summary>
    public static string SupportedList { get; } = string.Join(", ", Supported.Select(name => $"'{name}'"));

    /// <summary>
    /// The grouping applied when the caller names none.
    /// </summary>
    /// <remarks>
    /// Model, because the question the dashboard exists to answer first is which models the money
    /// went to, and because it is the grouping the design board draws.
    /// </remarks>
    public const UsageGroupKey Default = UsageGroupKey.Model;

    /// <summary>
    /// Resolves the <c>?groupBy=</c> value a caller sent.
    /// </summary>
    /// <param name="groupBy">The requested dimension, or <see langword="null"/> for the default.</param>
    /// <returns>The dimension to group by.</returns>
    /// <exception cref="ValidationException">
    /// <paramref name="groupBy"/> names no accepted dimension. Surfaces as a 400 keyed
    /// <see cref="GroupByKey"/>.
    /// </exception>
    public static UsageGroupKey Resolve(string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
        {
            return Default;
        }

        if (ByName.TryGetValue(groupBy.Trim(), out var resolved))
        {
            return resolved;
        }

        // Refused rather than ignored, on the reasoning the sort parameters already settled: a
        // report grouped by something the caller did not ask for looks exactly like one that was.
        throw new ValidationException(
        [
            new ValidationFailure(
                GroupByKey,
                $"'{groupBy}' is not a supported grouping. Supported groupings are {SupportedList}.")
        ]);
    }

    /// <summary>
    /// Renders a resolved dimension back as the token a caller would send.
    /// </summary>
    /// <param name="key">The dimension.</param>
    /// <returns>The wire token.</returns>
    /// <remarks>
    /// The response echoes the grouping it applied, so a client that sent nothing still knows what
    /// it received.
    /// </remarks>
    public static string ToToken(UsageGroupKey key) => NamesByKey[key];
}
