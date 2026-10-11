namespace Enterprise.Gpt.Service.Conversations.Tokenization.Models;

/// <summary>
/// What a turn's history looked like after the budget was applied.
/// </summary>
/// <param name="KeptIndexes">Original-list positions of the messages that fit, in order.</param>
/// <param name="DroppedMessages">How many messages were dropped.</param>
/// <param name="DroppedTokens">How many tokens those messages held.</param>
/// <param name="IsUnbounded">Whether the model declared no context window, so nothing was measured.</param>
/// <param name="Fits">False only when the system message and current prompt alone exceed the budget.</param>
public sealed record ContextBudgetResult(
    IReadOnlyList<int> KeptIndexes,
    int DroppedMessages,
    long DroppedTokens,
    bool IsUnbounded,
    bool Fits);
