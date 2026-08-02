namespace Enterprise.Gpt.Service
{
    /// <summary>
    /// Provides exclusive, per-conversation locking so that at most one chat turn runs
    /// against a given conversation at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default implementation (<see cref="ConversationLockService"/>) is <b>process-local</b>.
    /// It guarantees mutual exclusion only within a single API instance; running two or more
    /// replicas gives each its own registry and therefore no mutual exclusion at all. This
    /// interface exists as the seam for a distributed lease (for example a Cosmos DB lease
    /// document with a TTL) should the API ever be scaled out.
    /// </para>
    /// <para>
    /// Acquisition is deliberately non-blocking: a caller that cannot take the lock is expected
    /// to fail fast rather than queue behind an arbitrarily long model completion.
    /// </para>
    /// </remarks>
    public interface IConversationLockService
    {
        /// <summary>
        /// Attempts to acquire the exclusive lock for the specified conversation without waiting.
        /// </summary>
        /// <param name="conversationId">The unique identifier of the conversation to lock.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// A task that represents the asynchronous operation. The task result is an
        /// <see cref="IDisposable"/> that releases the lock when disposed, or <see langword="null"/>
        /// if the lock is already held.
        /// </returns>
        /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled via <paramref name="cancellationToken"/>.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the service has already been disposed.</exception>
        /// <example>
        /// <code language="csharp">
        /// using var releaser = await lockService.TryAcquireLockAsync(conversationId, cancellationToken)
        ///     ?? throw new ConversationBusyException($"Conversation {conversationId} is currently being processed.");
        /// </code>
        /// </example>
        ValueTask<IDisposable?> TryAcquireLockAsync(Guid conversationId, CancellationToken cancellationToken = default);
    }
}
