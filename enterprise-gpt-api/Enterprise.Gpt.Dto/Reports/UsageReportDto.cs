namespace Enterprise.Gpt.Dto;

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

/// <summary>
/// Aggregate figures for one period or one group.
/// </summary>
/// <remarks>
/// The token columns are reported separately and are <em>not</em> interchangeable. The three rules
/// <c>docs/conversations/usage-and-reporting.md</c> states apply verbatim to anything computed
/// from this type: the assistant's own turns and its tools are counted apart and summed only in
/// <see cref="TotalTokens"/>; <see cref="ContextTokens"/> is an estimate that sits beside the
/// billed columns and is never added to them; and nothing here is derived from per-turn or
/// per-tool-call rows, which would double-count what is already rolled up.
/// </remarks>
public record UsageReportTotalsDto
{
    /// <summary>Model calls in the period, of every kind and every outcome.</summary>
    /// <remarks>
    /// Always the sum of <see cref="CompletedTurns"/>, <see cref="CancelledTurns"/> and
    /// <see cref="FailedTurns"/>, because every call carries exactly one outcome.
    /// </remarks>
    public long Turns { get; init; }

    /// <summary>Calls whose response the model finished producing.</summary>
    public long CompletedTurns { get; init; }

    /// <summary>Calls the caller stopped, or whose client disconnected mid-stream.</summary>
    public long CancelledTurns { get; init; }

    /// <summary>Calls that faulted before the response completed.</summary>
    public long FailedTurns { get; init; }

    /// <summary>
    /// The subset of <see cref="Turns"/> that named a conversation rather than answering a prompt.
    /// </summary>
    /// <remarks>
    /// Roughly one per conversation created, so an outlier count means first turns that keep
    /// failing rather than an accounting bug. Carried alongside rather than subtracted: these
    /// tokens were billed, and a report that hid them would understate the invoice.
    /// </remarks>
    public long NamingTurns { get; init; }

    /// <summary>What those naming calls consumed, on the same terms as <see cref="TotalTokens"/>.</summary>
    public long NamingTokens { get; init; }

    /// <summary>Tokens the assistant's own model turns consumed, tools excluded.</summary>
    public long AssistantTokens { get; init; }

    /// <summary>Tokens consumed by every tool those turns invoked, nested calls included.</summary>
    public long ToolTokens { get; init; }

    /// <summary>
    /// The billed total: <see cref="AssistantTokens"/> plus <see cref="ToolTokens"/>.
    /// </summary>
    public long TotalTokens { get; init; }

    /// <summary>
    /// What the transcribed messages weigh, estimated locally, with untranscribed turns counted
    /// as zero.
    /// </summary>
    /// <remarks>
    /// Reported beside <see cref="TotalTokens"/> and never inside it. This is an estimate of what
    /// the conversation history costs to replay, not a charge, and the two are supposed to
    /// disagree — the more tools a period ran, the further apart they sit, because tool calls,
    /// tool results and reasoning are all billed and never transcribed.
    /// </remarks>
    public long ContextTokens { get; init; }

    /// <summary>
    /// What the period cost at the prices captured on each call, in USD, or <see langword="null"/>
    /// when nothing in it was priced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null rather than zero, which is the difference between nothing having been spent and
    /// nothing having been priceable. Calls missing either price contribute nothing and are
    /// counted in <see cref="UnpricedTurns"/>, so a non-zero count there means this figure is a
    /// floor.
    /// </para>
    /// <para>
    /// An approximation by construction, for two reasons that cannot be closed from this data:
    /// tool tokens are priced at the rate of the model that drove the turn, because nothing
    /// records which model ran inside a tool; and cached input bills at a discount the catalog has
    /// no column for, so a heavily cached period is overstated.
    /// </para>
    /// </remarks>
    public decimal? EstimatedCost { get; init; }

    /// <summary>
    /// Calls that carried no price and so contributed nothing to <see cref="EstimatedCost"/>.
    /// </summary>
    /// <remarks>
    /// Part of the answer rather than diagnostics: it is the only thing that distinguishes a cheap
    /// period from an unpriced one. The fix is a price on the catalog row, which applies from then
    /// on and never retroactively.
    /// </remarks>
    public long UnpricedTurns { get; init; }

    /// <summary>Distinct users who ran at least one call in the period.</summary>
    public int ActiveUsers { get; init; }

