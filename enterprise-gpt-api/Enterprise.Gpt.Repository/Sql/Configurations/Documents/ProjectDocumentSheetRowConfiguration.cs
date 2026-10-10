using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Maps <see cref="ProjectDocumentSheetRow"/> to <c>Core.ProjectDocumentSheetRow</c>, its native
/// <c>json</c> cells column, and the unique ordinal index that keeps a sheet's rows dense.
/// </summary>
public sealed class ProjectDocumentSheetRowConfiguration : IEntityTypeConfiguration<ProjectDocumentSheetRow>
{
    public void Configure(EntityTypeBuilder<ProjectDocumentSheetRow> builder)
    {
        builder.ToTable(nameof(ProjectDocumentSheetRow), "Core");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Cells)
            .HasColumnType("json")
            .IsRequired();

        builder.HasIndex(x => new { x.ProjectDocumentSheetId, x.RowIndex })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
