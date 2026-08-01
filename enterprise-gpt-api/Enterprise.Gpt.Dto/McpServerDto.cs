using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Dto
{
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

        public Guid? PermissionId { get; init; }

        public DateTimeOffset? DateDeactivated { get; init; }
    }
}
