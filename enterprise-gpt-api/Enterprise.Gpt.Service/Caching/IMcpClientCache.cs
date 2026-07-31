using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Service.Caching
{
    /// <summary>
    /// Cache key for MCP client entries. On-behalf-of servers are keyed per user because the
    /// client carries a user-scoped token; servers without authentication share one entry.
    /// </summary>
    /// <param name="McpServerId">The MCP server id.</param>
    /// <param name="UserOid">The user's Entra object id, or <see langword="null"/> for a shared entry.</param>
    public readonly record struct McpCacheKey(Guid McpServerId, Guid? UserOid)
    {
        /// <summary>
        /// Creates a per-user key for servers authenticated on behalf of the user.
        /// </summary>
        /// <param name="mcpServerId">The MCP server id.</param>
        /// <param name="userOid">The user's Entra object id.</param>
        /// <returns>The per-user cache key.</returns>
        public static McpCacheKey ForUser(Guid mcpServerId, Guid userOid)
        {
            return new McpCacheKey(mcpServerId, userOid);
        }

        /// <summary>
        /// Creates a key shared across all users for servers that require no authentication.
        /// </summary>
        /// <param name="mcpServerId">The MCP server id.</param>
        /// <returns>The shared cache key.</returns>
        public static McpCacheKey Shared(Guid mcpServerId)
        {
            return new McpCacheKey(mcpServerId, null);
        }
    }

    /// <summary>
    /// What a cache-entry factory produces: a live MCP client, the tools listed from it, and
    /// the token expiry that bounds the entry's usable lifetime.
    /// </summary>
    public sealed class McpCacheEntrySource
    {
        /// <summary>
        /// The connected MCP client. Abstracted as <see cref="IAsyncDisposable"/> so tests can
        /// substitute it; the cache disposes it once evicted and no longer leased.
        /// </summary>
        public required IAsyncDisposable Client { get; init; }

        /// <summary>
        /// The tools listed from <see cref="Client"/>. They are bound to that client and are
        /// only invocable while it is alive.
        /// </summary>
        public required IReadOnlyList<AITool> Tools { get; init; }

        /// <summary>
        /// Expiry of the access token baked into the client's transport;
        /// <see langword="null"/> for servers that require no authentication.
        /// </summary>
        public DateTimeOffset? TokenExpiresOn { get; init; }
    }

    /// <summary>
    /// A lease on a cached MCP entry. Holding a lease keeps the underlying client alive even
    /// if the entry is evicted; disposing releases it (the client is disposed once evicted and
    /// no leases remain).
    /// </summary>
    public interface IMcpToolLease : IAsyncDisposable
    {
        /// <summary>
        /// Gets the tools of the leased entry.
        /// </summary>
        IReadOnlyList<AITool> Tools { get; }
    }

    /// <summary>
    /// Singleton cache of live MCP clients and their tool lists. Entries are created once per
    /// key under concurrency (single-flight), never cached on failure, expire with the access
    /// token that backs them, and are disposed deterministically once evicted and unleased.
    /// Per-instance only: in a scaled-out deployment each instance holds its own connections.
    /// </summary>
    public interface IMcpClientCache : IAsyncDisposable
    {
        /// <summary>
        /// Returns a leased entry for the key, creating it via <paramref name="factory"/> on a
        /// miss or when the cached entry has less than the configured remaining lifetime.
        /// Exactly one factory runs per key under concurrency; failures are never cached.
        /// </summary>
        /// <param name="key">The cache key.</param>
        /// <param name="factory">Creates the client and tool list on a miss.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>A lease the caller must dispose when done using the tools.</returns>
        Task<IMcpToolLease> GetOrCreateAsync(
            McpCacheKey key,
            Func<CancellationToken, Task<McpCacheEntrySource>> factory,
            CancellationToken cancellationToken);

        /// <summary>
        /// Evicts every entry (all users and the shared entry) for the given server. Safe to
        /// call while streams are in flight: leased clients are disposed after their last
        /// lease releases.
        /// </summary>
        /// <param name="mcpServerId">The MCP server whose entries to evict.</param>
        void InvalidateServer(Guid mcpServerId);
    }
}
