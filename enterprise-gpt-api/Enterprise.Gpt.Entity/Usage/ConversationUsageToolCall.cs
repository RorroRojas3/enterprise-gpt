using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.ChatModels;
using Enterprise.Gpt.Entity.McpServers;

namespace Enterprise.Gpt.Entity.Usage;

/// <summary>
/// One tool invocation made while serving a <see cref="ConversationUsage"/> call: which tool ran,
/// what it consumed, and — for a tool that invoked tools of its own — the calls nested inside it.
/// </summary>
/// <remarks>
/// Append-only like its parent. The token columns hold this invocation alone, so any <c>SUM</c> is
/// correct; <see cref="SubtreeTotalTokens"/> keeps the middleware's rollup, and
/// <c>SubtreeTotalTokens = TotalTokens + sum(Children.SubtreeTotalTokens)</c>.
/// </remarks>
public class ConversationUsageToolCall : BaseCreatedEntity<Guid>
{
    public Guid ConversationUsageId { get; set; }

    /// <summary>
    /// The invocation this one ran inside, or <see langword="null"/> when the model called it
    /// directly.
    /// </summary>
    public Guid? ParentId { get; set; }

    /// <summary>
    /// The invocation's position among its siblings, in the order the middleware reported them.
    /// </summary>
    /// <remarks>
    /// Explicit because ids are database-assigned and <see cref="BaseCreatedEntity{TKey}.DateCreated"/> is
    /// the whole turn's timestamp.
    /// </remarks>
    public int Sequence { get; set; }

    /// <summary>
    /// How deeply the invocation is nested; zero for a tool the assistant called directly.
    /// </summary>
    /// <remarks>
    /// Denormalized so trees and top-level filters need no recursive query.
    /// </remarks>
    public int Depth { get; set; }

    /// <summary>
    /// The model turn that issued the invocation, joining to
    /// <see cref="ConversationUsageTurn.Iteration"/>.
    /// </summary>
    /// <remarks>
    /// Makes prompt cost attributable, since a tool's arguments and result bill in the next turn's prompt and
    /// serial invocation makes wall-clock order useless. Nested calls carry their root call's iteration, so
    /// attribute only at <see cref="Depth"/> zero. Null on older rows; not backfilled, as zero is a real
    /// iteration.
    /// </remarks>
    public int? Iteration { get; set; }

    public ConversationToolKinds Kind { get; set; }

    /// <summary>
    /// The tool's name as the model called it.
    /// </summary>
    public string ToolName { get; set; } = null!;

    /// <summary>
    /// Where the tool came from — an MCP server's name, or an agent's — as it stood when the call
    /// ran. <see langword="null"/> for a plain function, which has no origin beyond itself.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// The identifier the model assigned to the function call, or <see langword="null"/> when the
    /// invocation did not come from a model's tool loop.
    /// </summary>
    public string? CallId { get; set; }

    /// <summary>
    /// The catalog MCP server that served the call, when it could be correlated.
    /// </summary>
    /// <remarks>
    /// Matched by the tool-name prefix stamped on leased MCP tools, then by source name; left
    /// <see langword="null"/> rather than guessed when neither matches.
    /// </remarks>
    public Guid? McpServerId { get; set; }

    /// <summary>
    /// The catalog model the invocation consumed tokens against, when it is knowable.
    /// </summary>
    /// <remarks>
    /// Usually <see langword="null"/>: tool calls report no model. Set only for an agent whose catalog
    /// model this application configured.
    /// </remarks>
    public Guid? ModelId { get; set; }

    /// <summary>
    /// The deployment that billed the invocation, captured as it stood at the time, on the same
    /// terms as <see cref="ConversationUsage.DeploymentName"/>.
    /// </summary>
    public string? DeploymentName { get; set; }

    /// <summary>
    /// Input tokens consumed by this invocation alone, excluding <see cref="Children"/>.
    /// <see langword="null"/> when no usage was attributed to it.
    /// </summary>
    public long? InputTokens { get; set; }

    /// <summary>
    /// Output tokens produced by this invocation alone, excluding <see cref="Children"/>.
    /// <see langword="null"/> when no usage was attributed to it.
    /// </summary>
    public long? OutputTokens { get; set; }

    /// <summary>
    /// Total tokens for this invocation alone, as reported rather than recomputed.
    /// </summary>
    /// <remarks>
    /// Stored, not derived: cached and reasoning tokens can be billed outside both operands.
    /// </remarks>
    public long? TotalTokens { get; set; }

    /// <summary>
    /// Total tokens for this invocation and everything nested inside it.
    /// </summary>
    public long? SubtreeTotalTokens { get; set; }

    public long DurationMs { get; set; }

    /// <summary>
    /// Whether the invocation returned without throwing. Failed calls are kept: they still consumed tokens.
    /// </summary>
    public bool Succeeded { get; set; }

    public ConversationUsage ConversationUsage { get; set; } = null!;

    public ConversationUsageToolCall? Parent { get; set; }

    public McpServer? McpServer { get; set; }

    public Model? Model { get; set; }

    public List<ConversationUsageToolCall> Children { get; set; } = [];
}
