using Andes.Extensions.AI;

namespace Enterprise.Gpt.Service.Chat;

/// <summary>
/// Collects the <see cref="ChatUsageReport"/> produced by one tracked chat request, so the code
/// that started the request can read it back after the request ends however it ended.
/// </summary>
/// <remarks>
/// <para>
/// The tracking middleware delivers its report two ways: in-band, as the final update of a streamed
/// response, and out-of-band to every <see cref="IChatProgressObserver"/>. Only the second one is
/// usable here. A turn the user cancels or that faults mid-stream never reaches the in-band update,
/// and those tokens were still billed — so the observer, which fires once per request in every
/// outcome, is the source of truth. <see cref="ChatUsageObserver"/> is the bridge.
/// </para>
/// <para>
/// A scope is inert until <see cref="Enter"/> makes it the observer's target, and stops being the
/// target when that activation is disposed. It is deliberately not a create-once-and-forget ambient:
/// the consumer of this is an <c>async IAsyncEnumerable</c>, and an async iterator resumes on
/// whatever execution context its own consumer supplies, so anything written to an
/// <see cref="AsyncLocal{T}"/> inside one is gone after the first <c>yield return</c>. Wrapping each
/// move of the inner enumerator in an activation is what makes the ambient hold for the window that
/// matters — the window in which the middleware runs.
/// </para>
/// <para>
/// The report is written into the scope object rather than published through the
/// <see cref="AsyncLocal{T}"/>, because the middleware may complete a request from its own pump
/// thread: an assignment made there would not flow back to the flow that created the scope, whereas
/// a write into an object both flows hold does.
/// </para>
/// </remarks>
public sealed class ChatUsageScope
{
    private static readonly AsyncLocal<ChatUsageScope?> _current = new();

    private ChatUsageReport? _report;

    private ChatUsageScope()
    {
    }

    /// <summary>
    /// Gets the scope the current asynchronous flow reports to, or <see langword="null"/> when no
    /// activation is in effect.
    /// </summary>
    internal static ChatUsageScope? Current => _current.Value;

    /// <summary>
    /// Gets the report for the request that ran under this scope, or <see langword="null"/> when
    /// the request produced none — tracking is not installed on the client that served it, or it
    /// failed before the middleware could account for anything.
    /// </summary>
    /// <remarks>
    /// Read after the request has finished, which for a streamed request means after its enumerator
    /// has been disposed: disposal is where the middleware finalizes an abandoned request.
    /// </remarks>
    public ChatUsageReport? Report => Volatile.Read(ref _report);

    /// <summary>
    /// Creates a scope. It collects nothing until <see cref="Enter"/> is called.
    /// </summary>
    /// <returns>The new scope.</returns>
    public static ChatUsageScope Create()
    {
        return new ChatUsageScope();
    }

    /// <summary>
    /// Makes this scope the target for reports raised on the current flow, until the returned
    /// handle is disposed.
    /// </summary>
    /// <returns>A handle that restores the previously active scope when disposed.</returns>
    /// <example>
    /// <code language="csharp">
    /// using (scope.Enter())
    /// {
    ///     moved = await enumerator.MoveNextAsync();
    /// }
    /// </code>
    /// </example>
    public Activation Enter()
    {
        return new Activation(this);
    }

    /// <summary>
    /// Records the report for the completed request.
    /// </summary>
    /// <param name="report">The report the middleware produced, possibly partial.</param>
    /// <remarks>
    /// Last write wins. A single scope should see exactly one completion, but a tool running its
    /// own tracked pipeline would deliver a second, and losing the outer report to a nested one is
    /// a smaller error than throwing inside an observer.
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
}
