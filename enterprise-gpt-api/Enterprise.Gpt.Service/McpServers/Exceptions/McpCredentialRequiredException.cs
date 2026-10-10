using System.Net;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.McpServers.Exceptions;

/// <summary>
/// Thrown when an MCP server needs a credential the user supplies themselves and none is usable:
/// never saved, since removed, unreadable by the current key ring, or already refused; answered with 428.
/// </summary>
/// <remarks>
/// Distinct from <see cref="McpServerUnavailableException"/> because the client responds oppositely:
/// 502 offers Retry, while this asks the user for a credential no retry can produce.
/// </remarks>
/// <param name="mcpServerId">The id of the server the caller must supply a credential for.</param>
/// <param name="serverName">The display name of that server.</param>
public class McpCredentialRequiredException(Guid mcpServerId, string serverName)
    : ProblemException(
        HttpStatusCode.PreconditionRequired,
        ProblemCodes.McpCredentialRequired,
        $"MCP server '{serverName}' needs an API key you supply before it can be used.")
{
    /// <summary>Gets the id of the server the caller must supply a credential for.</summary>
    public Guid McpServerId { get; } = mcpServerId;

    /// <summary>Gets the display name of that server.</summary>
    public string ServerName { get; } = serverName;

    /// <inheritdoc />
    /// <remarks>The id as well as the name: the client addresses its key dialog by id, and the name is display copy.</remarks>
    public override IReadOnlyDictionary<string, object?> Extensions =>
        new Dictionary<string, object?> { ["serverName"] = ServerName, ["mcpServerId"] = McpServerId };
}
