namespace Enterprise.Gpt.Dto.Reports;

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
