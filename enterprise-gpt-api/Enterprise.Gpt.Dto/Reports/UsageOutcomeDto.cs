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
    /// A name, unlike enums elsewhere in the API: this value is read-only, so a legible trace beats
    /// symmetry with input routes.
    /// </remarks>
    public string Status { get; init; } = null!;

    /// <summary>Calls that ended this way.</summary>
    public long Turns { get; init; }

    /// <summary>What they consumed, billed model and tool tokens together.</summary>
    /// <remarks>
    /// A lower bound for cancelled calls: a turn abandoned mid-stream reports only the usage it reached.
    /// </remarks>
    public long TotalTokens { get; init; }
}
