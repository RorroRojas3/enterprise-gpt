using Enterprise.Gpt.Common.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// One model call charged to a conversation: which model and provider served it, what it cost,
/// and which MCP servers were attached to it.
/// </summary>
/// <remarks>
/// Append-only. Rows are never updated and never soft-deleted, which is why this derives from
/// <see cref="BaseEntity"/> rather than <see cref="BaseModifiedEntity"/> and why
/// <see cref="BaseEntity.DateDeactivated"/> stays <see langword="null"/> — a usage record that can
/// be edited or hidden is not an audit trail. <see cref="BaseEntity.DateCreated"/> is the moment
/// the call finished and is the timestamp reports group by.
/// </remarks>
[Table(nameof(ConversationUsage), Schema = "Core")]
public class ConversationUsage : BaseEntity
{
    [ForeignKey(nameof(Conversation))]
    public Guid ConversationId { get; set; }

    /// <summary>
    /// The user the call is charged to. Denormalized from <see cref="Conversation"/> so per-user
    /// reporting does not have to join through it.
    /// </summary>
    [ForeignKey(nameof(User))]
    public Guid UserId { get; set; }

    [ForeignKey(nameof(Model))]
    public Guid ModelId { get; set; }

    /// <summary>
    /// The provider that served the call.
    /// </summary>
    /// <remarks>
    /// Recorded here rather than read through <see cref="Model"/> at report time: an administrator
    /// can repoint a catalog row at a different provider, which would silently rewrite the history
    /// of every call it ever served.
    /// </remarks>
    [ForeignKey(nameof(Provider))]
    public Guid ProviderId { get; set; }

    /// <summary>
    /// The deployment that actually billed the call, captured as it stood at the time. Catalog
    /// rows are renamed and repointed; this value must keep meaning what it meant.
    /// </summary>
    [StringLength(512)]
    public string DeploymentName { get; set; } = null!;

    public ConversationUsageKinds Kind { get; set; }

    public ConversationUsageStatuses Status { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>
    /// The call's total token cost.
    /// </summary>
    /// <remarks>
    /// Deliberately an expression-bodied getter with no backing field, which is what keeps EF from
    /// mapping it. Giving it a setter would silently add a column that could disagree with its own
    /// operands.
    /// </remarks>
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>
    /// The identifier of the assistant message this call appended to the Cosmos transcript, or
    /// <see langword="null"/> for a naming call and for a turn that did not complete.
    /// </summary>
    /// <remarks>
    /// Best-effort correlation, not a guarantee: the audit row commits to SQL before the transcript
    /// append is attempted, so a Cosmos write that fails leaves this pointing at a message that was
    /// never written. That case is logged with both identifiers.
    /// </remarks>
    public Guid? AssistantMessageId { get; set; }

    public Conversation Conversation { get; set; } = null!;

    public User User { get; set; } = null!;

    public Model Model { get; set; } = null!;

    public Provider Provider { get; set; } = null!;

    public List<ConversationUsageMcpServer> McpServers { get; set; } = [];
}
