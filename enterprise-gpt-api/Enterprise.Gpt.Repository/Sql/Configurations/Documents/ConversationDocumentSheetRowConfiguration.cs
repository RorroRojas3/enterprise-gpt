using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

public sealed class ConversationDocumentSheetRowConfiguration : BaseDocumentSheetRowConfiguration<ConversationDocumentSheetRow, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ConversationDocumentSheetRow> builder)
    {
        builder.ToTable(nameof(ConversationDocumentSheetRow), "Core");

        // A json column cannot be an index key, so every supporting index sits on the ordinals.
        builder.HasIndex(x => new { x.ConversationDocumentSheetId, x.RowIndex })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
