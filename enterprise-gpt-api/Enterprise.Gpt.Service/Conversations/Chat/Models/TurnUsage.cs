using Enterprise.Gpt.Entity.Usage;

namespace Enterprise.Gpt.Service.Conversations.Chat.Models;

/// <summary>
/// What a finished chat request cost, in the shape the audit tables store it.
/// </summary>
/// <param name="InputTokens">Input tokens consumed by the assistant's own model turns.</param>
/// <param name="OutputTokens">Output tokens produced by the assistant's own model turns.</param>
/// <param name="ToolInputTokens">Input tokens consumed by every tool the request invoked.</param>
/// <param name="ToolOutputTokens">Output tokens produced by every tool the request invoked.</param>
/// <param name="CachedInputTokens">
/// Input tokens the assistant's turns served from the provider's prompt cache, already counted
/// inside <paramref name="InputTokens"/>. <see langword="null"/> when nothing reported one, which
/// is not the same as a cache miss.
/// </param>
/// <param name="ReasoningTokens">
/// Reasoning tokens the assistant's turns produced, already counted inside
/// <paramref name="OutputTokens"/>, on the same null terms.
/// </param>
/// <param name="Turns">
/// What the provider reported for each model turn, in report order. <b>Empty for a request that
/// did not stream</b> — providers report one aggregate then and the middleware surfaces no
/// breakdown — which is not the same as a request that had no turns.
/// </param>
/// <param name="ToolCalls">
/// Every tool invocation the request made, flat and in pre-order, with nesting expressed through
/// each row's <see cref="ConversationUsageToolCall.Children"/>.
/// </param>
/// <remarks>
/// <see cref="ToolCalls"/> is flat rather than a forest of roots because it is assigned straight to
/// <see cref="ConversationUsage.ToolCalls"/>: that navigation is what tells EF each row belongs to
/// the audit row, and a nested call reachable only through its parent would be saved with no owning
/// usage id at all. Roots are the entries whose
/// <see cref="ConversationUsageToolCall.Depth"/> is zero.
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
    /// Every input token the request consumed: the assistant's own turns plus its tools.
    /// </summary>
    /// <remarks>
    /// Derived from the same numbers written to the audit row rather than read separately off the
    /// report, so a conversation's running counters can never disagree with the sum of its usage
    /// rows.
    /// </remarks>
    public long TotalInputTokens => InputTokens + ToolInputTokens;

    /// <summary>
    /// Every output token the request produced, on the same terms as <see cref="TotalInputTokens"/>.
    /// </summary>
    public long TotalOutputTokens => OutputTokens + ToolOutputTokens;

    #region Public static methods

    /// <summary>
    /// Creates an empty result, for a request that produced no report at all.
    /// </summary>
    /// <returns>A result with no tokens, no tool calls and no turns.</returns>
    /// <remarks>
    /// A method rather than a shared instance: <see cref="ToolCalls"/> and <see cref="Turns"/> are
    /// assigned straight into EF navigations, which the change tracker then owns and mutates in
    /// place, so handing out the same list twice would let one turn's fixup append entities to
    /// another turn's graph.
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
