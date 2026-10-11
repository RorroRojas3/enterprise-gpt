using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Documents;

public sealed class ConversationDocumentSummaryConfiguration : BaseDocumentSummaryConfiguration<ConversationDocumentSummary, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ConversationDocumentSummary> builder)
    {
        builder.ToTable(nameof(ConversationDocumentSummary), "Core");

        builder.HasOne(x => x.ConversationDocument)
            .WithMany()
            .HasForeignKey(x => x.ConversationDocumentId);

        // One live summary per document; filtered so a deactivated summary does not block a later one.
        builder.HasIndex(x => x.ConversationDocumentId)
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
