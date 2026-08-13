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

    /// <summary>
    /// Input tokens consumed by the main assistant's own model turns. Tokens spent inside tools are
    /// excluded — they are in <see cref="ToolInputTokens"/> and, itemized, in <see cref="ToolCalls"/>.
    /// </summary>
    public long InputTokens { get; set; }

    /// <summary>
    /// Output tokens produced by the main assistant's own model turns, on the same terms as
    /// <see cref="InputTokens"/>.
    /// </summary>
    public long OutputTokens { get; set; }

    /// <summary>
    /// Input tokens consumed by every tool the call invoked, including tools nested inside other
    /// tools.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rolled-up total of the top-level entries in <see cref="ToolCalls"/>, denormalized here so
    /// the assistant/tool split of a call is readable without touching the child table. Zero when
    /// no tool ran, or when the tools that ran surfaced no usage — an MCP server need not report
    /// what it spent.
    /// </para>
    /// <para>
    /// Summed from the providers' input and output counts, whereas
    /// <see cref="ConversationUsageToolCall.SubtreeTotalTokens"/> stores their reported total. A
    /// provider whose total is not the sum of its operands — cached and reasoning tokens are billed
    /// without appearing in either — will make those two disagree, so a report should pick one and
    /// not join across them.
    /// </para>
    /// </remarks>
    public long ToolInputTokens { get; set; }

    /// <summary>
    /// Output tokens produced by every tool the call invoked, on the same terms as
    /// <see cref="ToolInputTokens"/>.
    /// </summary>
    public long ToolOutputTokens { get; set; }

    /// <summary>
    /// The price in USD per one million input tokens that applied when the call was made, or
    /// <see langword="null"/> when the model had no price on file.
    /// </summary>
    /// <remarks>
    /// Snapshotted rather than joined out to <see cref="Model"/> at report time, for the same
    /// reason <see cref="DeploymentName"/> is: the catalog is editable, and a report that read
    /// today's price would silently rewrite what every past call cost.
    /// </remarks>
    [Column(TypeName = "decimal(18, 6)")]
    public decimal? InputPricePerMillionTokens { get; set; }

    /// <summary>
    /// The price in USD per one million output tokens that applied when the call was made, on the
    /// same terms as <see cref="InputPricePerMillionTokens"/>.
    /// </summary>
    [Column(TypeName = "decimal(18, 6)")]
    public decimal? OutputPricePerMillionTokens { get; set; }

    /// <summary>
    /// What the main assistant's own turns cost, excluding tools.
    /// </summary>
    public long AssistantTokens => InputTokens + OutputTokens;

    /// <summary>
    /// The call's total token cost: the assistant's own turns plus everything its tools consumed.
    /// </summary>
    /// <remarks>
    /// Deliberately an expression-bodied getter with no backing field, which is what keeps EF from
    /// mapping it. Giving it a setter would silently add a column that could disagree with its own
    /// operands.
    /// </remarks>
    public long TotalTokens => AssistantTokens + ToolInputTokens + ToolOutputTokens;

    /// <summary>
    /// What the call cost in USD at the prices snapshotted on it, or <see langword="null"/> when
    /// either price is absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unmapped for the same reason as <see cref="TotalTokens"/>: an expression-bodied getter with
    /// no backing field cannot drift from the operands it is derived from, where a column could.
    /// </para>
    /// <para>
    /// Tool tokens are priced at the <em>driving</em> model's rate, which makes this an
    /// approximation by construction. Nothing in a tool call names the model behind it —
    /// <see cref="ConversationUsageToolCall.ModelId"/> is null on every row this application can
    /// write — so there is no rate to attribute them to other than the model that made the call.
    /// </para>
    /// <para>
    /// Null when either price is missing rather than treating the missing side as zero, which
    /// would report a partial figure as a whole one.
    /// </para>
    /// </remarks>
    public decimal? Cost => InputPricePerMillionTokens is not decimal inputPrice
        || OutputPricePerMillionTokens is not decimal outputPrice
            ? null
            : (InputTokens + ToolInputTokens) / 1_000_000m * inputPrice
                + (OutputTokens + ToolOutputTokens) / 1_000_000m * outputPrice;

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

    /// <summary>
    /// The tools the call invoked, as a forest: this collection holds every invocation the model
    /// made directly, and each of those carries its own nested calls.
    /// </summary>
    /// <remarks>
    /// Unfiltered, this navigation loads the whole tree flat, because EF maps the self-relationship
    /// on <see cref="ConversationUsageToolCall"/> as an ordinary parent/child edge. Queries that
    /// want only the top level filter on <c>ParentId == null</c>.
    /// </remarks>
    public List<ConversationUsageToolCall> ToolCalls { get; set; } = [];
}
