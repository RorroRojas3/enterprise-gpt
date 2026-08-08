namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// Specifies how the model call a conversation usage record accounts for ended.
/// </summary>
/// <remarks>
/// Tokens consumed before a stream is abandoned are still billed, so incomplete calls are
/// recorded rather than dropped; reports that only want whole answers filter on
/// <see cref="Completed"/>.
/// </remarks>
public enum ConversationUsageStatuses
{
    /// <summary>
    /// The model produced its complete response.
    /// </summary>
    Completed = 1,

    /// <summary>
    /// The caller cancelled the request or the client disconnected mid-stream.
    /// </summary>
    Cancelled = 2,

    /// <summary>
    /// The call faulted before the response completed.
    /// </summary>
    Failed = 3
}
