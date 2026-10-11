namespace Enterprise.Gpt.Service.Reports.Models;

/// <summary>One grouped row as the database returns it, before it is given a label.</summary>
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

    /// <summary>The database's sum of the priced calls; report <see cref="EstimatedCost"/> instead.</summary>
    public decimal? PricedCost { get; init; }

    public long UnpricedTurns { get; init; }

    /// <summary>What the group cost, or <see langword="null"/> when nothing in it could be priced.</summary>
    /// <remarks>
    /// Derived because EF wraps the SUM in a COALESCE, so an all-NULL (unpriceable) group would read as free.
    /// </remarks>
    public decimal? EstimatedCost => UnpricedTurns == Turns ? null : PricedCost;
}
