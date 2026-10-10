namespace Enterprise.Gpt.Dto.McpServers;

/// <summary>
/// What the caller may know about their own credential for one MCP server. Carries neither
/// the credential nor its ciphertext.
/// </summary>
public record McpCredentialStatusDto
{
    public Guid McpServerId { get; init; }

    public bool HasApiKey { get; init; }

    public string? ApiKeyHint { get; init; }

    /// <summary>When the credential was last saved; <see langword="null"/> when none is held.</summary>
    public DateTimeOffset? DateModified { get; init; }
}
