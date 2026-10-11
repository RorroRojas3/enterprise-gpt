using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Entity.Users;

namespace Enterprise.Gpt.Entity.Conversations;

/// <summary>
/// A reader's rating of one assistant message, as the relational side records it.
/// </summary>
/// <remarks>
/// A queryable projection: the transcript's <c>feedback</c> block is the source of truth, so it is patched
/// first and a failure writing this row is logged, not thrown. One row per message (unique index); a
/// withdrawn rating keeps its row with a null <see cref="Rating"/>. Never soft-deleted: the filter would
/// hide a row from the upsert while the unfiltered unique index refused its replacement.
/// </remarks>
public class MessageFeedback : BaseModifiedEntity<Guid>
{
    /// <summary>Half of the natural key, so message ids need only be unique per conversation.</summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    /// Denormalized from <see cref="Conversation"/>, like <see cref="ConversationUsage.UserId"/>, for
    /// per-user reporting.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// The assistant message the rating is against.
    /// </summary>
    /// <remarks>
    /// Not a foreign key: messages live in the Cosmos transcript. The id that
    /// <see cref="ConversationUsage.AssistantMessageId"/> and the <c>Finished</c> frame carry.
    /// </remarks>
    public Guid MessageId { get; set; }

    /// <summary>
    /// The verdict, or <see langword="null"/> once the reader withdrew it.
    /// </summary>
    public MessageFeedbackRatings? Rating { get; set; }

    public Conversation Conversation { get; set; } = null!;

    public User User { get; set; } = null!;
}
