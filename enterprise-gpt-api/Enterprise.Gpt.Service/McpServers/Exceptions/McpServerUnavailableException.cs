namespace Enterprise.Gpt.Service.Exceptions
{
    /// <summary>
    /// Thrown when an MCP server cannot be used: connecting to it or listing its tools fails
    /// (unreachable host, connection timeout, or a server-side protocol error), or its
    /// on-behalf-of token cannot be acquired. The token case includes a consent or Conditional
    /// Access requirement, which is not carried separately because these servers are consented
    /// tenant-wide by an administrator — a UI-required result there is a registration fault, not
    /// a state the signed-in user can resolve. Mapped to HTTP 502 by the global exception
    /// handler: the failure is upstream of this API, not in the client's request.
    /// </summary>
    public class McpServerUnavailableException : Exception
    {
        public McpServerUnavailableException(string serverName, Exception innerException)
            : base($"MCP server '{serverName}' is unavailable.", innerException)
        {
            ServerName = serverName;
        }

        /// <summary>
        /// Gets the display name of the MCP server that could not be used.
        /// </summary>
        public string ServerName { get; }
    }
}
