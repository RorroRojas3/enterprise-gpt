using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

public sealed class ProjectDocumentSummaryConfiguration : BaseDocumentSummaryConfiguration<ProjectDocumentSummary, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ProjectDocumentSummary> builder)
    {
        builder.ToTable(nameof(ProjectDocumentSummary), "Core");

        builder.HasOne(x => x.ProjectDocument)
            .WithMany()
            .HasForeignKey(x => x.ProjectDocumentId);

        // One live summary per document; filtered so a deactivated summary does not block a later one.
        builder.HasIndex(x => x.ProjectDocumentId)
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
