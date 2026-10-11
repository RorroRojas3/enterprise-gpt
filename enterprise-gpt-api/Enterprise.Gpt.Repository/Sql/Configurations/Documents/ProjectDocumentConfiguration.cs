using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

public sealed class ProjectDocumentConfiguration : BaseDocumentConfiguration<ProjectDocument, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ProjectDocument> builder)
    {
        builder.ToTable(nameof(ProjectDocument), "Core");

        builder.HasMany(x => x.Chunks)
            .WithOne(x => x.ProjectDocument)
            .HasForeignKey(x => x.ProjectDocumentId);

        builder.HasMany(x => x.Sheets)
            .WithOne(x => x.ProjectDocument)
            .HasForeignKey(x => x.ProjectDocumentId);

        // Documents are always listed for one project at a time, filtered to the active ones.
        builder.HasIndex(x => new { x.ProjectId, x.DateDeactivated });
    }
}
