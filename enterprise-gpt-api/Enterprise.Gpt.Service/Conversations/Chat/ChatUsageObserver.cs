using Andes.Extensions.AI;

namespace Enterprise.Gpt.Service.Conversations.Chat;

/// <summary>
/// Routes the tracking middleware's per-request usage report to the <see cref="ChatUsageScope"/>
/// the calling flow opened.
/// </summary>
/// <remarks>
/// A stateless singleton; <c>UseToolTracking()</c> picks up every <see cref="IChatProgressObserver"/>
/// in the container.
/// </remarks>
public sealed class ChatUsageObserver : IChatProgressObserver
{
    /// <inheritdoc />
    /// <remarks>Progress reaches the client in-band on the stream and is not persisted.</remarks>
    public void OnProgress(ChatProgressUpdate update)
    {
    }

    /// <inheritdoc />
    public void OnRequestCompleted(ChatUsageReport report)
    {
        // Requests outside any scope (document pipeline, unit tests) have nowhere to report to.
        ChatUsageScope.Current?.Complete(report);
    }
}
