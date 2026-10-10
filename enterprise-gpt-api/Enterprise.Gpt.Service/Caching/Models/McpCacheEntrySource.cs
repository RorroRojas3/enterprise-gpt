using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Service.Caching.Models;

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
