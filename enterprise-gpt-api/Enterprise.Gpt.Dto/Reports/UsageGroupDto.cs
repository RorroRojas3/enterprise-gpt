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
