using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.McpServers;

namespace Enterprise.Gpt.Entity.Usage;

/// <summary>
/// An MCP server selected and leased for a <see cref="ConversationUsage"/> call. Not a record of tool
/// invocation: the model may have called none of its tools.
/// </summary>
public class ConversationUsageMcpServer : BaseCreatedEntity<Guid>
{
    public Guid ConversationUsageId { get; set; }

    public Guid McpServerId { get; set; }

    /// <summary>
    /// The server's name as it stood when the call ran; servers can be renamed afterwards.
    /// </summary>
    public string McpServerName { get; set; } = null!;

    public ConversationUsage ConversationUsage { get; set; } = null!;

    public McpServer McpServer { get; set; } = null!;
}
