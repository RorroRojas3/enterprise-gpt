using Enterprise.Gpt.Common.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// One tool invocation made while serving a <see cref="ConversationUsage"/> call: which tool ran,
/// what it consumed, and — for a tool that invoked tools of its own — the calls nested inside it.
/// </summary>
/// <remarks>
/// <para>
/// Append-only for the same reason as its parent, and derives from <see cref="BaseEntity"/> rather
/// than <see cref="BaseModifiedEntity"/> for the same reason: a usage record that can be edited or
/// hidden is not an audit trail.
/// </para>
/// <para>
/// The token columns split deliberately. The tracking middleware reports each invocation's usage as
/// a <em>rollup</em> — its own consumption plus every descendant's — so persisting that number on
/// every row would double-count a nested agent under any <c>SUM</c>.
/// <see cref="InputTokens"/>/<see cref="OutputTokens"/>/<see cref="TotalTokens"/> therefore carry
/// only what <em>this</em> invocation consumed, which makes an aggregate correct under any filter,
/// and <see cref="SubtreeTotalTokens"/> keeps the rollup so the end-to-end cost of one call is
/// still a single-column read. The invariant is
/// <c>SubtreeTotalTokens = TotalTokens + sum(Children.SubtreeTotalTokens)</c>.
/// </para>
/// </remarks>
[Table(nameof(ConversationUsageToolCall), Schema = "Core")]
public class ConversationUsageToolCall : BaseEntity
{
    /// <summary>
    /// The maximum length of <see cref="ToolName"/>.
    /// </summary>
    /// <remarks>
    /// Public, and used by the annotation below rather than restated in it, because the code that
    /// writes these rows clips to it: that write runs after the answer has already been streamed,
    /// so a tool with an unusually long name must not be able to fail it. A width declared in one
    /// place and clipped to in another drifts, and the drift re-arms exactly that failure.
    /// </remarks>
    public const int ToolNameMaxLength = 256;

    /// <inheritdoc cref="ToolNameMaxLength" />
    public const int SourceMaxLength = 256;

    /// <inheritdoc cref="ToolNameMaxLength" />
    public const int CallIdMaxLength = 128;

    /// <inheritdoc cref="ToolNameMaxLength" />
    public const int DeploymentNameMaxLength = 512;

    [ForeignKey(nameof(ConversationUsage))]
    public Guid ConversationUsageId { get; set; }

    /// <summary>
    /// The invocation this one ran inside, or <see langword="null"/> when the model called it
    /// directly.
    /// </summary>
    [ForeignKey(nameof(Parent))]
    public Guid? ParentId { get; set; }

    /// <summary>
    /// The invocation's position among its siblings, in the order the middleware reported them.
    /// </summary>
    /// <remarks>
    /// Carried explicitly because nothing else orders siblings reliably: identifiers are assigned
    /// by the database and <see cref="BaseEntity.DateCreated"/> is the whole turn's timestamp, not
    /// the invocation's.
    /// </remarks>
    public int Sequence { get; set; }

    /// <summary>
    /// How deeply the invocation is nested; zero for a tool the assistant called directly.
    /// </summary>
    /// <remarks>
    /// Denormalized from the parent chain so rendering a tree, or filtering to top-level calls,
    /// does not need a recursive query.
    /// </remarks>
    public int Depth { get; set; }

    public ConversationToolKinds Kind { get; set; }

    /// <summary>
    /// The tool's name as the model called it.
    /// </summary>
    [StringLength(ToolNameMaxLength)]
    public string ToolName { get; set; } = null!;

    /// <summary>
    /// Where the tool came from — an MCP server's name, or an agent's — as it stood when the call
    /// ran. <see langword="null"/> for a plain function, which has no origin beyond itself.
    /// </summary>
    [StringLength(SourceMaxLength)]
    public string? Source { get; set; }

    /// <summary>
    /// The identifier the model assigned to the function call, or <see langword="null"/> when the
    /// invocation did not come from a model's tool loop.
    /// </summary>
    [StringLength(CallIdMaxLength)]
    public string? CallId { get; set; }

    /// <summary>
    /// The catalog MCP server that served the call, when it could be correlated.
    /// </summary>
    /// <remarks>
    /// Correlation is by the tool-name prefix this application stamps onto every leased MCP tool,
    /// falling back to the reported source name. Left <see langword="null"/> rather than guessed
    /// when neither matches, so a report can distinguish "not an MCP tool" from "we do not know".
    /// </remarks>
    [ForeignKey(nameof(McpServer))]
    public Guid? McpServerId { get; set; }

    /// <summary>
    /// The catalog model the invocation consumed tokens against, when it is knowable.
    /// </summary>
    /// <remarks>
    /// Usually <see langword="null"/>, and honestly so: the middleware reports no model for a tool
    /// call, an MCP server is opaque by construction, and a function that reports usage does not
    /// say what produced it. It is populated only where this application knows the answer itself —
    /// an agent whose catalog model it configured.
    /// </remarks>
    [ForeignKey(nameof(Model))]
    public Guid? ModelId { get; set; }

    /// <summary>
    /// The deployment that billed the invocation, captured as it stood at the time, on the same
    /// terms as <see cref="ConversationUsage.DeploymentName"/>.
    /// </summary>
    [StringLength(DeploymentNameMaxLength)]
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
    /// Stored rather than derived from <see cref="InputTokens"/> and <see cref="OutputTokens"/>
    /// because providers do not always agree that the total is their sum — cached and reasoning
    /// tokens are billed but need not appear in either operand.
    /// </remarks>
    public long? TotalTokens { get; set; }

    /// <summary>
    /// Total tokens for this invocation and everything nested inside it.
    /// </summary>
    public long? SubtreeTotalTokens { get; set; }

    /// <summary>
    /// How long the invocation took, in milliseconds.
    /// </summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// Whether the invocation returned without throwing. A failed call still consumed whatever it
    /// consumed before failing, which is why it is recorded rather than dropped.
    /// </summary>
    public bool Succeeded { get; set; }

    public ConversationUsage ConversationUsage { get; set; } = null!;

    public ConversationUsageToolCall? Parent { get; set; }

    public McpServer? McpServer { get; set; }

    public Model? Model { get; set; }

    public List<ConversationUsageToolCall> Children { get; set; } = [];
}
