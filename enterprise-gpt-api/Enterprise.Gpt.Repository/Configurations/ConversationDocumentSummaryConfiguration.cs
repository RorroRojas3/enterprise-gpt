using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Maps <see cref="ConversationDocumentSummary"/> to <c>Core.ConversationDocumentSummary</c> and
/// the unique index that keeps a document to one live summary.
/// </summary>
public sealed class ConversationDocumentSummaryConfiguration : IEntityTypeConfiguration<ConversationDocumentSummary>
{
    public void Configure(EntityTypeBuilder<ConversationDocumentSummary> builder)
    {
        builder.ToTable(nameof(ConversationDocumentSummary), "Core");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Text)
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.HasOne(x => x.ConversationDocument)
            .WithMany()
            .HasForeignKey(x => x.ConversationDocumentId);

        builder.HasOne(x => x.Model)
            .WithMany()
            .HasForeignKey(x => x.ModelId);

        // One summary per document, filtered the way the chunk tables' ordinal index is: a summary
        // deactivated alongside its document must not block a later one, and an unfiltered unique
        // index would make that a constraint violation instead.
        builder.HasIndex(x => x.ConversationDocumentId)
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }
}
