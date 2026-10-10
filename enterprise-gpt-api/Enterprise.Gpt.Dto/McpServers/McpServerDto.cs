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

        public string? IconKey { get; init; }

        /// <summary>
        /// Extra request headers sent on every call to this server; <see langword="null"/> for
        /// none. Administrative only — it is absent from <see cref="McpDto"/> for the same reason
        /// <see cref="Url"/> and <see cref="Scope"/> are.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Headers { get; init; }

        public Guid? PermissionId { get; init; }

        public DateTimeOffset? DateDeactivated { get; init; }
    }
}
