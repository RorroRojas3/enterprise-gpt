namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// Specifies how the model call a conversation usage record accounts for ended.
/// </summary>
/// <remarks>
/// Tokens consumed before a stream is abandoned are still billed, so incomplete calls are recorded;
/// reports that want whole answers filter on <see cref="Completed"/>.
/// </remarks>
public enum ConversationUsageStatuses
{
    Completed = 1,

    /// <summary>
    /// The caller cancelled the request or the client disconnected mid-stream.
    /// </summary>
    Cancelled = 2,

    Failed = 3
}
