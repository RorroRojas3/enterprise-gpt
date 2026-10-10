namespace Enterprise.Gpt.Service.Reports.Models;

/// <summary>
/// One grouped row as the database returns it, before it is given a label.
/// </summary>
/// <typeparam name="TKey">The grouping key, usually an anonymous type of key columns.</typeparam>
internal sealed record AggregateRow<TKey>
    where TKey : notnull
{
    public TKey Key { get; init; } = default!;

    public long Turns { get; init; }

    public long CompletedTurns { get; init; }

    public long CancelledTurns { get; init; }

    public long FailedTurns { get; init; }

    public long NamingTurns { get; init; }

    public long NamingTokens { get; init; }

    public long AssistantTokens { get; init; }

    public long ToolTokens { get; init; }

    public long TotalTokens { get; init; }

    public long ContextTokens { get; init; }

    /// <summary>
    /// What the priced calls in this group cost, as the database added them up.
    /// </summary>
    /// <remarks>
    /// Not the figure to report. Read <see cref="EstimatedCost"/> instead, which restores the
    /// distinction this one loses.
    /// </remarks>
    public decimal? PricedCost { get; init; }

    public long UnpricedTurns { get; init; }

    /// <summary>
    /// What the group cost, or <see langword="null"/> when nothing in it could be priced.
    /// </summary>
    /// <remarks>
    /// Derived rather than read straight off the aggregate, because the provider does not preserve
    /// the distinction on its own: SQL Server sums an all-NULL set to NULL, but EF wraps the
    /// aggregate in a COALESCE that turns that into zero — and zero reads as free where the honest
    /// answer is unpriceable. The comparison below restores it from a count the same query already
    /// produced, so it costs no extra work and cannot disagree with what was summed.
    /// </remarks>
    public decimal? EstimatedCost => UnpricedTurns == Turns ? null : PricedCost;
}
