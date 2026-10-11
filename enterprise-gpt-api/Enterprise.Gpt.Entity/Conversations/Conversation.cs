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
    /// <see langword="null"/> for a standalone conversation. Cleared rather than cascaded when the project
    /// is deactivated, so the conversation and its documents survive.
    /// </summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// The model the latest turn (abandoned ones included) ran with, denormalized from the newest
    /// <see cref="ConversationUsage"/> row so restoring the selection skips the audit trail.
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
    /// <c>Context</c> means estimated replay cost; the other token members are provider-billed, never
    /// summed with it, and diverge from it with tool use. Non-nullable, unlike on
    /// <see cref="ConversationUsage"/>: a conversation with no turns genuinely weighs zero.
    /// </remarks>
    public long ContextTokens { get; set; }

    public User User { get; set; } = null!;

    public Project? Project { get; set; }

    public Model? Model { get; set; }

    public List<ConversationDocument> Documents { get; set; } = [];

    public List<ConversationUsage> Usage { get; set; } = [];
}
