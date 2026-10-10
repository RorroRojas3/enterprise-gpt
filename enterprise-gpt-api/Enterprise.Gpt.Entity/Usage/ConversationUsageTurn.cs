using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// One model turn within a <see cref="ConversationUsage"/> call: what the provider reported for a
/// single iteration of the function-invocation loop.
/// </summary>
/// <remarks>
/// <para>
/// Append-only, and derives from <see cref="BaseEntity"/>, for the same reason as its parent and
/// siblings: a usage record that can be edited or hidden is not an audit trail.
/// </para>
/// <para>
/// These rows exist so per-tool <em>prompt</em> cost is answerable. A tool consumes no tokens of its
/// own — an MCP tool is a round trip to another process — but its arguments and its result enter the
/// <em>next</em> turn's prompt and are billed there, inside one aggregate the provider never
/// itemizes. Storing each turn as reported turns that into arithmetic the reader can do:
/// <c>input(N+1) − input(N) − output(N)</c> is what the calls issued by turn <c>N</c> cost, and
/// <see cref="ConversationUsageToolCall.Iteration"/> is what says which calls those were.
/// </para>
/// <para>
/// The subtraction is an attribution model rather than an invoice, which is why the numbers stored
/// here are the reported ones and the derivation lives in the queries that need it. Two corrections
/// belong to the reader: <see cref="ReasoningTokens"/> is billed inside
/// <see cref="OutputTokens"/> but is not replayed into the next prompt, so it must come out of
/// <c>output(N)</c>; and <see cref="CachedInputTokens"/> is billed inside <see cref="InputTokens"/>
/// at a discount, which changes what a token costs rather than how many there were.
/// </para>
/// <para>
/// Rows exist only for a streamed request. Providers report a single aggregate for a non-streaming
/// call, so the middleware surfaces no per-turn breakdown and a call that never streamed carries no
/// turns at all — which is not the same as a turn that reported nothing.
/// </para>
/// </remarks>
[Table(nameof(ConversationUsageTurn), Schema = "Core")]
public class ConversationUsageTurn : BaseEntity
{
    /// <summary>
    /// The maximum length of <see cref="ResponseId"/>.
    /// </summary>
    /// <remarks>
    /// Public, and clipped to by the code that writes these rows, for the same reason the widths on
    /// <see cref="ConversationUsageToolCall"/> are: the write runs after the answer has already been
    /// streamed, so a provider returning an unusually long identifier must not be able to fail the
    /// whole turn's accounting.
    /// </remarks>
    public const int ResponseIdMaxLength = 128;

    /// <summary>
    /// The maximum length of <see cref="ReportedModelId"/>. Clipped to for the same reason as
    /// <see cref="ResponseIdMaxLength"/>.
    /// </summary>
    public const int ReportedModelIdMaxLength = 512;

    [ForeignKey(nameof(ConversationUsage))]
    public Guid ConversationUsageId { get; set; }

    /// <summary>
    /// The zero-based model iteration of the function-invocation loop this row reports.
    /// </summary>
    /// <remarks>
    /// Joins to <see cref="ConversationUsageToolCall.Iteration"/>, which names the turn that issued
    /// each tool call.
    /// </remarks>
    public int Iteration { get; set; }

    /// <summary>
    /// The provider's response identifier for this turn, when it reported one. Carried for support
    /// correlation only; nothing joins on it.
    /// </summary>
    [StringLength(ResponseIdMaxLength)]
    public string? ResponseId { get; set; }

    /// <summary>
    /// The model identifier the provider reported for this turn, when it reported one.
    /// </summary>
    /// <remarks>
    /// Named for what it is rather than <c>ModelId</c>, which everywhere else in this schema is a
    /// catalog <see cref="Guid"/> foreign key. This is the provider's own string, and it is the one
    /// place in the trail where a model actually names itself — <see cref="ConversationUsage"/>
    /// records the catalog row this application chose, which is not the same claim.
    /// </remarks>
    [StringLength(ReportedModelIdMaxLength)]
    public string? ReportedModelId { get; set; }

    /// <summary>
    /// Input tokens the provider reported for this turn, or <see langword="null"/> when it reported
    /// none. Null is not zero: it says nothing was reported.
    /// </summary>
    public long? InputTokens { get; set; }

    /// <summary>
    /// Output tokens the provider reported for this turn, on the same terms as
    /// <see cref="InputTokens"/>.
    /// </summary>
    public long? OutputTokens { get; set; }

    /// <summary>
    /// The turn's total, as reported rather than recomputed.
    /// </summary>
    /// <remarks>
    /// Stored for the same reason <see cref="ConversationUsageToolCall.TotalTokens"/> is: providers
    /// do not always agree that the total is the sum of its operands.
    /// </remarks>
    public long? TotalTokens { get; set; }

    /// <summary>
    /// Input tokens served from the provider's prompt cache, already counted inside
    /// <see cref="InputTokens"/>.
    /// </summary>
    /// <remarks>
    /// Recorded because cached tokens bill at a discount, so a cost derived from
    /// <see cref="InputTokens"/> alone overstates what was charged. It is not currently priced — see
    /// <see cref="ConversationUsage.EstimatedCost"/>.
    /// </remarks>
    public long? CachedInputTokens { get; set; }

    /// <summary>
    /// Reasoning tokens the model produced internally, already counted inside
    /// <see cref="OutputTokens"/>.
    /// </summary>
    /// <remarks>
    /// Recorded because they are billed but are generally not carried into the next turn's prompt,
    /// so any attribution that subtracts a turn's output from the next turn's input has to subtract
    /// these back out or it understates what the turn's tools cost.
    /// <para>
    /// "Generally" is load-bearing, and the exception is why <see cref="ConversationUsage.ProviderId"/>
    /// matters to a report built on this: Anthropic requires thinking blocks to be sent back with a
    /// tool result, so on an Anthropic tool-use turn the reasoning tokens <em>are</em> billed again
    /// in the next prompt and subtracting them over-attributes the turn's tools. The correction
    /// holds on providers that discard reasoning between turns, which is the rest of the catalog.
    /// </para>
    /// </remarks>
    public long? ReasoningTokens { get; set; }

    public ConversationUsage ConversationUsage { get; set; } = null!;
}
