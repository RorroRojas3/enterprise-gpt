using Enterprise.Gpt.Entity.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

/// <summary>
/// Maps <see cref="ConversationDocumentSheetColumn"/> to <c>Core.ConversationDocumentSheetColumn</c>
/// and the unique ordinal index that keeps a sheet's columns dense.
/// </summary>
public sealed class ConversationDocumentSheetColumnConfiguration : IEntityTypeConfiguration<ConversationDocumentSheetColumn>
{
    public void Configure(EntityTypeBuilder<ConversationDocumentSheetColumn> builder)
    {
        builder.ToTable(nameof(ConversationDocumentSheetColumn), "Core");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.InferredType)
            .HasConversion<int>();

        builder.HasIndex(x => new { x.ConversationDocumentSheetId, x.ColumnIndex })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
