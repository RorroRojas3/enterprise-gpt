using Enterprise.Gpt.Dto.Pagination;

namespace Enterprise.Gpt.Dto.Reports;

/// <summary>
/// An aggregated view of <c>Core.ConversationUsage</c> over one period, as
/// <c>GET api/reports/usage</c> returns it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a <see cref="PaginatedResponseDto{T}"/>: this is period totals, a
/// prior-period comparison and several differently-shaped row sets answered together, not a page
/// of one collection. One request serves the whole dashboard, because four round trips for four
/// cards on one screen would make the range control feel broken.
/// </para>
/// <para>
/// Every figure here counts <em>both</em> kinds of call — a chat turn and the out-of-band naming
/// completion alike. Spend is spend, and a naming call is billed against a real deployment like
/// any other; <see cref="UsageReportTotalsDto.NamingTurns"/> carries the overhead separately for
/// anyone who needs to see it apart. This departs from the outcome query in
/// <c>docs/conversations/usage-and-reporting.md</c>, which filters <c>Kind = 1</c>: that
/// cookbook is SQL for an analyst, and a donut whose turn count disagreed with the table beside
/// it would be a defect on screen.
/// </para>
/// <para>
/// It carries no prompt content, no tool arguments and no tool results, and structurally cannot:
/// nothing in it is sourced from the transcript or from <c>Core.ConversationUsageToolCall</c>.
/// </para>
/// </remarks>
public record UsageReportDto
{
    /// <summary>
    /// The inclusive start of the reported window, as the server resolved it.
    /// </summary>
    /// <remarks>
    /// Echoed rather than assumed, because the caller may send neither bound. The client renders
    /// its date range from these two values, so what it displays is always what was measured.
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
    /// Supplied rather than a percentage, so the client decides how to render a delta — and so a
    /// prior period of zero is distinguishable from a prior period that was not measured, which a
    /// precomputed percentage could not express.
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
    /// Dense on purpose. A sparse series drawn as a line silently rescales the horizontal axis, so
    /// a quiet fortnight and a busy one occupy the same width and the shape of the chart lies.
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
    /// Always by model, whatever <see cref="GroupBy"/> says, because the chart it feeds asks a
    /// fixed question — which models cost the most — that does not change when the table beside it
    /// is re-cut. Answered here rather than by a second request so one grouping change is one
    /// round trip. When <see cref="GroupBy"/> is <c>model</c> this is the head of
    /// <see cref="Groups"/>, and the duplication is the price of the two staying independent.
    /// </remarks>
    public IReadOnlyList<UsageGroupDto> TopModels { get; init; } = [];

    /// <summary>
    /// Whether <see cref="Groups"/> was cut short at <see cref="GroupLimit"/>.
    /// </summary>
    /// <remarks>
    /// Reachable in practice only when grouping by user, where the number of groups is the number
    /// of people who used the platform. <see cref="Totals"/> is always computed over the whole
    /// window regardless, so a truncated list never makes the headline figures wrong — only
    /// incomplete as a breakdown, which is what this flag exists to say.
    /// </remarks>
    public bool GroupsTruncated { get; init; }

    /// <summary>
    /// The most groups this deployment will return, from <c>Reports:MaxGroups</c>.
    /// </summary>
    public int GroupLimit { get; init; }
}
