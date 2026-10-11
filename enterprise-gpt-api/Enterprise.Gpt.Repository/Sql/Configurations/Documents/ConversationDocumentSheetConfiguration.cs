using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

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

        // Filtered so a soft-deleted sheet cannot block re-ingesting the same document.
        builder.HasIndex(x => new { x.ConversationDocumentId, x.SheetIndex })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
