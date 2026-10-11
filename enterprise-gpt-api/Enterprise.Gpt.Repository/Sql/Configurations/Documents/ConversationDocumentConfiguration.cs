using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

public sealed class ConversationDocumentConfiguration : BaseDocumentConfiguration<ConversationDocument, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ConversationDocument> builder)
    {
        builder.ToTable(nameof(ConversationDocument), "Core");

        // Without a store default, AddColumn backfills the CLR default, which the enum does not define.
        // ValueGeneratedNever for the same reason as Model.IsUserSelectable.
        builder.Property(x => x.Type)
            .HasConversion<int>()
            .HasDefaultValue(ConversationDocumentTypes.Uploaded)
            .ValueGeneratedNever();

        builder.HasOne(x => x.Conversation)
            .WithMany(x => x.Documents)
            .HasForeignKey(x => x.ConversationId);

        builder.HasMany(x => x.Chunks)
            .WithOne(x => x.ConversationDocument)
            .HasForeignKey(x => x.ConversationDocumentId);

        builder.HasMany(x => x.Sheets)
            .WithOne(x => x.ConversationDocument)
            .HasForeignKey(x => x.ConversationDocumentId);

        // Per-conversation listing.
        builder.HasIndex(x => new { x.ConversationId, x.DateDeactivated });

        // Caller-scoped cross-conversation listing.
        builder.HasIndex(x => new { x.UserId, x.DateDeactivated });
    }
}
