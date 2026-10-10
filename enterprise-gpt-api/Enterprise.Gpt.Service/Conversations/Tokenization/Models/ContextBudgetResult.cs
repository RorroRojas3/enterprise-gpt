namespace Enterprise.Gpt.Service.Conversations.Tokenization.Models;

/// <summary>
/// What a turn's history looked like after the budget was applied.
/// </summary>
/// <param name="KeptIndexes">
/// The positions, in the original list, of the messages that fit — in their original order.
/// </param>
/// <param name="DroppedMessages">How many messages were dropped.</param>
/// <param name="DroppedTokens">How many tokens those messages held.</param>
/// <param name="IsUnbounded">Whether the model declared no context window, so nothing was measured.</param>
/// <param name="Fits">
/// Whether the result fits the budget. False only when what cannot be dropped — the system message
/// and the current prompt — exceeds it on its own.
/// </param>
public sealed record ContextBudgetResult(
    IReadOnlyList<int> KeptIndexes,
    int DroppedMessages,
    long DroppedTokens,
    bool IsUnbounded,
    bool Fits);
