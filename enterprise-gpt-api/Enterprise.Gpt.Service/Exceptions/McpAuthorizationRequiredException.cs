namespace Enterprise.Gpt.Service.Exceptions
{
    /// <summary>
    /// Thrown when acquiring an on-behalf-of token for an MCP server requires user
    /// interaction (consent or a Conditional Access challenge) that a background API call
    /// cannot perform. Mapped to HTTP 403 by the global exception handler — deliberately not
    /// 401, which would send clients into token-refresh loops that cannot fix a consent
    /// problem.
    /// </summary>
    public class McpAuthorizationRequiredException(string serverName, Exception innerException) : Exception($"Consent or additional authentication is required for MCP server '{serverName}'.", innerException)
    {


        /// <summary>
        /// Gets the display name of the MCP server the token acquisition failed for.
        /// </summary>
        public string ServerName { get; } = serverName;
    }
}
