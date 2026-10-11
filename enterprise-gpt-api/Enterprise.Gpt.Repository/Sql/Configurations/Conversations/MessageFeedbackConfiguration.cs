using Enterprise.Gpt.Entity.Conversations;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Conversations;

/// <summary>
/// Configures the <see cref="MessageFeedback"/> projection table.
/// </summary>
public sealed class MessageFeedbackConfiguration : BaseModifiedEntityConfiguration<MessageFeedback, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<MessageFeedback> builder)
    {
        builder.ToTable(nameof(MessageFeedback), "Core");

        builder.HasOne(x => x.Conversation)
            .WithMany()
            .HasForeignKey(x => x.ConversationId);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder.Property(x => x.Rating).HasConversion<int>();

        // One rating per message, enforced here because the upsert reads before it writes and two
        // concurrent ratings would both insert. Also the upsert's lookup.
        builder.HasIndex(x => new { x.ConversationId, x.MessageId }).IsUnique();

        // No reporting index until a reporting query exists; a speculative one is charged on every write.
    }
}
