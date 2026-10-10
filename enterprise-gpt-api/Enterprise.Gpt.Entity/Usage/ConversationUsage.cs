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
    /// Input tokens the assistant's own turns served from the provider's prompt cache, already
    /// counted inside <see cref="InputTokens"/>. <see langword="null"/> when the provider reported
    /// no cache figure, which is not the same as reporting a cache miss.
    /// </summary>
    /// <remarks>
    /// Recorded because cached tokens bill at a discount, so <see cref="EstimatedCost"/> — which
    /// prices every input token at the same rate — overstates a cached call. Closing that gap needs
    /// a cached-input price on the catalog model, which does not exist yet; until it does, this
    /// column is how a report can tell an overstatement is possible.
    /// </remarks>
    public long? CachedInputTokens { get; set; }

    /// <summary>
    /// Reasoning tokens the assistant's own turns produced, already counted inside
    /// <see cref="OutputTokens"/>, on the same null terms as <see cref="CachedInputTokens"/>.
    /// </summary>
    public long? ReasoningTokens { get; set; }

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
    /// What the two messages this turn transcribed weigh, estimated locally, or
    /// <see langword="null"/> when the turn was not transcribed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nullable on purpose, and the distinction matters: a cancelled or failed turn is billed in
    /// SQL and deliberately never transcribed, so it has no context cost at all. Null says "not
    /// transcribed" where zero would say "transcribed nothing" — the same reason the price columns
    /// below are nullable.
    /// </para>
    /// <para>
    /// The only estimated figure on this row. Every other token column is what the provider
    /// reported it billed, and the two are not a reconciliation pair: tool calls, tool results and
    /// reasoning are billed and never transcribed, so the billed columns exceed this one by more
    /// the more tools a turn ran. They agree only on a turn with no tool calls, which is exactly
    /// the condition the estimator's accuracy is measured under.
    /// </para>
    /// </remarks>
    public long? ContextTokens { get; set; }

    /// <summary>
    /// What the serving deployment charged per 1,000,000 input tokens when this call ran, or
    /// <see langword="null"/> when the model carried no price.
    /// </summary>
    /// <remarks>
    /// Snapshotted for the same reason <see cref="DeploymentName"/> is: the catalog is editable, so
    /// joining <c>[Core.Ref].[Model]</c> at report time would price a call at today's rate and
    /// silently rewrite the history of every call the model ever served.
    /// </remarks>
    [Column(TypeName = "decimal(18, 6)")]
    public decimal? InputPricePerMillionTokens { get; set; }

    /// <summary>
    /// What the serving deployment charged per 1,000,000 output tokens when this call ran, on the
    /// same terms as <see cref="InputPricePerMillionTokens"/>.
    /// </summary>
    [Column(TypeName = "decimal(18, 6)")]
    public decimal? OutputPricePerMillionTokens { get; set; }

    /// <summary>
    /// What this call cost at the prices captured on it, or <see langword="null"/> when either
    /// price is absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unmapped, on the same terms as <see cref="TotalTokens"/>: an expression-bodied getter with
    /// no backing field, so no column can drift from its own operands.
    /// </para>
    /// <para>
    /// Null rather than a partial figure when a price is missing. Adding the priced half would
    /// produce a number that looks like a cost and understates it, and there is no way for a reader
    /// to tell the two apart afterwards.
    /// </para>
    /// <para>
    /// Tool tokens are priced at the driving model's rate because nothing in a tool call names the
    /// model behind it — <see cref="ConversationUsageToolCall.ModelId"/> is null on every row this
    /// application can write. Cost is therefore an approximation by construction.
    /// </para>
    /// <para>
    /// <see cref="ContextTokens"/> is deliberately absent from this arithmetic and must stay
    /// absent. Cost is what the provider reported it billed, never what the transcript weighs; the
    /// two are reportable side by side and are not addable.
    /// </para>
    /// <para>
    /// Being unmapped, this cannot appear in a server-side projection: a
    /// <c>Select(x =&gt; x.EstimatedCost)</c> over an <see cref="IQueryable{T}"/> throws, because
    /// there is no column to translate it to. Materialize the row first, or repeat the arithmetic
    /// in the query — the same constraint <see cref="TotalTokens"/> carries.
    /// </para>
    /// </remarks>
    public decimal? EstimatedCost =>
        InputPricePerMillionTokens is { } inputPrice && OutputPricePerMillionTokens is { } outputPrice
            ? (((InputTokens + ToolInputTokens) * inputPrice) + ((OutputTokens + ToolOutputTokens) * outputPrice)) / 1_000_000m
            : null;

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

    /// <summary>
    /// What the provider reported for each model turn of the call, in the order it reported them.
    /// </summary>
    /// <remarks>
    /// Empty for a call that did not stream — providers report one aggregate then, and the
    /// middleware surfaces no breakdown — and for every row written before this table existed.
    /// Empty therefore means "no breakdown available", never "the call had no turns".
    /// </remarks>
    public List<ConversationUsageTurn> Turns { get; set; } = [];
}
