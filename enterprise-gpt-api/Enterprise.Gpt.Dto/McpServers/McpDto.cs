namespace Enterprise.Gpt.Dto.McpServers;

/// <summary>
/// User-facing MCP server for the tools listing and chat selection. Connection details (<c>Url</c>,
/// <c>AuthType</c>, <c>Scope</c>) are deliberately withheld; <see cref="IconKey"/> is presentation only.
/// </summary>
public record McpDto
{
    public Guid Id { get; init; }

    public string Name { get; init; } = null!;

    public string? Description { get; init; }

    public string? IconKey { get; init; }

    /// <summary>
    /// Whether using this server requires a credential the caller supplies themselves.
    /// </summary>
    public bool RequiresUserApiKey { get; init; }

    /// <summary>
    /// Whether the caller holds a usable credential; <see langword="false"/> when none is required or
    /// the server rejected the stored one.
    /// </summary>
    public bool HasUserApiKey { get; init; }

    /// <summary>
    /// Last four characters of the caller's stored credential; <see langword="null"/> when none is held.
    /// </summary>
    public string? ApiKeyHint { get; init; }
}
