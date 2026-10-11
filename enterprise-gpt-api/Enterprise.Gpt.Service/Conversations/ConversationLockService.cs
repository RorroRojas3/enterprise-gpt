namespace Enterprise.Gpt.Service.Conversations;

/// <summary>
/// Provides exclusive, per-conversation locking so that at most one chat turn runs
/// against a given conversation at a time.
/// </summary>
/// <remarks>
/// <see cref="ConversationLockService"/> is process-local, so replicas get no mutual exclusion; this is
/// the seam for a distributed lease. Acquisition never waits, so a busy caller fails fast.
/// </remarks>
public interface IConversationLockService
{
    /// <summary>Tries to take the conversation's lock without waiting.</summary>
    /// <returns>A handle that releases the lock, or <see langword="null"/> if it is held.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The service has been disposed.</exception>
    ValueTask<IDisposable?> TryAcquireLockAsync(Guid conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Process-local implementation of <see cref="IConversationLockService"/> backed by a
/// reference-counted registry of per-conversation semaphores.
/// </summary>
/// <remarks>
/// Entries go when their last holder releases. Ref counting rather than an eviction sweep, which could
/// dispose a semaphore a caller had just fetched and let two turns run on one conversation.
/// </remarks>
public sealed class ConversationLockService : IConversationLockService, IDisposable
{
    private readonly Dictionary<Guid, LockEntry> _locks = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    // Exposed for tests asserting the registry drains rather than leaks.
    internal int ActiveLockCount
    {
        get
        {
            lock (_gate)
            {
                return _locks.Count;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<IDisposable?> TryAcquireLockAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var entry = Rent(conversationId);

        var acquired = false;
        try
        {
            acquired = await entry.Semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Not-acquired and cancelled paths alike; on success the releaser returns the reference.
            if (!acquired)
            {
                Return(conversationId, entry);
            }
        }

        return acquired ? new LockReleaser(this, conversationId, entry) : null;
    }

    /// <summary>Marks the service as disposed and abandons the registry.</summary>
    /// <remarks>
    /// Held semaphores are left to their <see cref="LockReleaser"/>: disposing them here would fault the
    /// release path of a request still unwinding during shutdown.
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _locks.Clear();
        }
    }

    /// <summary>
    /// A conversation's semaphore; <see cref="RefCount"/> counts holders plus pending acquirers and is
    /// guarded by the service's gate, never by the semaphore.
    /// </summary>
    private sealed class LockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int RefCount { get; set; }
    }

    private sealed class LockReleaser(ConversationLockService service, Guid conversationId, LockEntry entry) : IDisposable
    {
        private int _disposed;

        /// <remarks>
        /// The interlocked guard is load-bearing: a concurrent double dispose would release twice and
        /// admit a second concurrent turn.
        /// </remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            entry.Semaphore.Release();
            service.Return(conversationId, entry);
        }
    }

    #region Private methods

    /// <summary>Takes a reference to the conversation's entry, creating it if needed.</summary>
    private LockEntry Rent(Guid conversationId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_locks.TryGetValue(conversationId, out var entry))
            {
                entry = new LockEntry();
                _locks[conversationId] = entry;
            }

            entry.RefCount++;

            return entry;
        }
    }

    /// <summary>Drops one reference, removing and disposing the entry with the last one.</summary>
    private void Return(Guid conversationId, LockEntry entry)
    {
        lock (_gate)
        {
            if (--entry.RefCount > 0)
            {
                return;
            }

            _locks.Remove(conversationId);
        }

        // Zero under the gate means no one holds or can obtain this entry: a concurrent Rent takes
        // the gate first and would have incremented it.
        entry.Semaphore.Dispose();
    }

    #endregion
}
