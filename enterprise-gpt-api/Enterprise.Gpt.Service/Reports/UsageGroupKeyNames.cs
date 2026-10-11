using Enterprise.Gpt.Common.Enums;
using FluentValidation;
using FluentValidation.Results;

namespace Enterprise.Gpt.Service.Reports;

/// <summary>The <c>?groupBy=</c> values <c>GET api/reports/usage</c> accepts.</summary>
/// <remarks>
/// Shaped like the sort-key tables in <see cref="Enterprise.Gpt.Service.Sorting"/> so the advertised and
/// accepted sets cannot drift, but not built on <c>SortParameters.ResolveKey</c>, which reports under <c>sort</c>.
/// </remarks>
public static class UsageGroupKeyNames
{
    /// <summary>
    /// The <c>errors</c> key for a rejected grouping: the wire parameter name, which the client's control owns.
    /// </summary>
    public const string GroupByKey = "groupBy";

    /// <summary>The accepted values, in the order a rejection message lists them.</summary>
    public static IReadOnlyList<string> Supported { get; } = ["model", "provider", "user", "day"];

    private static readonly Dictionary<string, UsageGroupKeys> ByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["model"] = UsageGroupKeys.Model,
            ["provider"] = UsageGroupKeys.Provider,
            ["user"] = UsageGroupKeys.User,
            ["day"] = UsageGroupKeys.Day
        };

    private static readonly Dictionary<UsageGroupKeys, string> NamesByKey =
        ByName.ToDictionary(entry => entry.Value, entry => entry.Key);

    public static string SupportedList { get; } = string.Join(", ", Supported.Select(name => $"'{name}'"));

    /// <summary>The grouping applied when the caller names none.</summary>
    /// <remarks>Model, because which models the money went to is the dashboard's first question.</remarks>
    public const UsageGroupKeys Default = UsageGroupKeys.Model;

    #region Public static methods

    /// <summary>
    /// Resolves the <c>?groupBy=</c> value a caller sent, or <see cref="Default"/> when it is blank.
    /// </summary>
    /// <exception cref="ValidationException">
    /// <paramref name="groupBy"/> names no accepted dimension; surfaces as a 400 keyed <see cref="GroupByKey"/>.
    /// </exception>
    public static UsageGroupKeys Resolve(string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
        {
            return Default;
        }

        if (ByName.TryGetValue(groupBy.Trim(), out var resolved))
        {
            return resolved;
        }

        // Refused rather than ignored: a report grouped by something the caller did not ask for
        // looks exactly like one that was.
        throw new ValidationException(
        [
            new ValidationFailure(
                GroupByKey,
                $"'{groupBy}' is not a supported grouping. Supported groupings are {SupportedList}.")
        ]);
    }

    /// <summary>
    /// Renders a resolved dimension as its wire token, so the response can echo the grouping it applied.
    /// </summary>
    public static string ToToken(UsageGroupKeys key) => NamesByKey[key];

    #endregion
}
