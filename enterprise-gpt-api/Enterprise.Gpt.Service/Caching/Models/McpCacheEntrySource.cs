using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Service.Caching.Models;

/// <summary>
/// What a cache-entry factory produces: a live MCP client, the tools listed from it, and
/// the token expiry that bounds the entry's usable lifetime.
/// </summary>
public sealed class McpCacheEntrySource
{
    /// <summary>The connected MCP client; <see cref="IAsyncDisposable"/> so tests can substitute it.</summary>
    public required IAsyncDisposable Client { get; init; }

    /// <summary>The tools listed from <see cref="Client"/>, invocable only while it is alive.</summary>
    public required IReadOnlyList<AITool> Tools { get; init; }

    /// <summary>
    /// Expiry of the access token baked into the client's transport;
    /// <see langword="null"/> for servers that require no authentication.
    /// </summary>
    public DateTimeOffset? TokenExpiresOn { get; init; }
}
