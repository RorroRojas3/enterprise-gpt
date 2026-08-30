namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when an MCP server needs a credential the user supplies themselves and none is
/// usable: never saved, since removed, unreadable by the current key ring, or already refused
/// by the server. Mapped to HTTP 428 by the global exception handler.
/// </summary>
/// <remarks>
/// Distinct from <see cref="McpServerUnavailableException"/> because the two need opposite
/// responses from the client: 502 offers Retry, while this asks the user for a credential and
/// no retry can change it.
/// </remarks>
public class McpCredentialRequiredException(Guid mcpServerId, string serverName)
    : Exception($"MCP server '{serverName}' needs an API key you supply before it can be used.")
{
    /// <summary>Gets the id of the server the caller must supply a credential for.</summary>
    public Guid McpServerId { get; } = mcpServerId;

    /// <summary>Gets the display name of that server.</summary>
    public string ServerName { get; } = serverName;
}
