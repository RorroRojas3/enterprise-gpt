using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Maps <see cref="ProjectDocument"/> to <c>Core.ProjectDocument</c>, its owner and chunk
/// relationships, and the index that backs the per-project listing.
/// </summary>
public sealed class ProjectDocumentConfiguration : IEntityTypeConfiguration<ProjectDocument>
{
    public void Configure(EntityTypeBuilder<ProjectDocument> builder)
    {
        builder.ToTable(nameof(ProjectDocument), "Core");

        builder.HasKey(x => x.Id);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder.HasMany(x => x.Chunks)
            .WithOne(x => x.ProjectDocument)
            .HasForeignKey(x => x.ProjectDocumentId);

        // Documents are always listed for one project at a time, filtered to the active ones.
        builder.HasIndex(x => new { x.ProjectId, x.DateDeactivated });
    }
}
