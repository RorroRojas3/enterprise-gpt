using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Service.Settings;
using System.Collections.Concurrent;

namespace Enterprise.Gpt.Service.Caching
{
    /// <summary>
    /// Default <see cref="IMcpClientCache"/> built on a
    /// <c>ConcurrentDictionary&lt;McpCacheKey, Lazy&lt;Task&lt;McpCacheEntry&gt;&gt;&gt;</c> rather than
    /// <c>IMemoryCache</c>/<c>HybridCache</c>: entries hold live, non-serializable MCP clients
    /// that need single-flight creation, deterministic refcounted disposal, and per-server
    /// invalidation — none of which the general-purpose caches model. The key space is tiny
    /// (users × servers), so no size management is needed. A background sweep disposes
    /// expired, unleased entries; the container disposes the cache itself on shutdown.
    /// </summary>
    public sealed class McpClientCache : IMcpClientCache
    {
        // Retries cover benign races only (a co-waiter's creator canceling, an entry evicted
        // between completion and leasing); real factory failures always rethrow immediately.
        private const int MaxAttempts = 5;

        private readonly ConcurrentDictionary<McpCacheKey, Lazy<Task<McpCacheEntry>>> _entries = new();
        private readonly ILogger<McpClientCache> _logger;
        private readonly McpClientCacheOptions _options;
        private readonly CancellationTokenSource _sweepCts = new();
        private readonly Task _sweepTask;

        public McpClientCache(ILogger<McpClientCache> logger, IOptions<McpClientCacheOptions> options)
        {
            _logger = logger;
            _options = options.Value;
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
                    // WaitAsync lets this caller stop waiting on its own cancellation without
                    // affecting co-waiters awaiting the same in-flight creation.
                    entry = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Only drop the entry when the underlying creation actually failed. When
                    // this caller was merely waiting (WaitAsync) on someone else's in-flight
                    // factory, that factory may still complete and serve future requests.
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

                // The freshness gate applies to previously cached entries only. A freshly
                // created entry is always served — even when the identity provider returned a
                // short-lived token — otherwise it would be evicted and recreated forever.
                if (!created && entry.EffectiveExpiry - DateTimeOffset.UtcNow < _options.MinRemainingLifetimeForStream)
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

                // If the server was invalidated while this entry was being created, the entry
                // is no longer in the dictionary. Mark it evicted so the client is disposed
                // once this (last) lease releases instead of leaking.
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

            _logger.LogInformation("Evicted cached MCP clients for server {McpServerId}", mcpServerId);
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

        /// <summary>
        /// Releases one lease; called by <see cref="McpToolLease.DisposeAsync"/>. Disposes the
        /// client when the entry is evicted and this was the last lease.
        /// </summary>
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
            var now = DateTimeOffset.UtcNow;
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
                _logger.LogWarning(ex, "Failed to dispose an evicted MCP client.");
            }
        }

        private async Task SweepLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var timer = new PeriodicTimer(_options.SweepInterval);
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
            var now = DateTimeOffset.UtcNow;
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

        /// <summary>
        /// A cached client with its tools, expiry, and lease state. Lease transitions are
        /// small lock-protected critical sections; exactly one path ever disposes the client.
        /// </summary>
        private sealed class McpCacheEntry
        {
            private readonly object _sync = new();
            private int _leaseCount;
            private bool _evicted;
            private bool _disposed;

            public McpCacheEntry(McpCacheEntrySource source, DateTimeOffset effectiveExpiry)
            {
                Client = source.Client;
                Tools = source.Tools;
                EffectiveExpiry = effectiveExpiry;
            }

            public IAsyncDisposable Client { get; }

            public IReadOnlyList<AITool> Tools { get; }

            public DateTimeOffset EffectiveExpiry { get; }

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

            /// <summary>
            /// Marks the entry evicted. Returns <see langword="true"/> when the caller must
            /// dispose the client now (no leases outstanding); otherwise disposal happens when
            /// the last lease releases.
            /// </summary>
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

            /// <summary>
            /// Releases one lease. Returns <see langword="true"/> when the caller must dispose
            /// the client now (entry evicted and this was the last lease).
            /// </summary>
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
    }
}
