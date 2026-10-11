namespace Enterprise.Gpt.Dto.Actions.McpServers;

/// <summary>
/// The character rule a user-supplied MCP credential must satisfy; its lengths live in
/// <c>UserMcpCredentialLimits</c>.
/// </summary>
public static class McpCredentialRules
{
    #region Public static methods

    /// <summary>
    /// Whether a credential is printable ASCII with no whitespace.
    /// </summary>
    /// <remarks>
    /// Unlike header values, no spaces: one would end the <c>Bearer</c> token, and no issuer puts one in a
    /// token, so it is always a paste that caught surrounding text.
    /// </remarks>
    public static bool IsWellFormed(string? apiKey)
    {
        return !string.IsNullOrEmpty(apiKey)
            && apiKey.All(c => c is > ' ' and <= '~');
    }

    #endregion
}