    /// <summary>Distinct conversations at least one call in the period was charged to.</summary>
    /// <remarks>
    /// Conversations that were <em>used</em> in the period, which is not the same as conversations
    /// created in it — an older thread picked up again counts here.
    /// </remarks>
    public int Conversations { get; init; }
}

/// <summary>
/// One row of the grouped breakdown.
/// </summary>
public record UsageGroupDto
{
    /// <summary>
    /// The group's stable identity: a model, provider or user id, or an ISO-8601 date.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Label"/> so a client can key rows on something a rename cannot
    /// move, and so two users who share a display name remain two rows.
    /// </remarks>
    public string Key { get; init; } = null!;

    /// <summary>
    /// What to call the group on screen.
    /// </summary>
    /// <remarks>
    /// Read from the catalog, so it is what the model or provider is called <em>now</em> — while
    /// every figure on this row comes from the snapshot taken when each call ran. A rename
    /// therefore relabels history and a repricing never rewrites it, which is the pairing the
    /// price and deployment-name snapshots exist to make possible.
    /// </remarks>
    public string Label { get; init; } = null!;

    /// <summary>
    /// A second line under <see cref="Label"/>, or <see langword="null"/> where there is none.
    /// </summary>
    /// <remarks>
    /// Populated only when grouping by user, where it carries the email address: display names
    /// collide, and spend attributed to the wrong person is worse than a label nobody reads. Every
    /// other grouping has a label that is already unique.
    /// </remarks>
    public string? Sublabel { get; init; }

    /// <inheritdoc cref="UsageReportTotalsDto.Turns"/>
    public long Turns { get; init; }

    /// <inheritdoc cref="UsageReportTotalsDto.CompletedTurns"/>
    public long CompletedTurns { get; init; }

    /// <inheritdoc cref="UsageReportTotalsDto.CancelledTurns"/>
    public long CancelledTurns { get; init; }

    /// <inheritdoc cref="UsageReportTotalsDto.FailedTurns"/>
    public long FailedTurns { get; init; }

    /// <inheritdoc cref="UsageReportTotalsDto.AssistantTokens"/>
    public long AssistantTokens { get; init; }

    /// <inheritdoc cref="UsageReportTotalsDto.ToolTokens"/>
    public long ToolTokens { get; init; }

    /// <inheritdoc cref="UsageReportTotalsDto.TotalTokens"/>
    public long TotalTokens { get; init; }

    /// <inheritdoc cref="UsageReportTotalsDto.EstimatedCost"/>
    public decimal? EstimatedCost { get; init; }

    /// <inheritdoc cref="UsageReportTotalsDto.UnpricedTurns"/>
    public long UnpricedTurns { get; init; }
}

/// <summary>
/// How many calls ended one way, and what they consumed.
/// </summary>
public record UsageOutcomeDto
{
    /// <summary>
    /// The outcome, as the name of a <c>ConversationUsageStatuses</c> member:
    /// <c>Completed</c>, <c>Cancelled</c> or <c>Failed</c>.
    /// </summary>
    /// <remarks>
    /// Sent as a name rather than the stored integer. Nothing in this API registers a
    /// <c>JsonStringEnumConverter</c>, so enum values travel as numbers elsewhere — but those are
    /// values a client posts back, whereas this one is only ever read, and a report legible in a
    /// network trace is worth more here than symmetry with the routes that take input.
    /// </remarks>
    public string Status { get; init; } = null!;

    /// <summary>Calls that ended this way.</summary>
    public long Turns { get; init; }

    /// <summary>What they consumed, billed model and tool tokens together.</summary>
    /// <remarks>
    /// For the two unsuccessful outcomes this is the number the audit trail exists to surface:
    /// tokens billed for answers nobody read. It is a lower bound on a cancelled call, because a
    /// provider reports usage when a turn ends and a turn abandoned mid-stream reports what it had
    /// reached rather than everything it would have produced.
    /// </remarks>
    public long TotalTokens { get; init; }
}

/// <summary>
/// One day of the reported window.
/// </summary>
public record UsageSeriesPointDto
{
    /// <summary>The UTC day, as <c>yyyy-MM-dd</c>.</summary>
    public DateOnly Date { get; init; }

    /// <inheritdoc cref="UsageReportTotalsDto.TotalTokens"/>
    public long TotalTokens { get; init; }

    /// <inheritdoc cref="UsageReportTotalsDto.EstimatedCost"/>
    public decimal? EstimatedCost { get; init; }
}
