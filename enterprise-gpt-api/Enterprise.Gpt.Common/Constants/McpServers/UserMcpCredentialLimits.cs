namespace Enterprise.Gpt.Common.Constants.McpServers;

/// <summary>
/// The bounds of a user-supplied MCP credential and the columns that store it.
/// </summary>
public static class UserMcpCredentialLimits
{
    /// <summary>Shortest credential accepted. Below this it is a typo, not a token.</summary>
    public const int ApiKeyMinLength = 8;

    /// <summary>Longest credential accepted; low enough that the protected payload fits <see cref="CiphertextMaxLength"/>.</summary>
    public const int ApiKeyMaxLength = 512;

    public const int CiphertextMaxLength = 2048;

    public const int ApiKeyHintMaxLength = 4;
}
