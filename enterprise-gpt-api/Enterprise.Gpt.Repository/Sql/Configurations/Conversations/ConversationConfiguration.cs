using Enterprise.Gpt.Common.Constants.Conversations;
using Enterprise.Gpt.Entity.Conversations;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Conversations;

public sealed class ConversationConfiguration : BaseModifiedEntityConfiguration<Conversation, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable(nameof(Conversation), "Core");

        builder.Property(x => x.Name).HasMaxLength(ConversationLimits.NameMaxLength);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder.HasOne(x => x.Project)
            .WithMany(x => x.Conversations)
            .HasForeignKey(x => x.ProjectId)
            .IsRequired(false);

        // Optional: a conversation has no model until its first turn runs.
        builder.HasOne(x => x.Model)
            .WithMany()
            .HasForeignKey(x => x.ModelId)
            .IsRequired(false);

        // For the project cascade. No DateCreated: the owner-scoped ?projectId= listing seeks the user
        // index below for its ordering and filters ProjectId as a residual.
        builder.HasIndex(x => new { x.ProjectId, x.DateDeactivated });

        // Sidebar listing, newest first; the descending DateCreated lets it page off the index without a sort.
        builder.HasIndex(x => new { x.UserId, x.DateDeactivated, x.DateCreated })
            .IsDescending(false, false, true);

        // Favourites listing. Separate: IsFavorite before DateCreated would break the unfiltered ordering,
        // and after it could not narrow the seek.
        builder.HasIndex(x => new { x.UserId, x.DateDeactivated, x.IsFavorite, x.DateCreated })
            .IsDescending(false, false, false, true);
    }
}
