using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Configures the <see cref="MessageFeedback"/> projection table.
/// </summary>
public sealed class MessageFeedbackConfiguration : IEntityTypeConfiguration<MessageFeedback>
{
    public void Configure(EntityTypeBuilder<MessageFeedback> builder)
    {
        builder.ToTable(nameof(MessageFeedback), "Core");

        builder.HasKey(x => x.Id);

        builder.HasOne(x => x.Conversation)
            .WithMany()
            .HasForeignKey(x => x.ConversationId);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder.Property(x => x.Rating).HasConversion<int>();

        // The one-rating-per-message rule, enforced by the database rather than by the upsert that
        // maintains it: the upsert reads before it writes, so two concurrent ratings of the same
        // message would otherwise both insert. It is also the lookup that upsert seeks on.
        builder.HasIndex(x => new { x.ConversationId, x.MessageId }).IsUnique();

        // No reporting index yet, deliberately. The usage table carries three because the queries
        // that read it exist; the reporting stories over this one (US-1301/US-1302) have not been
        // written, and an index shaped for a query nobody has issued is a guess charged on every
        // write. Add it with the query.
    }
}
