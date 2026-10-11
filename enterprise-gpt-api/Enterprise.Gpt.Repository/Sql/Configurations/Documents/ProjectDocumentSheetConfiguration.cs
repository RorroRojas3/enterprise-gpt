using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

public sealed class ProjectDocumentSheetConfiguration : BaseDocumentSheetConfiguration<ProjectDocumentSheet, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ProjectDocumentSheet> builder)
    {
        builder.ToTable(nameof(ProjectDocumentSheet), "Core");

        builder.HasMany(x => x.Columns)
            .WithOne(x => x.ProjectDocumentSheet)
            .HasForeignKey(x => x.ProjectDocumentSheetId);

        builder.HasMany(x => x.Rows)
            .WithOne(x => x.ProjectDocumentSheet)
            .HasForeignKey(x => x.ProjectDocumentSheetId);

        // Filtered so a soft-deleted sheet cannot block re-ingesting the same document.
        builder.HasIndex(x => new { x.ProjectDocumentId, x.SheetIndex })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
