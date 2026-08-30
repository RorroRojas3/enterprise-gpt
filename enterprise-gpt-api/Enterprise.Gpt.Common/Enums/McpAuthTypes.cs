namespace Enterprise.Gpt.Common.Enums
{
    /// <summary>
    /// Specifies how the API authenticates against an MCP server.
    /// </summary>
    public enum McpAuthTypes
    {
        /// <summary>
        /// The MCP server requires no authentication.
        /// </summary>
        None = 1,

        /// <summary>
        /// The MCP server requires a Microsoft Entra ID token acquired on behalf of the signed-in user.
        /// </summary>
        EntraIdOnBehalfOf = 2,

        /// <summary>
        /// The MCP server requires a bearer credential each user issues and supplies themselves,
        /// such as a GitHub personal access token.
        /// </summary>
        /// <remarks>
        /// Stored encrypted per user and returned by no route.
        /// </remarks>
        UserApiKey = 3
    }
}
