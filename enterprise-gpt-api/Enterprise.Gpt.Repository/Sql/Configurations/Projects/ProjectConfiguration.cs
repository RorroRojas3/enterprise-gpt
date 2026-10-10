using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Maps <see cref="Project"/> to <c>Core.Project</c>, its ownership and document relationships, and
/// the indexes that back the per-user listing and the one-active-project-per-name rule.
/// </summary>
public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable(nameof(Project), "Core");

        builder.HasKey(x => x.Id);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder.HasMany(x => x.Documents)
            .WithOne(x => x.Project)
            .HasForeignKey(x => x.ProjectId);

        // Projects are always listed for one user at a time, filtered to the active ones.
        builder.HasIndex(x => new { x.UserId, x.DateDeactivated });

        // A user cannot hold two active projects of the same name; the filter keeps a deleted
        // project from blocking a new one that reuses its name.
        builder.HasIndex(x => new { x.UserId, x.Name })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();

        // No index for the ?isFavorite= filter, deliberately. ConversationConfiguration carries one
        // only because its unfiltered listing already seeks an ordered
        // (UserId, DateDeactivated, DateCreated DESC) index that the favourites variant has to stay
        // in parity with. The index above carries no DateCreated, so the unfiltered listing already
        // sorts after its seek — and a favourites index would buy the filtered listing an ordered
        // path the unfiltered one does not have, paid for on every project write. IsFavorite stays
        // a residual predicate over one user's projects, which is a bounded set.
    }
}
