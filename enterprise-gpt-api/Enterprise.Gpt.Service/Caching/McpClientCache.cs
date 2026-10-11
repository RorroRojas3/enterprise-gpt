using System.Collections.Concurrent;
using Enterprise.Gpt.Service.Caching.Models;
using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Caching;

/// <summary>A lease on a cached MCP entry; the client outlives eviction until all leases dispose.</summary>
public interface IMcpToolLease : IAsyncDisposable
{
    IReadOnlyList<AITool> Tools { get; }
}

/// <summary>Per-instance singleton cache of live MCP clients and their tool lists.</summary>
/// <remarks>
/// Single-flight creation, failures never cached, entries expire with their access token, and a client is
/// disposed once it is evicted and unleased.
/// </remarks>
public interface IMcpClientCache : IAsyncDisposable
{
    /// <summary>
    /// Returns a leased entry, creating it via <paramref name="factory"/> on a miss or when the cached entry
    /// has less than the configured remaining lifetime.
    /// </summary>
    /// <returns>A lease the caller must dispose when done using the tools.</returns>
    Task<IMcpToolLease> GetOrCreateAsync(
        McpCacheKey key,
        Func<CancellationToken, Task<McpCacheEntrySource>> factory,
        CancellationToken cancellationToken);

    /// <summary>
    /// Evicts every entry for a server; leased clients are disposed after their last lease releases.
    /// </summary>
    void InvalidateServer(Guid mcpServerId);

    /// <summary>Evicts one user's entry for a server, as <see cref="InvalidateServer"/> does for all.</summary>
    /// <remarks>
    /// A user-supplied credential has no expiry, so without this a replaced key keeps the old connection
    /// until the age cap.
    /// </remarks>
    void InvalidateServerForUser(Guid mcpServerId, Guid userOid);
}

/// <inheritdoc />
/// <remarks>
/// Lazy tasks in a dictionary rather than <c>IMemoryCache</c>/<c>HybridCache</c>, which model neither
/// single-flight creation, refcounted disposal nor per-server invalidation. The key space is tiny.
/// </remarks>
public sealed partial class McpClientCache : IMcpClientCache
{
    // Retries cover benign races only (a co-waiter's creator canceling, an entry evicted
    // between completion and leasing); real factory failures always rethrow immediately.
    private const int MaxAttempts = 5;

    private readonly ConcurrentDictionary<McpCacheKey, Lazy<Task<McpCacheEntry>>> _entries = new();
    private readonly ILogger<McpClientCache> _logger;
    private readonly McpClientCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _sweepCts = new();
    private readonly Task _sweepTask;

    public McpClientCache(ILogger<McpClientCache> logger, IOptions<McpClientCacheOptions> options, TimeProvider timeProvider)
    {
        _logger = logger;
        _options = options.Value;
        _timeProvider = timeProvider;
        _sweepTask = SweepLoopAsync(_sweepCts.Token);
    }

    /// <inheritdoc />
    public async Task<IMcpToolLease> GetOrCreateAsync(
        McpCacheKey key,
        Func<CancellationToken, Task<McpCacheEntrySource>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var newLazy = new Lazy<Task<McpCacheEntry>>(
                () => CreateEntryAsync(factory, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var lazy = _entries.GetOrAdd(key, newLazy);
            var created = ReferenceEquals(lazy, newLazy);

            McpCacheEntry entry;
            try
            {
                // WaitAsync so this caller can stop waiting without cancelling co-waiters.
                entry = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Drop the entry only if creation itself failed; another caller's factory may still complete.
                if (lazy.Value.IsCompleted && !lazy.Value.IsCompletedSuccessfully)
                {
                    RemoveIfCurrent(key, lazy);
                }

                throw;
            }
            catch (OperationCanceledException)
            {
                // The creating caller canceled; retry so this caller runs its own factory.
                RemoveIfCurrent(key, lazy);
                if (attempt >= MaxAttempts)
                {
                    throw;
                }

                continue;
            }
            catch
            {
                // Failures are never cached: drop the faulted entry so the next request
                // runs a fresh factory (the factory disposes any partially created client).
                RemoveIfCurrent(key, lazy);
                throw;
            }

            // Only cached entries are freshness-gated: a new one is always served, or a short-lived token
            // would be evicted and recreated forever.
            if (!created && entry.EffectiveExpiry - _timeProvider.GetUtcNow() < _options.MinRemainingLifetimeForStream)
            {
                if (RemoveIfCurrent(key, lazy))
                {
                    EvictEntry(entry);
                }

                if (attempt >= MaxAttempts)
                {
                    throw new InvalidOperationException("Unable to obtain an MCP client with sufficient remaining token lifetime.");
                }

                continue;
            }

            if (!entry.TryAddLease())
            {
                // Evicted and disposed between completion and leasing — retry fresh.
                RemoveIfCurrent(key, lazy);
                if (attempt >= MaxAttempts)
                {
                    throw new InvalidOperationException("Unable to lease an MCP client; the cache entry kept being evicted.");
                }

                continue;
            }

            // Invalidated during creation: mark it evicted so the client is disposed when this lease releases.
            if (!_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, lazy))
            {
                EvictEntry(entry);
            }

            return new McpToolLease(this, entry);
        }
    }

    /// <inheritdoc />
    public void InvalidateServer(Guid mcpServerId)
    {
        foreach (var kvp in _entries)
        {
            if (kvp.Key.McpServerId != mcpServerId || !_entries.TryRemove(kvp))
            {
                continue;
            }

            // Never force an unstarted factory to run just to dispose its result; callers
            // already holding the removed lazy detect the eviction themselves after leasing.
            if (kvp.Value.IsValueCreated)
            {
                _ = EvictWhenCompletedAsync(kvp.Value);
            }
        }

        LogEvictedServerClients(_logger, mcpServerId);
    }

