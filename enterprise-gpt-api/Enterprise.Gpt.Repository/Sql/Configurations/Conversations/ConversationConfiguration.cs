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

        // Optional for the same reason the project relationship is: a conversation has no model
        // until its first turn runs, and an annotation-only mapping would make the navigation
        // required.
        builder.HasOne(x => x.Model)
            .WithMany()
            .HasForeignKey(x => x.ModelId)
            .IsRequired(false);

        // The index the project cascade reads when it clears ProjectId. Deliberately not extended
        // with DateCreated for US-907's ?projectId= listing: that query is owner-scoped too, so the
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
