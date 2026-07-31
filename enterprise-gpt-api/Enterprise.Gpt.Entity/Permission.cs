using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity
{
    /// <summary>
    /// A grantable permission. Built-in permissions (e.g. Administrator) and custom permissions
    /// have no <see cref="McpServerId"/>; MCP-linked permissions are auto-managed by their server.
    /// </summary>
    [Table(nameof(Permission), Schema = "Core")]
    public class Permission : BaseModifiedByEntity
    {
        [StringLength(256)]
        public string Name { get; set; } = null!;

        [StringLength(1024)]
        public string? Description { get; set; }

        [ForeignKey(nameof(McpServer))]
        public Guid? McpServerId { get; set; }

        public McpServer? McpServer { get; set; }

        public ICollection<UserPermission> UserPermissions { get; set; } = [];
    }
}
