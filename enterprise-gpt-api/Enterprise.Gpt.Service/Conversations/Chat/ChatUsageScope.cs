using Andes.Extensions.AI;

namespace Enterprise.Gpt.Service.Conversations.Chat;

/// <summary>
/// Collects the <see cref="ChatUsageReport"/> produced by one tracked chat request, so the code
/// that started the request can read it back after the request ends however it ended.
/// </summary>
/// <remarks>
/// Fed by <see cref="ChatUsageObserver"/>, not the in-band final update, which cancelled or faulted turns
/// never reach. The report is written into this object rather than the ambient because the middleware
/// may complete a request on its own thread.
/// </remarks>
public sealed class ChatUsageScope
{
    private static readonly AsyncLocal<ChatUsageScope?> _current = new();

    private ChatUsageReport? _report;

    private ChatUsageScope()
    {
    }

    internal static ChatUsageScope? Current => _current.Value;

    /// <summary>
    /// Gets the request's report, or <see langword="null"/> when tracking is not installed or the
    /// request failed before the middleware accounted for anything.
    /// </summary>
    /// <remarks>
    /// For a streamed request, read after its enumerator is disposed; disposal finalizes an abandoned request.
    /// </remarks>
    public ChatUsageReport? Report => Volatile.Read(ref _report);

    /// <summary>
    /// Makes this scope the current flow's report target until the returned handle is disposed.
    /// </summary>
    /// <remarks>
    /// An async iterator drops <see cref="AsyncLocal{T}"/> writes after a <c>yield</c>, so enter around
    /// each <c>MoveNextAsync</c> of the inner enumerator.
    /// </remarks>
    public Activation Enter()
    {
        return new Activation(this);
    }

    /// <remarks>
    /// Last write wins: a tool running its own tracked pipeline can deliver a second report, and losing
    /// the outer one beats throwing inside an observer.
    /// </remarks>
    internal void Complete(ChatUsageReport report)
    {
        Volatile.Write(ref _report, report);
    }

    /// <summary>
    /// The window during which a <see cref="ChatUsageScope"/> is the current flow's report target.
    /// </summary>
    public readonly struct Activation : IDisposable
    {
        private readonly ChatUsageScope? _previous;

        internal Activation(ChatUsageScope scope)
        {
            _previous = _current.Value;
            _current.Value = scope;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _current.Value = _previous;
        }
    }

    #region Public static methods

    /// <summary>Creates a scope; it collects nothing until <see cref="Enter"/> is called.</summary>
    public static ChatUsageScope Create()
    {
        return new ChatUsageScope();
    }

    #endregion
}
