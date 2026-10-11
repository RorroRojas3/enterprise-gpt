using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

/// <summary>
/// Maps <see cref="ProjectDocumentSheetColumn"/> to <c>Core.ProjectDocumentSheetColumn</c> and the
/// unique ordinal index that keeps a sheet's columns dense.
/// </summary>
public sealed class ProjectDocumentSheetColumnConfiguration : BaseDocumentSheetColumnConfiguration<ProjectDocumentSheetColumn, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ProjectDocumentSheetColumn> builder)
    {
        builder.ToTable(nameof(ProjectDocumentSheetColumn), "Core");

        builder.HasIndex(x => new { x.ProjectDocumentSheetId, x.ColumnIndex })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
