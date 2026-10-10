using FluentValidation;

namespace Enterprise.Gpt.Dto.Actions.McpServers;

/// <summary>
/// The shape a user-supplied MCP credential must have, and the limit the ciphertext column is
/// sized from.
/// </summary>
public static class McpCredentialRules
{
    /// <summary>Shortest credential accepted. Below this it is a typo, not a token.</summary>
    public const int MinLength = 8;

    /// <summary>
    /// Longest credential accepted. Comfortably above a GitHub fine-grained token, and low
    /// enough that the protected payload fits the column it is stored in.
    /// </summary>
    public const int MaxLength = 512;

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
