using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

/// <summary>
/// Maps <see cref="ConversationDocumentSheetRow"/> to <c>Core.ConversationDocumentSheetRow</c>, its
/// native <c>json</c> cells column, and the unique ordinal index that keeps a sheet's rows dense.
/// </summary>
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
