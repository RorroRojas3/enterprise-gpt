using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Maps <see cref="ProjectDocumentChunk"/> to <c>Core.ProjectDocumentChunk</c> and the unique
/// ordinal index that keeps a document's chunks dense.
/// </summary>
public sealed class ProjectDocumentChunkConfiguration : IEntityTypeConfiguration<ProjectDocumentChunk>
{
    public void Configure(EntityTypeBuilder<ProjectDocumentChunk> builder)
    {
        builder.ToTable(nameof(ProjectDocumentChunk), "Core");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Text)
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        // Chunk ordinals are dense and unique per document; the filter keeps soft-deleted rows from
        // blocking a re-ingestion of the same document.
        builder.HasIndex(x => new { x.ProjectDocumentId, x.Index })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
