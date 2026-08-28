using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Maps <see cref="ProjectDocumentSheet"/> to <c>Core.ProjectDocumentSheet</c>, its column and row
/// relationships, and the unique ordinal index that keeps a document's sheets dense.
/// </summary>
public sealed class ProjectDocumentSheetConfiguration : IEntityTypeConfiguration<ProjectDocumentSheet>
{
    public void Configure(EntityTypeBuilder<ProjectDocumentSheet> builder)
    {
        builder.ToTable(nameof(ProjectDocumentSheet), "Core");

        builder.HasKey(x => x.Id);

        builder.HasMany(x => x.Columns)
            .WithOne(x => x.ProjectDocumentSheet)
            .HasForeignKey(x => x.ProjectDocumentSheetId);

        builder.HasMany(x => x.Rows)
            .WithOne(x => x.ProjectDocumentSheet)
            .HasForeignKey(x => x.ProjectDocumentSheetId);

        // Filtered for the reason the conversation-side index is: a deactivated sheet must not block
        // the next ingestion of the same document.
        builder.HasIndex(x => new { x.ProjectDocumentId, x.SheetIndex })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
