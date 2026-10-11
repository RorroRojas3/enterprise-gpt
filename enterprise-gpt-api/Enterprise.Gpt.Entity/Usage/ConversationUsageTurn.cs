using Enterprise.Gpt.Entity.Base;

namespace Enterprise.Gpt.Entity.Usage;

/// <summary>
/// One model turn within a <see cref="ConversationUsage"/> call: what the provider reported for a
/// single iteration of the function-invocation loop.
/// </summary>
/// <remarks>
/// Append-only. Exists to attribute per-tool prompt cost: a tool's arguments and result bill in the next
/// turn's prompt, so <c>input(N+1) − input(N) − output(N)</c> is what turn <c>N</c>'s calls cost (see
/// <see cref="ConversationUsageToolCall.Iteration"/>). Values are stored as reported and the attribution
/// lives in queries. Rows exist only for streamed requests.
/// </remarks>
public class ConversationUsageTurn : BaseCreatedEntity<Guid>
{
    public Guid ConversationUsageId { get; set; }

    /// <summary>
    /// The zero-based model iteration of the function-invocation loop this row reports.
    /// </summary>
    /// <remarks>
    /// Joins to <see cref="ConversationUsageToolCall.Iteration"/>, which names the turn that issued
    /// each tool call.
    /// </remarks>
    public int Iteration { get; set; }

    /// <summary>
    /// The provider's response identifier for this turn, when it reported one. Carried for support
    /// correlation only; nothing joins on it.
    /// </summary>
    public string? ResponseId { get; set; }

    /// <summary>
    /// The model identifier the provider reported for this turn, when it reported one.
    /// </summary>
    /// <remarks>
    /// The provider's own string, not a catalog <c>ModelId</c>; <see cref="ConversationUsage"/> records the
    /// catalog row this application chose, which is a different claim.
    /// </remarks>
    public string? ReportedModelId { get; set; }

    /// <summary>
    /// Input tokens the provider reported for this turn, or <see langword="null"/> when it reported
    /// none. Null is not zero: it says nothing was reported.
    /// </summary>
    public long? InputTokens { get; set; }

    /// <summary>
    /// Output tokens the provider reported for this turn, on the same terms as
    /// <see cref="InputTokens"/>.
    /// </summary>
    public long? OutputTokens { get; set; }

    /// <summary>
    /// The turn's total, as reported rather than recomputed.
    /// </summary>
    /// <remarks>
    /// Stored for the same reason <see cref="ConversationUsageToolCall.TotalTokens"/> is: providers
    /// do not always agree that the total is the sum of its operands.
    /// </remarks>
    public long? TotalTokens { get; set; }

    /// <summary>
    /// Input tokens served from the provider's prompt cache, already counted inside
    /// <see cref="InputTokens"/>.
    /// </summary>
    /// <remarks>
    /// Cached tokens bill at a discount, so cost from <see cref="InputTokens"/> alone overstates; not yet
    /// priced (see <see cref="ConversationUsage.EstimatedCost"/>).
    /// </remarks>
    public long? CachedInputTokens { get; set; }

    /// <summary>
    /// Reasoning tokens the model produced internally, already counted inside
    /// <see cref="OutputTokens"/>.
    /// </summary>
    /// <remarks>
    /// Usually not replayed, so delta attribution subtracts them from <c>output(N)</c>. Except on Anthropic,
    /// which requires thinking blocks back with tool results and so rebills them; reports must check
    /// <see cref="ConversationUsage.ProviderId"/>.
    /// </remarks>
    public long? ReasoningTokens { get; set; }

    public ConversationUsage ConversationUsage { get; set; } = null!;
}
