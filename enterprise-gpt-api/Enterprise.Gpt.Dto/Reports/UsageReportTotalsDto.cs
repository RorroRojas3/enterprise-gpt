namespace Enterprise.Gpt.Dto.Reports;

/// <summary>
/// Aggregate figures for one period or one group.
/// </summary>
/// <remarks>
/// The token columns are not interchangeable; the rules in <c>docs/conversations/usage-and-reporting.md</c>
/// apply. Assistant and tool tokens are summed only in <see cref="TotalTokens"/>, <see cref="ContextTokens"/>
/// is never added to billed columns, and nothing derives from per-turn or per-tool-call rows (double count).
/// </remarks>
public record UsageReportTotalsDto
{
    /// <summary>Model calls in the period, of every kind and every outcome.</summary>
    /// <remarks>
    /// Always <see cref="CompletedTurns"/> + <see cref="CancelledTurns"/> + <see cref="FailedTurns"/>.
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
    /// Roughly one per new conversation; an outlier means first turns keep failing. Included in
    /// <see cref="Turns"/> rather than subtracted, because these tokens were billed.
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
    /// A replay-cost estimate, never added to <see cref="TotalTokens"/>; the two diverge as tool calls,
    /// tool results and reasoning are billed but never transcribed.
    /// </remarks>
    public long ContextTokens { get; init; }

    /// <summary>
    /// What the period cost at the prices captured on each call, in USD, or <see langword="null"/>
    /// when nothing in it was priced.
    /// </summary>
    /// <remarks>
    /// A floor when <see cref="UnpricedTurns"/> is non-zero. Approximate: tool tokens use the driving
    /// model's rate and cached-input discounts are not modelled, so heavily cached periods overstate.
    /// </remarks>
    public decimal? EstimatedCost { get; init; }

    /// <summary>
    /// Calls that carried no price and so contributed nothing to <see cref="EstimatedCost"/>.
    /// </summary>
    /// <remarks>
    /// Distinguishes a cheap period from an unpriced one. A catalog price applies only going forward.
    /// </remarks>
    public long UnpricedTurns { get; init; }

    /// <summary>Distinct users who ran at least one call in the period.</summary>
    public int ActiveUsers { get; init; }

    /// <summary>Distinct conversations at least one call in the period was charged to.</summary>
    /// <remarks>
    /// Conversations <em>used</em> in the period, not created in it.
    /// </remarks>
    public int Conversations { get; init; }
}
