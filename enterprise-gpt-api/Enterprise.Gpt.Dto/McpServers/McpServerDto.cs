using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Dto.McpServers;

/// <summary>
/// Administrative representation of an MCP server, including connection and permission details.
/// </summary>
public record McpServerDto
{
    public Guid Id { get; init; }

    public string Name { get; init; } = null!;

    public string Description { get; init; } = null!;

    public string Url { get; init; } = null!;

    public McpAuthTypes AuthType { get; init; }

    public string? Scope { get; init; }

    public string? IconKey { get; init; }

    /// <summary>
    /// Extra request headers sent on every call; <see langword="null"/> for none. Administrative only,
    /// withheld from <see cref="McpDto"/> like <see cref="Url"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public Guid? PermissionId { get; init; }

    public DateTimeOffset? DateDeactivated { get; init; }
}
