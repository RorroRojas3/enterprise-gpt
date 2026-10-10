using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Maps <see cref="ConversationDocumentSheet"/> to <c>Core.ConversationDocumentSheet</c>, its column
/// and row relationships, and the unique ordinal index that keeps a document's sheets dense.
/// </summary>
public sealed class ConversationDocumentSheetConfiguration : IEntityTypeConfiguration<ConversationDocumentSheet>
{
    public void Configure(EntityTypeBuilder<ConversationDocumentSheet> builder)
    {
        builder.ToTable(nameof(ConversationDocumentSheet), "Core");

        builder.HasKey(x => x.Id);

        builder.HasMany(x => x.Columns)
            .WithOne(x => x.ConversationDocumentSheet)
            .HasForeignKey(x => x.ConversationDocumentSheetId);

        builder.HasMany(x => x.Rows)
            .WithOne(x => x.ConversationDocumentSheet)
            .HasForeignKey(x => x.ConversationDocumentSheetId);

        // Per ordinal rather than per document, because a document holds many sheets; filtered the
        // way the chunk tables' ordinal index is, so a soft-deleted sheet cannot block a later
        // re-ingestion of the same document.
        builder.HasIndex(x => new { x.ConversationDocumentId, x.SheetIndex })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
