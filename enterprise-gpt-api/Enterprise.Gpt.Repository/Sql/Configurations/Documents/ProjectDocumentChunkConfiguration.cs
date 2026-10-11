using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

public sealed class ProjectDocumentChunkConfiguration : BaseDocumentChunkConfiguration<ProjectDocumentChunk, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ProjectDocumentChunk> builder)
    {
        builder.ToTable(nameof(ProjectDocumentChunk), "Core");

        // Chunk ordinals are dense and unique per document; the filter keeps soft-deleted rows from
        // blocking a re-ingestion of the same document.
        builder.HasIndex(x => new { x.ProjectDocumentId, x.Index })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
