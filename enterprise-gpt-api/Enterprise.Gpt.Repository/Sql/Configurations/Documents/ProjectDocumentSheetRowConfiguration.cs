using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

public sealed class ProjectDocumentSheetRowConfiguration : BaseDocumentSheetRowConfiguration<ProjectDocumentSheetRow, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ProjectDocumentSheetRow> builder)
    {
        builder.ToTable(nameof(ProjectDocumentSheetRow), "Core");

        builder.HasIndex(x => new { x.ProjectDocumentSheetId, x.RowIndex })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
