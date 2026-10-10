using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Service.Caching.Models;

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
