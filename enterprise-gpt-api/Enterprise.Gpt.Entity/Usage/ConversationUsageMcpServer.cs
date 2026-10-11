using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.McpServers;

namespace Enterprise.Gpt.Entity.Usage;

/// <summary>
/// An MCP server that was attached to a <see cref="ConversationUsage"/> call — selected by the
/// client and successfully leased, so its tools were available to the model. Not a record of tool
/// invocation: a server can be attached without the model calling any of its tools.
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
