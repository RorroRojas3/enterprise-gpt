using System.Net;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.McpServers.Exceptions;

/// <summary>
/// Thrown when an MCP server refuses the credential the user supplied: revoked, expired, or missing
/// the scopes the server needs; answered with 428.
/// </summary>
/// <remarks>
/// Recognized from the 401 or 403 the transport reports, which would otherwise read as the
/// connectivity failure <see cref="McpServerUnavailableException"/> describes.
/// </remarks>
/// <param name="mcpServerId">The id of the server that refused the credential.</param>
/// <param name="serverName">The display name of that server.</param>
/// <param name="innerException">The transport failure carrying the refusal.</param>
public class McpCredentialRejectedException(Guid mcpServerId, string serverName, Exception innerException)
    : ProblemException(
        HttpStatusCode.PreconditionRequired,
        ProblemCodes.McpCredentialRejected,
        $"MCP server '{serverName}' rejected your API key.",
        innerException)
{
    /// <summary>Gets the id of the server that refused the credential.</summary>
    public Guid McpServerId { get; } = mcpServerId;

    /// <summary>Gets the display name of that server.</summary>
    public string ServerName { get; } = serverName;

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> Extensions =>
        new Dictionary<string, object?> { ["serverName"] = ServerName, ["mcpServerId"] = McpServerId };
}
