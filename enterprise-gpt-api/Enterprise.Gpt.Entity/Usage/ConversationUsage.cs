using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.ChatModels;
using Enterprise.Gpt.Entity.Conversations;
using Enterprise.Gpt.Entity.Users;

namespace Enterprise.Gpt.Entity.Usage;

/// <summary>
/// One model call charged to a conversation: which model and provider served it, what it cost,
/// and which MCP servers were attached to it.
/// </summary>
/// <remarks>
/// Append-only audit row: never updated or soft-deleted. Reports group by
/// <see cref="BaseCreatedEntity{TKey}.DateCreated"/>.
/// </remarks>
public class ConversationUsage : BaseCreatedEntity<Guid>
{
    public Guid ConversationId { get; set; }

    /// <summary>Denormalized from <see cref="Conversation"/> for per-user reporting.</summary>
    public Guid UserId { get; set; }

    public Guid ModelId { get; set; }

    /// <remarks>
    /// Snapshotted rather than read through <see cref="Model"/>, since repointing a catalog row would
    /// otherwise rewrite history.
    /// </remarks>
    public Guid ProviderId { get; set; }

    /// <summary>
    /// The deployment that billed the call, snapshotted because catalog rows are renamed and repointed.
    /// </summary>
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
    /// Rolled up from the top-level <see cref="ToolCalls"/>; zero when no tool ran or reported usage.
    /// Summed from input and output counts, unlike the reported
    /// <see cref="ConversationUsageToolCall.SubtreeTotalTokens"/>, so never join across the two.
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
    /// Cached tokens bill at a discount the catalog cannot price yet, so this flags where
    /// <see cref="EstimatedCost"/> overstates.
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
    /// Getter-only so EF does not map it; a stored column could disagree with its operands.
    /// </remarks>
    public long TotalTokens => AssistantTokens + ToolInputTokens + ToolOutputTokens;

    /// <summary>
    /// What the two messages this turn transcribed weigh, estimated locally, or
    /// <see langword="null"/> when the turn was not transcribed.
    /// </summary>
    /// <remarks>
    /// Null, not zero, for cancelled and failed turns, which are billed but never transcribed. The only
    /// estimated column; it matches the billed ones only on a turn without tool calls.
    /// </remarks>
    public long? ContextTokens { get; set; }

    /// <summary>
    /// What the serving deployment charged per 1,000,000 input tokens when this call ran, or
    /// <see langword="null"/> when the model carried no price.
    /// </summary>
    /// <remarks>
    /// Snapshotted like <see cref="DeploymentName"/>, so a catalog price change never reprices history.
    /// </remarks>
    public decimal? InputPricePerMillionTokens { get; set; }

    /// <summary>
    /// What the serving deployment charged per 1,000,000 output tokens when this call ran, on the
    /// same terms as <see cref="InputPricePerMillionTokens"/>.
    /// </summary>
    public decimal? OutputPricePerMillionTokens { get; set; }

    /// <summary>
    /// What this call cost at the prices captured on it, or <see langword="null"/> when either
    /// price is absent.
    /// </summary>
    /// <remarks>
    /// Unmapped like <see cref="TotalTokens"/>, so it throws in an <see cref="IQueryable{T}"/> projection;
    /// materialize first. Tool tokens are priced at the driving model's rate, and <see cref="ContextTokens"/>
    /// must never enter this sum.
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
    /// Best-effort: the SQL row commits before the transcript append, so a failed Cosmos write leaves
    /// this dangling (logged with both ids).
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
    /// Unfiltered, this loads the whole tree flat; filter on <c>ParentId == null</c> for the top level.
    /// </remarks>
    public List<ConversationUsageToolCall> ToolCalls { get; set; } = [];

    /// <summary>
    /// What the provider reported for each model turn of the call, in the order it reported them.
    /// </summary>
    /// <remarks>
    /// Empty means no breakdown (non-streaming calls, older rows), never that the call had no turns.
    /// </remarks>
    public List<ConversationUsageTurn> Turns { get; set; } = [];
}
