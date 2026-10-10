namespace Enterprise.Gpt.Dto
{
    /// <summary>
    /// A grantable permission. MCP-linked permissions carry the owning server's id and are
    /// managed automatically by that server's lifecycle.
    /// </summary>
    public record PermissionDto
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = null!;

        public string? Description { get; init; }

        /// <summary>
        /// Whether the permission is granted automatically to every self-provisioned user.
        /// </summary>
        public bool IsDefault { get; init; }

        public Guid? McpServerId { get; init; }

        public DateTimeOffset? DateDeactivated { get; init; }
    }
}
