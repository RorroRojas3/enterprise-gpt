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
    Naming = 2
}
