using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.ChatModels;
using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Entity.Projects;
using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Entity.Users;

namespace Enterprise.Gpt.Entity.Conversations;

public class Conversation : BaseModifiedEntity<Guid>
{
    public Guid UserId { get; set; }

    /// <summary>
    /// The project this conversation belongs to, or <see langword="null"/> for a standalone
    /// conversation. Cleared rather than cascaded when the project is deactivated, so the
    /// conversation and its own documents survive.
    /// </summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// The model this conversation's most recent turn ran with, or <see langword="null"/> before
    /// its first turn. Denormalized from the newest <see cref="ConversationUsage"/> row so
    /// restoring the model selection does not have to read the audit trail. Set for an abandoned
    /// turn too — it is still the model the user last chose.
    /// </summary>
    public Guid? ModelId { get; set; }

    public string Name { get; set; } = "New Conversation";

    public bool IsFavorite { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>
    /// What this conversation's stored transcript weighs: the sum of every transcribed
    /// message's estimated token cost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A different quantity from <see cref="InputTokens"/>, <see cref="OutputTokens"/> and
    /// <see cref="TotalTokens"/> beside it, which are what the provider billed. This is what the
    /// transcript costs to replay on the next turn, estimated locally and recomputable from the
    /// messages themselves. The naming rule is that <c>Context</c> means estimated and
    /// replayed, and every other token member is provider-billed; the two are never summed.
    /// </para>
    /// <para>
    /// The two are <em>supposed</em> to disagree, and the gap widens with tool use: tool calls,
    /// tool results and reasoning are billed and never transcribed. A report treating a
    /// divergence as a defect is reading the wrong invariant.
    /// </para>
    /// <para>
    /// Not nullable, unlike its counterpart on <see cref="ConversationUsage"/>: a conversation
    /// that has never completed a turn genuinely weighs nothing, whereas a turn that was billed
    /// but never transcribed has no context cost at all.
    /// </para>
    /// </remarks>
    public long ContextTokens { get; set; }

    public User User { get; set; } = null!;

    public Project? Project { get; set; }

    public Model? Model { get; set; }

    public List<ConversationDocument> Documents { get; set; } = [];

    public List<ConversationUsage> Usage { get; set; } = [];
}
