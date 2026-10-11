using System.Collections.Concurrent;
using System.Collections.Frozen;
using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Caching;

/// <summary>
/// Cache of each user's active permission grants, keyed by Entra object id (= <c>User.Id</c>).
/// </summary>
/// <remarks>
/// Per-instance: invalidation reaches only the instance that served the mutation, so the absolute entry
/// lifetime is the only cross-instance convergence. Failures are never cached; expiry is checked on read.
/// </remarks>
public interface IUserPermissionCache
{
    /// <summary>
    /// Returns the user's active permission ids, running <paramref name="loader"/> once per user on a miss
    /// or expiry. An empty set is a hit.
    /// </summary>
    /// <remarks>
    /// The loader may be shared with other requests' callers, so it must not capture request-scoped state.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="loader"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="InvalidOperationException">
    /// Other requests kept replacing the entry until the retry budget ran out.
    /// </exception>
    Task<IReadOnlySet<Guid>> GetOrLoadAsync(
        Guid userId,
        Func<CancellationToken, Task<IReadOnlyCollection<Guid>>> loader,
        CancellationToken cancellationToken);

    /// <summary>Reads the current invalidation stamp, for pairing with <see cref="Set"/>.</summary>
    /// <returns>An opaque stamp that changes whenever any entry is evicted.</returns>
    /// <remarks>
    /// Capture it <em>before</em> reading grants, or a revoke committing before <see cref="Set"/> would be
    /// undone for a whole entry lifetime.
    /// </remarks>
    long CaptureInvalidationStamp();

    /// <summary>Replaces the user's entry and restarts its lifetime; used at sign-in.</summary>
    /// <param name="userId">The user's Entra object id.</param>
    /// <param name="stamp">
    /// From <see cref="CaptureInvalidationStamp"/> before the grants were read. After any eviction since,
    /// the write is skipped and the cached entry dropped (fails safe).
    /// </param>
    /// <param name="permissionIds">The permission ids the user actively holds; may be empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="permissionIds"/> is <see langword="null"/>.</exception>
    void Set(Guid userId, long stamp, IEnumerable<Guid> permissionIds);

    /// <summary>Drops one user's entry, so their next authorized request reloads from the database.</summary>
    void Invalidate(Guid userId);

    /// <summary>
    /// Drops several users' entries, for cascades that soft-delete a permission or MCP server with its grants.
    /// </summary>
    void Invalidate(IEnumerable<Guid> userIds);

    /// <summary>
    /// Drops every entry. Test-only: restores isolation after a fixture writes grants straight to the database.
    /// </summary>
    void Clear();
}

/// <inheritdoc />
/// <remarks>
/// Lazy tasks in a dictionary, as in <see cref="McpClientCache"/>. A global invalidation stamp orders
/// <see cref="Set"/> against evictions, so grants read before a revoke can never be republished after it.
/// </remarks>
public sealed partial class UserPermissionCache(
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
                // WaitAsync so this caller can stop waiting without cancelling co-waiters.
                entry = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A load this caller started is bound to its request and cannot serve co-waiters, so drop
                // it; someone else's may still complete unless it already failed.
                if (created || (lazy.Value.IsCompleted && !lazy.Value.IsCompletedSuccessfully))
                {
                    RemoveIfCurrent(userId, lazy);
                }

                throw;
            }
            catch when (!created)
            {
                // Another request's load, whose token or DbContext scope may be gone: retry with this
                // caller's own loader, so a genuine fault still throws on the next pass.
                RemoveIfCurrent(userId, lazy);
                if (attempt >= MaxAttempts)
                {
                    throw;
                }

                continue;
            }
            catch
            {
                // Own loader failed: never cache it, or the user is locked out for a whole entry lifetime.
                RemoveIfCurrent(userId, lazy);
                throw;
            }

            // Only cached entries are expiry-gated, so a fresh load is always served and cannot loop.
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
            // Global, not per user: an unrelated eviction can skip this write too, costing one load, in
            // exchange for a single comparison.
            if (_invalidationStamp != stamp)
            {
                // Also drop the cached entry, which predates this snapshot and is staler still.
                _entries.TryRemove(userId, out _);
                return;
            }

            // Replaces any in-flight load, which is no fresher than this read and is never published.
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
            LogEvictedPermissionSet(_logger, userId);
        }
    }

    /// <inheritdoc />
    public void Invalidate(IEnumerable<Guid> userIds)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        // Materialized before the gate, so a lazy sequence cannot run caller code under the lock.
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
            LogEvictedPermissionSets(_logger, evicted);
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

        LogClearedPermissionSets(_logger);
    }

    // Frozen, so readers need no synchronization and always see a consistent snapshot.
    private sealed record CacheEntry(FrozenSet<Guid> PermissionIds, DateTimeOffset ExpiresAt);

    #region Private methods

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

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Evicted the cached permission set for user {UserId}")]
    private static partial void LogEvictedPermissionSet(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evicted cached permission sets for {UserCount} user(s)")]
    private static partial void LogEvictedPermissionSets(ILogger logger, int userCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleared every cached permission set")]
    private static partial void LogClearedPermissionSets(ILogger logger);

    #endregion
}
