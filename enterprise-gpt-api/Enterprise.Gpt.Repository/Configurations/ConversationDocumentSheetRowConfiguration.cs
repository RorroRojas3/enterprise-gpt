using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Maps <see cref="ConversationDocumentSheetRow"/> to <c>Core.ConversationDocumentSheetRow</c>, its
/// native <c>json</c> cells column, and the unique ordinal index that keeps a sheet's rows dense.
/// </summary>
public sealed class ConversationDocumentSheetRowConfiguration : IEntityTypeConfiguration<ConversationDocumentSheetRow>
{
    public void Configure(EntityTypeBuilder<ConversationDocumentSheetRow> builder)
    {
        builder.ToTable(nameof(ConversationDocumentSheetRow), "Core");

        builder.HasKey(x => x.Id);

        // Stated rather than inferred: EF only selects the native json type on its own for a complex
        // type mapped with ToJson or for a primitive collection, and this is a hand-serialized string.
        builder.Property(x => x.Cells)
            .HasColumnType("json")
            .IsRequired();

        // A json column cannot be an index key, so every supporting index sits on the ordinals.
        builder.HasIndex(x => new { x.ConversationDocumentSheetId, x.RowIndex })
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
