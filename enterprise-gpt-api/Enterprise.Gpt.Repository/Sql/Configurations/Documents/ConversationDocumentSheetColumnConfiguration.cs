using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

public sealed class ConversationDocumentSheetColumnConfiguration : BaseDocumentSheetColumnConfiguration<ConversationDocumentSheetColumn, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ConversationDocumentSheetColumn> builder)
    {
        builder.ToTable(nameof(ConversationDocumentSheetColumn), "Core");

        builder.HasIndex(x => new { x.ConversationDocumentSheetId, x.ColumnIndex })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
