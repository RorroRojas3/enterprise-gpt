using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations
{
    public sealed class ConversationDocumentChunkConfiguration : IEntityTypeConfiguration<ConversationDocumentChunk>
    {
        public void Configure(EntityTypeBuilder<ConversationDocumentChunk> builder)
        {
            builder.ToTable(nameof(ConversationDocumentChunk), "Core");

            builder.HasKey(x => x.Id);

            builder.Property(x => x.Text)
                .HasColumnType("nvarchar(max)")
                .IsRequired();

            // Chunk ordinals are dense and unique per document; the filter keeps soft-deleted rows from
            // blocking a re-ingestion of the same document.
            builder.HasIndex(x => new { x.ConversationDocumentId, x.Index })
                .HasFilter("[DateDeactivated] IS NULL")
                .IsUnique();
        }
    }
}
