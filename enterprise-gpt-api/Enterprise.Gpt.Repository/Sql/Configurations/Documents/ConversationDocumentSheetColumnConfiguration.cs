using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

/// <summary>
/// Maps <see cref="ConversationDocumentSheetColumn"/> to <c>Core.ConversationDocumentSheetColumn</c>
/// and the unique ordinal index that keeps a sheet's columns dense.
/// </summary>
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
