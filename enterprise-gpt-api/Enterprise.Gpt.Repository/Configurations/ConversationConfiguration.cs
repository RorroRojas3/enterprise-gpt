using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Configures the optional <see cref="Conversation"/> to <see cref="Project"/> relationship.
/// </summary>
/// <remarks>
/// The rest of the conversation mapping still comes from data annotations on the entity; this type
/// exists because an optional foreign key and its index cannot be expressed there without making the
/// project navigation required.
/// </remarks>
public sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable(nameof(Conversation), "Core");

        builder.HasKey(x => x.Id);

        builder.HasOne(x => x.Project)
            .WithMany(x => x.Conversations)
            .HasForeignKey(x => x.ProjectId)
            .IsRequired(false);

        // Conversations are listed for one project at a time, filtered to the active ones. Also the
        // index the project cascade reads when it clears ProjectId.
        builder.HasIndex(x => new { x.ProjectId, x.DateDeactivated });
    }
}
