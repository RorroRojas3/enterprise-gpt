namespace Enterprise.Gpt.Dto.Reports;

/// <summary>
/// One row of the grouped breakdown.
/// </summary>
public record UsageGroupDto
{
    /// <summary>
    /// The group's stable identity: a model, provider or user id, or an ISO-8601 date.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Label"/> so rows survive renames and users sharing a name stay apart.
    /// </remarks>
    public string Key { get; init; } = null!;

    /// <summary>
    /// What to call the group on screen.
    /// </summary>
    /// <remarks>
    /// The catalog's current name, while the figures come from per-call snapshots: a rename relabels
    /// history, a repricing never rewrites it.
    /// </remarks>
    public string Label { get; init; } = null!;

    /// <summary>
    /// A second line under <see cref="Label"/>, or <see langword="null"/> where there is none.
    /// </summary>
    /// <remarks>
    /// Only set when grouping by user, carrying the email because display names collide.
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
