using System.Net;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.McpServers.Exceptions;

/// <summary>
/// Thrown when an MCP server cannot be used: connecting or listing its tools fails, or its
/// on-behalf-of token cannot be acquired; answered with 502.
/// </summary>
/// <remarks>
/// A consent or Conditional Access requirement lands here too: these servers are consented tenant-wide,
/// so a UI-required result is a registration fault, not a state the signed-in user can resolve.
/// </remarks>
/// <param name="serverName">The display name of the server that could not be used.</param>
/// <param name="innerException">The connection, protocol or token failure.</param>
public class McpServerUnavailableException(string serverName, Exception innerException)
    : ProblemException(
        HttpStatusCode.BadGateway, ProblemCodes.McpServerUnavailable, $"MCP server '{serverName}' is unavailable.", innerException)
{
    /// <summary>
    /// Gets the display name of the MCP server that could not be used.
    /// </summary>
    public string ServerName { get; } = serverName;

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> Extensions =>
        new Dictionary<string, object?> { ["serverName"] = ServerName };
}
