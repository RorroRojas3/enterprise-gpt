using Enterprise.Gpt.Common.Constants.Conversations;
using Enterprise.Gpt.Entity.Conversations;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Conversations;

/// <summary>
/// Maps <see cref="Conversation"/> to <c>Core.Conversation</c>, its owner, its optional project and model,
/// and the indexes that back the sidebar listings.
/// </summary>
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

        // The index the project cascade reads when it clears ProjectId. Deliberately not extended
        // with DateCreated for the ?projectId= listing: that query is owner-scoped too, so the
        // engine seeks the user index below and gets the ordering off it for free, leaving ProjectId
        // as a residual predicate over one user's conversations — a bounded set. Folding DateCreated
        // in here would buy a second ordered path for a listing that already has one.
        builder.HasIndex(x => new { x.ProjectId, x.DateDeactivated });

        // The sidebar listing: always the owner's active conversations, newest first, paged. The
        // trailing descending DateCreated is what lets the engine page straight off the index
        // instead of sorting the user's whole history on every request.
        builder.HasIndex(x => new { x.UserId, x.DateDeactivated, x.DateCreated })
            .IsDescending(false, false, true);

        // The favourites-only variant of the same listing. A separate index rather than folding
        // IsFavorite into the one above: placed before DateCreated it would break the ordering for
        // the unfiltered listing, and placed after it, it could not narrow the seek.
        builder.HasIndex(x => new { x.UserId, x.DateDeactivated, x.IsFavorite, x.DateCreated })
            .IsDescending(false, false, false, true);
    }
}
