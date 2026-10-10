using System.ComponentModel.DataAnnotations;

namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Usage-reporting configuration, bound from the <c>Reports</c> section and validated at startup.
/// </summary>
public sealed class ReportOptions
{
    /// <summary>
    /// Configuration section this type binds from.
    /// </summary>
    public const string SectionName = "Reports";

    /// <summary>
    /// The longest window <c>GET api/reports/usage</c> will aggregate, in days. Default: 366.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than a clamp, because a caller who asked for two years and silently got one
    /// would read the answer as two years' spend. <c>Core.ConversationUsage</c> is the
    /// fastest-growing table in the schema and nothing prunes it, so an unbounded range is an
    /// unbounded scan. A year and a day covers every calendar comparison a reader is likely to
    /// want, including a leap year.
    /// </remarks>
    [Range(1, 3660)]
    public int MaxRangeDays { get; set; } = 366;

    /// <summary>
    /// The most groups one report returns before the breakdown is cut short. Default: 500.
    /// </summary>
    /// <remarks>
    /// Only reachable when grouping by user, where the number of groups is the number of people who
    /// used the platform; a model, provider or day grouping is bounded well below it by the
    /// catalog and by <see cref="MaxRangeDays"/>. The period totals are computed over the whole
    /// window regardless of this, so truncation makes the breakdown incomplete and never makes the
    /// headline figures wrong.
    /// </remarks>
    [Range(1, 5000)]
    public int MaxGroups { get; set; } = 500;
}
