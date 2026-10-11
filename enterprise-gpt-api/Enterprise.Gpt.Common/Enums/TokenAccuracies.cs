namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// How a transcript message's token count was arrived at.
/// </summary>
/// <remarks>
/// Recorded per message because a budget built on an estimate needs to know it carries tokenizer error.
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
    /// Nothing writes this yet; it reserves the distinction so exact counts need no schema change later.
    /// </remarks>
    Exact = 2
}
