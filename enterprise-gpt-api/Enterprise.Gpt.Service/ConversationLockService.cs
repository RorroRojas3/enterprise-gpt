namespace Enterprise.Gpt.Service
{
    /// <summary>
    /// Process-local implementation of <see cref="IConversationLockService"/> backed by a
    /// reference-counted registry of per-conversation semaphores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entries are created on first use and removed the instant their last holder releases,
    /// so the registry stays bounded by the number of <em>concurrently active</em> conversations
    /// rather than by the number of conversations ever seen. Reference counting is used in place
    /// of a background eviction sweep because a sweep cannot observe "no one is using this entry"
    /// atomically with respect to acquisition: an evicted-and-disposed semaphore can be handed to
    /// a caller that fetched it a moment earlier, after which the next caller creates a fresh
    /// entry and two turns run against the same conversation — precisely the outcome this
    /// service exists to prevent.
    /// </para>
    /// <para>
    /// Registered as a singleton; see <see cref="IConversationLockService"/> for the scale-out caveat.
    /// </para>
    /// </remarks>
    public sealed class ConversationLockService : IConversationLockService, IDisposable
    {
        /// <summary>
        /// A single conversation's semaphore together with the number of outstanding
        /// holders and pending acquirers. <see cref="RefCount"/> is guarded by the owning
        /// service's gate, never by the semaphore itself.
        /// </summary>
        private sealed class LockEntry
        {
            /// <summary>
            /// Gets the binary semaphore that serialises turns for the conversation.
            /// </summary>
            public SemaphoreSlim Semaphore { get; } = new(1, 1);

            /// <summary>
            /// Gets or sets the number of callers currently holding or acquiring this entry.
            /// </summary>
            public int RefCount { get; set; }
        }

        /// <summary>
        /// Releases a conversation lock and drops the caller's reference to its registry entry.
        /// </summary>
        private sealed class LockReleaser(ConversationLockService service, Guid conversationId, LockEntry entry) : IDisposable
        {
            private int _disposed;

            /// <summary>
            /// Releases the semaphore and returns the entry to the registry. Safe to call more
            /// than once and from more than one thread; only the first call has any effect.
            /// </summary>
            /// <remarks>
            /// The interlocked guard is load-bearing rather than defensive: a concurrent double
            /// dispose would otherwise call <see cref="SemaphoreSlim.Release()"/> twice, raising
            /// the count to two and admitting a second concurrent turn.
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

        private readonly Dictionary<Guid, LockEntry> _locks = [];
        private readonly Lock _gate = new();
        private bool _disposed;

        /// <summary>
        /// Gets the number of conversations currently holding a registry entry. Exposed for tests
        /// so they can assert that the registry drains rather than leaks.
        /// </summary>
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
                // Covers the not-acquired and cancelled paths alike; the successful path hands the
                // reference on to the releaser, which returns it on disposal.
                if (!acquired)
                {
                    Return(conversationId, entry);
                }
            }

            return acquired ? new LockReleaser(this, conversationId, entry) : null;
        }

        /// <summary>
        /// Takes a reference to the conversation's registry entry, creating it if necessary.
        /// </summary>
        /// <param name="conversationId">The unique identifier of the conversation.</param>
        /// <returns>The entry, with its reference count already incremented on the caller's behalf.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when the service has already been disposed.</exception>
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

        /// <summary>
        /// Drops one reference to the conversation's registry entry, removing and disposing it
        /// once the last reference is gone.
        /// </summary>
        /// <param name="conversationId">The unique identifier of the conversation.</param>
        /// <param name="entry">The entry the caller holds a reference to.</param>
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

            // Reaching zero under the gate means no other caller holds or can obtain this entry:
            // a concurrent Rent for the same id would have had to take the gate first, and would
            // have found the entry still present and incremented it above zero.
            entry.Semaphore.Dispose();
        }

        /// <summary>
        /// Marks the service as disposed and abandons the registry.
        /// </summary>
        /// <remarks>
        /// Every entry in the registry has at least one reference by construction, so there is
        /// nothing here to dispose: entries are left for their own <see cref="LockReleaser"/> to
        /// release and reclaim. Disposing a held semaphore here would fault the release path of a
        /// request still unwinding during host shutdown, and a <see cref="SemaphoreSlim"/> with no
        /// allocated wait handle holds no unmanaged resource worth reclaiming at process exit.
        /// </remarks>
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _locks.Clear();
            }
        }
    }
}
