namespace Enterprise.Gpt.Service.Exceptions
{
    /// <summary>
    /// Thrown when connecting to an MCP server or listing its tools fails (unreachable host,
    /// connection timeout, or a server-side protocol error). Mapped to HTTP 502 by the global
    /// exception handler: the failure is upstream of this API, not in the client's request.
    /// </summary>
    public class McpServerUnavailableException : Exception
    {
        public McpServerUnavailableException(string serverName, Exception innerException)
            : base($"MCP server '{serverName}' is unavailable.", innerException)
        {
            ServerName = serverName;
        }

        /// <summary>
        /// Gets the display name of the MCP server that could not be reached.
        /// </summary>
        public string ServerName { get; }
    }
}
