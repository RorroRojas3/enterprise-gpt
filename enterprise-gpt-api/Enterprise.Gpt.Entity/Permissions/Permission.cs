using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.McpServers;

namespace Enterprise.Gpt.Entity.Permissions;

/// <summary>
/// A grantable permission. Built-in permissions (e.g. Administrator) and custom permissions
/// have no <see cref="McpServerId"/>; MCP-linked permissions are auto-managed by their server.
/// </summary>
public class Permission : BaseModifiedByEntity<Guid>
{
    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    /// <summary>
    /// Whether self-provisioned users get this permission on first sign-in. Safe to expose because
    /// <c>PermissionService.EnsurePermissionIsCustom</c> keeps it off built-in and MCP-managed rows.
    /// </summary>
    public bool IsDefault { get; set; }

    public Guid? McpServerId { get; set; }

    public McpServer? McpServer { get; set; }

    public ICollection<UserPermission> UserPermissions { get; set; } = [];
}
