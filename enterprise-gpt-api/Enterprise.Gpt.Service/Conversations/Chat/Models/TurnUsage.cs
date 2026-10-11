using Enterprise.Gpt.Entity.Usage;

namespace Enterprise.Gpt.Service.Conversations.Chat.Models;

/// <summary>
/// What a finished chat request cost, in the shape the audit tables store it.
/// </summary>
/// <param name="InputTokens">The assistant's own model turns, excluding tools.</param>
/// <param name="OutputTokens">The assistant's own model turns, excluding tools.</param>
/// <param name="ToolInputTokens">Every tool the request invoked.</param>
/// <param name="ToolOutputTokens">Every tool the request invoked.</param>
/// <param name="CachedInputTokens">
/// Already inside <paramref name="InputTokens"/>; <see langword="null"/> when unreported, not a cache miss.
/// </param>
/// <param name="ReasoningTokens">
/// Already inside <paramref name="OutputTokens"/>, on the same null terms.
/// </param>
/// <param name="Turns">Per model turn in report order; empty for a non-streamed request.</param>
/// <param name="ToolCalls">
/// Every invocation, flat and in pre-order, nested through <see cref="ConversationUsageToolCall.Children"/>.
/// </param>
/// <remarks>
/// <see cref="ToolCalls"/> is flat because assigning it to <see cref="ConversationUsage.ToolCalls"/> is how
/// EF gives each row its owning usage id; roots have <see cref="ConversationUsageToolCall.Depth"/> zero.
/// </remarks>
internal sealed record TurnUsage(
    long InputTokens,
    long OutputTokens,
    long ToolInputTokens,
    long ToolOutputTokens,
    long? CachedInputTokens,
    long? ReasoningTokens,
    List<ConversationUsageToolCall> ToolCalls,
    List<ConversationUsageTurn> Turns)
{
    /// <summary>
    /// Assistant plus tool input tokens, derived from the audit-row numbers so a conversation's running
    /// counters always match its usage rows.
    /// </summary>
    public long TotalInputTokens => InputTokens + ToolInputTokens;

    /// <summary>
    /// Every output token the request produced, on the same terms as <see cref="TotalInputTokens"/>.
    /// </summary>
    public long TotalOutputTokens => OutputTokens + ToolOutputTokens;

    #region Public static methods

    /// <summary>Creates an empty result, for a request that produced no report at all.</summary>
    /// <remarks>
    /// Not a shared instance: the lists become EF navigations the change tracker mutates, so sharing them
    /// would let one turn's fixup append entities to another's graph.
    /// </remarks>
    public static TurnUsage Empty()
    {
        return new TurnUsage(
            InputTokens: 0,
            OutputTokens: 0,
            ToolInputTokens: 0,
            ToolOutputTokens: 0,
            CachedInputTokens: null,
            ReasoningTokens: null,
            ToolCalls: [],
            Turns: []);
    }

    #endregion
}
