namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// How a transcript message's token count was arrived at.
/// </summary>
/// <remarks>
/// Recorded per message rather than assumed globally, because the two are not interchangeable: an
/// estimate carries the tokenizer's error against whatever the provider actually charges, and a
/// budget built on it needs to know which it is holding.
/// </remarks>
public enum TokenAccuracies
{
    /// <summary>
    /// Counted locally with a tiktoken vocabulary, and therefore approximate for any provider whose
    /// tokenizer is not tiktoken.
    /// </summary>
    Estimated = 1,

    /// <summary>
    /// Reported by the provider that served the message, and therefore exact.
    /// </summary>
    /// <remarks>
    /// Nothing writes this yet. It exists so the field can distinguish the two the day an exact
    /// count is available — from a provider token-count endpoint, say — without a schema change or
    /// a re-interpretation of every row already written.
    /// </remarks>
    Exact = 2
}
