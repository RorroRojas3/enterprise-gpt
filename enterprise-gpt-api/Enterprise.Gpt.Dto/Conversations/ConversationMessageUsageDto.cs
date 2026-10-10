namespace Enterprise.Gpt.Dto.Conversations;

/// <summary>
/// What a turn consumed, as it was recorded beside the assistant message it produced.
/// </summary>
/// <remarks>
/// Carries the turn's totals rather than the assistant's share alone. The split between the
/// assistant's own model turns and the tools underneath them lives in the relational audit
/// trail, which is where a report reads it from; a reader looking at one message wants the one
/// number that message cost.
/// </remarks>
public class ConversationMessageUsageDto
{
    /// <summary>
    /// The turn's total input tokens, tools included.
    /// </summary>
    public long InputTokens { get; set; }

    /// <summary>
    /// The turn's total output tokens, tools included.
    /// </summary>
    public long OutputTokens { get; set; }
}
