namespace Enterprise.Gpt.Dto.Reports;

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
