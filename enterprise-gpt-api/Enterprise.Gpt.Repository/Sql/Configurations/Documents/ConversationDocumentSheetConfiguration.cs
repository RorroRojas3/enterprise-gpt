using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

/// <summary>
/// Maps <see cref="ConversationDocumentSheet"/> to <c>Core.ConversationDocumentSheet</c>, its column
/// and row relationships, and the unique ordinal index that keeps a document's sheets dense.
/// </summary>
public sealed class ConversationDocumentSheetConfiguration : BaseDocumentSheetConfiguration<ConversationDocumentSheet, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ConversationDocumentSheet> builder)
    {
        builder.ToTable(nameof(ConversationDocumentSheet), "Core");

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
