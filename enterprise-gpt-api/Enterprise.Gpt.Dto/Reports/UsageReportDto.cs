using Enterprise.Gpt.Dto.Pagination;

namespace Enterprise.Gpt.Dto.Reports;

/// <summary>
/// An aggregated view of <c>Core.ConversationUsage</c> over one period, as
/// <c>GET api/reports/usage</c> returns it.
/// </summary>
/// <remarks>
/// Not a <see cref="PaginatedResponseDto{T}"/>: one request serves the whole dashboard. Figures include
/// naming calls (see <see cref="UsageReportTotalsDto.NamingTurns"/>), unlike the <c>Kind = 1</c> query in
/// <c>docs/conversations/usage-and-reporting.md</c>, so every card agrees. Nothing is sourced from the
/// transcript or <c>Core.ConversationUsageToolCall</c>, so no prompt or tool content can leak.
/// </remarks>
public record UsageReportDto
{
    /// <summary>
    /// The inclusive start of the reported window, as the server resolved it.
    /// </summary>
    /// <remarks>
    /// Echoed because the caller may omit both bounds; the client renders its range from these.
    /// </remarks>
    public DateTimeOffset From { get; init; }

    /// <summary>
    /// The exclusive end of the reported window, as the server resolved it.
    /// </summary>
    public DateTimeOffset To { get; init; }

    /// <summary>
    /// The dimension <see cref="Groups"/> is grouped by, as the server resolved it:
    /// <c>model</c>, <c>provider</c>, <c>user</c> or <c>day</c>.
    /// </summary>
    public string GroupBy { get; init; } = null!;

    /// <summary>
    /// Everything in the window, ungrouped.
    /// </summary>
    public UsageReportTotalsDto Totals { get; init; } = null!;

    /// <summary>
    /// The same figures over the equal-length window ending where <see cref="From"/> begins.
    /// </summary>
    /// <remarks>
    /// Raw figures, not a percentage, so a zero prior period is distinguishable from an unmeasured one.
    /// </remarks>
    public UsageReportTotalsDto PriorTotals { get; init; } = null!;

    /// <summary>
    /// The window split by how each call ended. One entry per outcome that occurred; an outcome
    /// with no calls is absent rather than zero.
    /// </summary>
    public IReadOnlyList<UsageOutcomeDto> Outcomes { get; init; } = [];

    /// <summary>
    /// One entry per UTC day in the window, including days with no traffic.
    /// </summary>
    /// <remarks>
    /// Dense on purpose: a sparse series drawn as a line silently rescales the time axis.
    /// </remarks>
    public IReadOnlyList<UsageSeriesPointDto> Series { get; init; } = [];

    /// <summary>
    /// The window grouped by <see cref="GroupBy"/>, ordered by total tokens descending.
    /// </summary>
    public IReadOnlyList<UsageGroupDto> Groups { get; init; } = [];

    /// <summary>
    /// The ten models that consumed the most tokens in the window, ordered descending.
    /// </summary>
    /// <remarks>
    /// Always by model, whatever <see cref="GroupBy"/> says, to save a round trip per regrouping; it
    /// duplicates the head of <see cref="Groups"/> when grouping by model.
    /// </remarks>
    public IReadOnlyList<UsageGroupDto> TopModels { get; init; } = [];

    /// <summary>
    /// Whether <see cref="Groups"/> was cut short at <see cref="GroupLimit"/>.
    /// </summary>
    /// <remarks>
    /// In practice only when grouping by user. <see cref="Totals"/> always covers the whole window.
    /// </remarks>
    public bool GroupsTruncated { get; init; }

    /// <summary>
    /// The most groups this deployment will return, from <c>Reports:MaxGroups</c>.
    /// </summary>
    public int GroupLimit { get; init; }
}
