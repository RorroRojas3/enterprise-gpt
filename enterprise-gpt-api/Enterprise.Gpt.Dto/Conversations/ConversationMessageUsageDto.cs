namespace Enterprise.Gpt.Dto.Conversations;

/// <summary>
/// What a turn consumed, as it was recorded beside the assistant message it produced.
/// </summary>
/// <remarks>
/// The turn's totals; the split between model turns and the tools beneath them lives in the relational
/// audit trail that reports read.
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
