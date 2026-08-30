namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when an MCP server refuses the credential the user supplied — the token was revoked,
/// expired, or never carried the scopes the server needs. Mapped to HTTP 428 by the global
/// exception handler.
/// </summary>
/// <remarks>
/// Separated from <see cref="McpServerUnavailableException"/> so the reader is told to replace
/// their own key rather than that a third-party service is down. Recognized from the 401 or 403
/// the transport reports, which would otherwise be classified as a connectivity failure.
/// </remarks>
public class McpCredentialRejectedException(Guid mcpServerId, string serverName, Exception innerException)
    : Exception($"MCP server '{serverName}' rejected your API key.", innerException)
{
    /// <summary>Gets the id of the server that refused the credential.</summary>
    public Guid McpServerId { get; } = mcpServerId;

    /// <summary>Gets the display name of that server.</summary>
    public string ServerName { get; } = serverName;
}
