namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// Specifies which kind of model call a conversation usage record accounts for.
/// </summary>
public enum ConversationUsageKinds
{
    /// <summary>
    /// A chat turn: the user's prompt and the model's streamed answer.
    /// </summary>
    Chat = 1,

    /// <summary>
    /// The completion that names a conversation from its opening prompt. Billed like any
    /// other call, so it is recorded separately rather than folded into the first chat turn.
    /// </summary>
    Naming = 2,

    /// <summary>
    /// A document summarization run, billed to the conversation whose turn asked for it — including
    /// a run over a project document, which no conversation owns.
    /// </summary>
    /// <remarks>
    /// One row per run rather than per model call, matching how a chat turn is recorded: a run's
    /// map, collapse and reduce calls roll into one row the way a turn's iterations do, and the
    /// call count itself is persisted on the summary row. The row always carries the pinned
    /// summarizer's own model, deployment and prices, never the conversation's selected chat model.
    /// </remarks>
    Summarization = 3
}
