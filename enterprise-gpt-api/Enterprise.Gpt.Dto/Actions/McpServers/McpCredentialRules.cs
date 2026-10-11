namespace Enterprise.Gpt.Dto.Actions.McpServers;

/// <summary>
/// The character rule a user-supplied MCP credential must satisfy; its lengths live in
/// <c>UserMcpCredentialLimits</c>.
/// </summary>
public static class McpCredentialRules
{
    /// <summary>
    /// Whether a credential is printable ASCII with no whitespace.
    /// </summary>
    /// <param name="apiKey">The credential to test.</param>
    /// <returns><see langword="true"/> when it can be sent as a bearer token.</returns>
    /// <remarks>
    /// Stricter than <c>McpServerHeaderRules.IsWellFormedValue</c> in the one way that matters
    /// here: a space would terminate the <c>Bearer</c> token on the wire, and no issuer puts
    /// one inside a token, so it is always a paste that caught surrounding text.
    /// </remarks>
    public static bool IsWellFormed(string? apiKey)
    {
        return !string.IsNullOrEmpty(apiKey)
            && apiKey.All(c => c is > ' ' and <= '~');
    }
}
