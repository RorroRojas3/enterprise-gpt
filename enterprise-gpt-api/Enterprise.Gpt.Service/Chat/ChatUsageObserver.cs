using Andes.Extensions.AI;

namespace Enterprise.Gpt.Service.Chat;

/// <summary>
/// Routes the tracking middleware's per-request usage report to the <see cref="ChatUsageScope"/>
/// the calling flow opened.
/// </summary>
/// <remarks>
/// Registered as a singleton and picked up automatically: <c>UseToolTracking()</c> appends every
/// <see cref="IChatProgressObserver"/> in the container to its options when the chat client is
/// built. It is stateless by design — everything per-request lives on the ambient scope.
/// </remarks>
public sealed class ChatUsageObserver : IChatProgressObserver
{
    /// <inheritdoc />
    /// <remarks>
    /// Nothing to do: progress reaches the client in-band, as synthetic content on the streamed
    /// response, and none of it is persisted.
    /// </remarks>
    public void OnProgress(ChatProgressUpdate update)
    {
    }

    /// <inheritdoc />
    public void OnRequestCompleted(ChatUsageReport report)
    {
        // A request issued outside any scope — a document-pipeline call, or a unit test — has
        // nowhere to report to and is not an error.
        ChatUsageScope.Current?.Complete(report);
    }
}
