using Enterprise.Gpt.Common.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace Enterprise.Gpt.Entity;

/// <summary>
/// A reader's rating of one assistant message, as the relational side records it (US-1102).
/// </summary>
/// <remarks>
/// <para>
/// A projection, not the source of truth. The rating a reader sees comes back from the Cosmos
/// transcript document's own <c>feedback</c> block, which the transcript response projects
/// directly; this table exists so the same facts are queryable — "how many answers were marked
/// unhelpful last week" is a question the transcript container cannot answer, because every read
/// against it is confined to a single conversation's partition by design.
/// </para>
/// <para>
/// The write order follows from that split: the transcript is patched first and this row second,
/// and a failure to write this row is logged rather than thrown. A request that answered 204 must
/// mean the rating survives a reload, and it is the transcript that decides whether it does.
/// </para>
/// <para>
/// One row per message, enforced by a unique index rather than by the upsert that maintains it.
/// Withdrawing a rating leaves the row in place with a null <see cref="Rating"/>: the row is the
/// record that this message was considered, and deleting it would erase that. Withdrawing a rating
/// that was never recorded files no row at all, for the same reason.
/// </para>
/// <para>
/// <see cref="BaseEntity.DateDeactivated"/> comes with the base type and stays null on every row:
/// nothing here is ever soft-deleted, and the upsert deliberately does not filter on it. Were a row
/// ever deactivated, the unique index would refuse the replacement and the upsert would revive the
/// deactivated one instead — so if soft deletion is ever wanted here, the index has to be filtered
/// in the same change.
/// </para>
/// </remarks>
[Table(nameof(MessageFeedback), Schema = "Core")]
public class MessageFeedback : BaseModifiedEntity
{
    /// <summary>
    /// The conversation the rated message belongs to. Half of the natural key, and the reason a
    /// message id alone never has to be globally unique.
    /// </summary>
    [ForeignKey(nameof(Conversation))]
    public Guid ConversationId { get; set; }

    /// <summary>
    /// The reader who rated. Denormalized from <see cref="Conversation"/>, exactly as
    /// <see cref="ConversationUsage.UserId"/> is, so per-user reporting need not join through it.
    /// </summary>
    [ForeignKey(nameof(User))]
    public Guid UserId { get; set; }

    /// <summary>
    /// The assistant message the rating is against.
    /// </summary>
    /// <remarks>
    /// Deliberately not a foreign key: messages live in the Cosmos transcript and there is no
    /// message table to point at. It is the same identifier
    /// <see cref="ConversationUsage.AssistantMessageId"/> correlates on, and the one the
    /// <c>Finished</c> frame carries.
    /// </remarks>
    public Guid MessageId { get; set; }

    /// <summary>
    /// The verdict, or <see langword="null"/> once the reader withdrew it.
    /// </summary>
    public MessageFeedbackRatings? Rating { get; set; }

    public Conversation Conversation { get; set; } = null!;

    public User User { get; set; } = null!;
}
