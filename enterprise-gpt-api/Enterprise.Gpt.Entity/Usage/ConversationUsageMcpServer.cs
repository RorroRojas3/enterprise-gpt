using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// An MCP server that was attached to a <see cref="ConversationUsage"/> call — selected by the
/// client and successfully leased, so its tools were available to the model. Not a record of tool
/// invocation: a server can be attached without the model calling any of its tools.
/// </summary>
[Table(nameof(ConversationUsageMcpServer), Schema = "Core")]
public class ConversationUsageMcpServer : BaseEntity
{
    [ForeignKey(nameof(ConversationUsage))]
    public Guid ConversationUsageId { get; set; }

    [ForeignKey(nameof(McpServer))]
    public Guid McpServerId { get; set; }

    /// <summary>
    /// The server's name as it stood when the call ran; servers can be renamed afterwards.
    /// </summary>
    [StringLength(256)]
    public string McpServerName { get; set; } = null!;

    public ConversationUsage ConversationUsage { get; set; } = null!;

    public McpServer McpServer { get; set; } = null!;
}
