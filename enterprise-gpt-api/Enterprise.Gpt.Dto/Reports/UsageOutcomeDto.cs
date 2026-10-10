namespace Enterprise.Gpt.Dto.Reports;

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
