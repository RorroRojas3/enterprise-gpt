using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Maps <see cref="ProjectDocumentSheetColumn"/> to <c>Core.ProjectDocumentSheetColumn</c> and the
/// unique ordinal index that keeps a sheet's columns dense.
/// </summary>
public sealed class ProjectDocumentSheetColumnConfiguration : IEntityTypeConfiguration<ProjectDocumentSheetColumn>
{
    public void Configure(EntityTypeBuilder<ProjectDocumentSheetColumn> builder)
    {
        builder.ToTable(nameof(ProjectDocumentSheetColumn), "Core");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.InferredType)
            .HasConversion<int>();

        builder.HasIndex(x => new { x.ProjectDocumentSheetId, x.ColumnIndex })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
