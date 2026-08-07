using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Service.Settings;
using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace Enterprise.Gpt.Service.Caching
{
    /// <summary>
    /// Cache of each user's active permission grants, keyed by Entra object id (which is also
    /// <c>User.Id</c>, so no join is involved).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entries are written at sign-in and loaded lazily on a miss, exactly once per user under
    /// concurrency. Failures are never cached: a transient database error must not lock a user out
    /// for a whole entry lifetime.
    /// </para>
    /// <para>
    /// Per-instance only. Invalidation reaches the instance that served the mutation and no other,
    /// so every entry also carries an absolute lifetime as the sole convergence mechanism in a
    /// scaled-out deployment. Expiry is evaluated on read — there is no sweep, so entries for users
    /// who never come back are held until the process recycles.
    /// </para>
    /// </remarks>
    public interface IUserPermissionCache
    {
        /// <summary>
        /// Returns the user's active permission ids, running <paramref name="loader"/> on a miss or
        /// when the cached entry has expired. Exactly one loader runs per user under concurrency; a
        /// user with no grants caches as the empty set and is a hit, not a miss.
        /// </summary>
        /// <param name="userId">The user's Entra object id.</param>
        /// <param name="loader">
        /// Reads the user's active grants on a miss. It may be shared with concurrent callers from
        /// other requests, so it must not capture state that cannot outlive the calling request.
        /// </param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>The set of permission ids the user actively holds.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="loader"/> is <see langword="null"/>.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
        /// <exception cref="InvalidOperationException">
        /// The entry kept being replaced or abandoned by other requests and no set could be resolved
        /// within the retry budget.
        /// </exception>
        /// <example>
        /// <code language="csharp">
        /// var granted = await cache.GetOrLoadAsync(
        ///     oid, token => LoadGrantsAsync(services, oid, token), cancellationToken);
        /// if (!granted.Contains(PermissionIds.Administrator)) { /* 403 */ }
        /// </code>
        /// </example>
        Task<IReadOnlySet<Guid>> GetOrLoadAsync(
            Guid userId,
            Func<CancellationToken, Task<IReadOnlyCollection<Guid>>> loader,
            CancellationToken cancellationToken);

        /// <summary>
        /// Reads the current invalidation stamp, for pairing with <see cref="Set"/>.
        /// </summary>
        /// <returns>An opaque stamp that changes whenever any entry is evicted.</returns>
        /// <remarks>
        /// Capture this <em>before</em> reading grants from the database. Without it, a revoke that
        /// commits between the read and the <see cref="Set"/> would be undone by the pre-revoke
        /// snapshot, retaining access for a whole entry lifetime.
        /// </remarks>
        long CaptureInvalidationStamp();

        /// <summary>
        /// Replaces the user's entry with <paramref name="permissionIds"/> and restarts its
        /// lifetime. Used at sign-in, where the grant set has just been read for the profile.
        /// </summary>
        /// <param name="userId">The user's Entra object id.</param>
        /// <param name="stamp">
        /// The value <see cref="CaptureInvalidationStamp"/> returned before the grants were read.
        /// When any eviction has happened since, <paramref name="permissionIds"/> may predate it, so
        /// the write is skipped and any entry already cached for the user is dropped as well. That
        /// only costs the next request a load, so this fails safe.
        /// </param>
        /// <param name="permissionIds">The permission ids the user actively holds; may be empty.</param>
        /// <exception cref="ArgumentNullException"><paramref name="permissionIds"/> is <see langword="null"/>.</exception>
        void Set(Guid userId, long stamp, IEnumerable<Guid> permissionIds);

        /// <summary>
        /// Drops one user's entry, so their next authorized request reloads from the database.
        /// A no-op when nothing is cached for them.
        /// </summary>
        /// <param name="userId">The user whose entry to drop.</param>
        void Invalidate(Guid userId);

        /// <summary>
        /// Drops several users' entries, for the cascades that soft-delete a permission or an MCP
        /// server together with every grant of it.
        /// </summary>
        /// <param name="userIds">The users whose entries to drop; duplicates are harmless.</param>
        void Invalidate(IEnumerable<Guid> userIds);

        /// <summary>
        /// Drops every entry. No production code path calls this: it exists so the integration test
        /// fixture can restore isolation after writing grants straight to the database, bypassing
        /// the services that invalidate. Normal invalidation is per user.
        /// </summary>
        void Clear();
    }

    /// <summary>
    /// Default <see cref="IUserPermissionCache"/>, built on a
    /// <c>ConcurrentDictionary&lt;Guid, Lazy&lt;Task&lt;CacheEntry&gt;&gt;&gt;</c> for the same
    /// reason as <see cref="McpClientCache"/>: single-flight creation and per-key invalidation,
    /// neither of which <c>IMemoryCache</c> models without a lock of its own. The key space is one
    /// entry per signed-in user, so no size management is needed. A single global invalidation stamp
    /// orders <see cref="Set"/> against concurrent evictions, so a sign-in that read its grants
    /// before a revoke committed can never republish them afterwards.
    /// </summary>
    public sealed class UserPermissionCache(
        ILogger<UserPermissionCache> logger,
        IOptions<UserPermissionCacheOptions> options,
        TimeProvider timeProvider) : IUserPermissionCache
    {
        // Retries cover benign races only (the loading caller's request aborting, an entry
        // invalidated between load and read); real loader failures always rethrow immediately.
        private const int MaxAttempts = 5;

        private readonly ConcurrentDictionary<Guid, Lazy<Task<CacheEntry>>> _entries = new();
        private readonly ILogger<UserPermissionCache> _logger = logger;
        private readonly UserPermissionCacheOptions _options = options.Value;
        private readonly TimeProvider _timeProvider = timeProvider;

        // Guards the stamp against the entry write it authorizes, so an eviction cannot land between
        // Set's check and its write. Reads never take it — GetOrLoadAsync stays lock-free.
        private readonly Lock _stampGate = new();
        private long _invalidationStamp;

        /// <inheritdoc />
        public async Task<IReadOnlySet<Guid>> GetOrLoadAsync(
            Guid userId,
            Func<CancellationToken, Task<IReadOnlyCollection<Guid>>> loader,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(loader);

            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var newLazy = new Lazy<Task<CacheEntry>>(
                    () => LoadEntryAsync(loader, cancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                var lazy = _entries.GetOrAdd(userId, newLazy);
                var created = ReferenceEquals(lazy, newLazy);

                CacheEntry entry;
                try
                {
                    // WaitAsync lets this caller stop waiting on its own cancellation without
                    // affecting co-waiters awaiting the same in-flight load.
                    entry = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // A load this caller started is bound to its token and its request scope, so it
                    // can never serve a co-waiter once this request is gone — drop it. A load
                    // someone else started may still complete, so leave it unless it already failed.
                    if (created || (lazy.Value.IsCompleted && !lazy.Value.IsCompletedSuccessfully))
                    {
                        RemoveIfCurrent(userId, lazy);
                    }

                    throw;
                }
                catch when (!created)
                {
                    // The load belonged to another request, whose token may have been signalled or
                    // whose DbContext scope may already be disposed. Retry so this caller runs its
                    // own loader rather than surfacing a failure it did not cause. The retry creates
                    // the entry itself, so a genuine loader fault still throws on the next pass.
                    RemoveIfCurrent(userId, lazy);
                    if (attempt >= MaxAttempts)
                    {
                        throw;
                    }

                    continue;
                }
                catch
                {
                    // This caller's own loader failed. Never cache that: the next request runs a
                    // fresh loader instead of being locked out for a whole entry lifetime.
                    RemoveIfCurrent(userId, lazy);
                    throw;
                }

                // Lazy expiry: with no sweep, a stale entry is noticed only when it is read. The
                // gate applies to previously cached entries only — a freshly loaded entry is always
                // served, so a pathological lifetime cannot produce a reload loop.
                if (!created && entry.ExpiresAt <= _timeProvider.GetUtcNow())
                {
                    RemoveIfCurrent(userId, lazy);
                    if (attempt >= MaxAttempts)
                    {
                        throw new InvalidOperationException(
                            "Unable to load a fresh permission set; the cache entry kept being replaced.");
                    }

                    continue;
                }

                return entry.PermissionIds;
            }
        }

        /// <inheritdoc />
        public long CaptureInvalidationStamp()
        {
            lock (_stampGate)
            {
                return _invalidationStamp;
            }
        }

        /// <inheritdoc />
        public void Set(Guid userId, long stamp, IEnumerable<Guid> permissionIds)
        {
            ArgumentNullException.ThrowIfNull(permissionIds);

            // Built outside the gate; only the check and the publish need to be atomic.
            var entry = CreateEntry(permissionIds);

            lock (_stampGate)
            {
                // The stamp is global rather than per user, so an unrelated eviction can also skip
                // this write. That costs one load on the next request and keeps the check to a
                // single comparison, which is the right trade for an authorization cache.
                if (_invalidationStamp != stamp)
                {
                    // Skipping alone is not the same as reloading: whatever is cached predates this
                    // snapshot, so leaving it would serve something even staler until it expires.
                    _entries.TryRemove(userId, out _);
                    return;
                }

                // Replaces any in-flight load for this user: the caller has just read the grant set
                // from the database, so it is at least as fresh as anything still loading. The
                // orphaned load completes into nothing and is never published.
                _entries[userId] = new Lazy<Task<CacheEntry>>(Task.FromResult(entry));
            }
        }

        /// <inheritdoc />
        public void Invalidate(Guid userId)
        {
            bool evicted;
            lock (_stampGate)
            {
                _invalidationStamp++;
                evicted = _entries.TryRemove(userId, out _);
            }

            if (evicted)
            {
                _logger.LogInformation("Evicted the cached permission set for user {UserId}", userId);
            }
        }

        /// <inheritdoc />
        public void Invalidate(IEnumerable<Guid> userIds)
        {
            ArgumentNullException.ThrowIfNull(userIds);

            // Materialized before the gate: the parameter is an arbitrary sequence and may evaluate
            // lazily, and enumerating caller code under the lock could run I/O on it.
            Guid[] ids = [.. userIds];

            var evicted = 0;
            lock (_stampGate)
            {
                _invalidationStamp++;
                foreach (var userId in ids)
                {
                    if (_entries.TryRemove(userId, out _))
                    {
                        evicted++;
                    }
                }
            }

            if (evicted > 0)
            {
                _logger.LogInformation("Evicted cached permission sets for {UserCount} user(s)", evicted);
            }
        }

        /// <inheritdoc />
        public void Clear()
        {
            lock (_stampGate)
            {
                _invalidationStamp++;
                _entries.Clear();
            }

            _logger.LogInformation("Cleared every cached permission set");
        }

        private async Task<CacheEntry> LoadEntryAsync(
            Func<CancellationToken, Task<IReadOnlyCollection<Guid>>> loader,
            CancellationToken cancellationToken)
        {
            var permissionIds = await loader(cancellationToken).ConfigureAwait(false);

            return CreateEntry(permissionIds);
        }

        // Copied and frozen on the way in: the loader's collection belongs to its caller and may be
        // mutated afterwards, while the cached instance is handed to every concurrent reader.
        private CacheEntry CreateEntry(IEnumerable<Guid> permissionIds)
        {
            return new CacheEntry(
                permissionIds.ToFrozenSet(),
                _timeProvider.GetUtcNow() + _options.EntryLifetime);
        }

        private void RemoveIfCurrent(Guid userId, Lazy<Task<CacheEntry>> lazy)
        {
            _entries.TryRemove(new KeyValuePair<Guid, Lazy<Task<CacheEntry>>>(userId, lazy));
        }

        /// <summary>
        /// One user's grant set and the instant it stops being served. The set is frozen, so readers
        /// need no synchronization and a reader holding a reference always sees a consistent
        /// snapshot even while the entry is being replaced.
        /// </summary>
        private sealed record CacheEntry(FrozenSet<Guid> PermissionIds, DateTimeOffset ExpiresAt);
    }
}