    /// <inheritdoc />
    public void InvalidateServerForUser(Guid mcpServerId, Guid userOid)
    {
        if (!_entries.TryRemove(McpCacheKey.ForUser(mcpServerId, userOid), out var lazy))
        {
            return;
        }

        if (lazy.IsValueCreated)
        {
            _ = EvictWhenCompletedAsync(lazy);
        }

        LogEvictedUserClient(_logger, mcpServerId, userOid);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _sweepCts.CancelAsync().ConfigureAwait(false);
        await _sweepTask.ConfigureAwait(false);
        _sweepCts.Dispose();

        foreach (var kvp in _entries)
        {
            if (_entries.TryRemove(kvp) && kvp.Value.IsValueCreated)
            {
                await EvictWhenCompletedAsync(kvp.Value).ConfigureAwait(false);
            }
        }
    }

    // Lease transitions are lock-protected, and exactly one path ever disposes the client.
    private sealed class McpCacheEntry(McpCacheEntrySource source, DateTimeOffset effectiveExpiry)
    {
        private readonly object _sync = new();
        private int _leaseCount;
        private bool _evicted;
        private bool _disposed;

        public IAsyncDisposable Client { get; } = source.Client;

        public IReadOnlyList<AITool> Tools { get; } = source.Tools;

        public DateTimeOffset EffectiveExpiry { get; } = effectiveExpiry;

        public bool TryAddLease()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return false;
                }

                _leaseCount++;
                return true;
            }
        }

        // True when the caller must dispose the client now; otherwise the last lease release does.
        public bool MarkEvicted()
        {
            lock (_sync)
            {
                _evicted = true;
                if (_leaseCount == 0 && !_disposed)
                {
                    _disposed = true;
                    return true;
                }

                return false;
            }
        }

        // True when the caller must dispose the client now (evicted, and this was the last lease).
        public bool ReleaseLease()
        {
            lock (_sync)
            {
                _leaseCount--;
                if (_evicted && _leaseCount == 0 && !_disposed)
                {
                    _disposed = true;
                    return true;
                }

                return false;
            }
        }
    }

    private sealed class McpToolLease(McpClientCache cache, McpCacheEntry entry) : IMcpToolLease
    {
        private int _disposed;

        public IReadOnlyList<AITool> Tools => entry.Tools;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await cache.ReleaseLeaseAsync(entry).ConfigureAwait(false);
        }
    }

    #region Private methods

    private async ValueTask ReleaseLeaseAsync(McpCacheEntry entry)
    {
        if (entry.ReleaseLease())
        {
            await DisposeClientAsync(entry).ConfigureAwait(false);
        }
    }

    private async Task<McpCacheEntry> CreateEntryAsync(
        Func<CancellationToken, Task<McpCacheEntrySource>> factory,
        CancellationToken cancellationToken)
    {
        var source = await factory(cancellationToken).ConfigureAwait(false);

        // The entry expires when its token does (minus a safety buffer) and never later
        // than the hard age cap; entries without a token only get the age cap.
        var now = _timeProvider.GetUtcNow();
        var maxLifetimeExpiry = now + _options.MaxEntryLifetime;
        var effectiveExpiry = source.TokenExpiresOn is { } tokenExpiresOn
            ? Min(tokenExpiresOn - _options.TokenExpiryBuffer, maxLifetimeExpiry)
            : maxLifetimeExpiry;

        return new McpCacheEntry(source, effectiveExpiry);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right)
    {
        return left <= right ? left : right;
    }

    private bool RemoveIfCurrent(McpCacheKey key, Lazy<Task<McpCacheEntry>> lazy)
    {
        return _entries.TryRemove(new KeyValuePair<McpCacheKey, Lazy<Task<McpCacheEntry>>>(key, lazy));
    }

    private void EvictEntry(McpCacheEntry entry)
    {
        if (entry.MarkEvicted())
        {
            _ = DisposeClientAsync(entry);
        }
    }

    private async Task EvictWhenCompletedAsync(Lazy<Task<McpCacheEntry>> lazy)
    {
        try
        {
            var entry = await lazy.Value.ConfigureAwait(false);
            EvictEntry(entry);
        }
        catch
        {
            // The factory failed or was canceled; it disposed any partial client itself.
        }
    }

    private async Task DisposeClientAsync(McpCacheEntry entry)
    {
        try
        {
            await entry.Client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogEvictedClientDisposeFailed(_logger, ex);
        }
    }

    private async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.SweepInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SweepExpiredEntriesAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task SweepExpiredEntriesAsync()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var kvp in _entries)
        {
            if (!kvp.Value.IsValueCreated || !kvp.Value.Value.IsCompletedSuccessfully)
            {
                continue;
            }

            var entry = await kvp.Value.Value.ConfigureAwait(false);
            if (entry.EffectiveExpiry <= now && _entries.TryRemove(kvp))
            {
                EvictEntry(entry);
            }
        }
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Evicted cached MCP clients for server {McpServerId}")]
    private static partial void LogEvictedServerClients(ILogger logger, Guid mcpServerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evicted the cached MCP client for server {McpServerId} and user {UserId}")]
    private static partial void LogEvictedUserClient(ILogger logger, Guid mcpServerId, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to dispose an evicted MCP client.")]
    private static partial void LogEvictedClientDisposeFailed(ILogger logger, Exception exception);

    #endregion
}
