using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations
{
    public sealed class ConversationDocumentConfiguration : IEntityTypeConfiguration<ConversationDocument>
    {
        public void Configure(EntityTypeBuilder<ConversationDocument> builder)
        {
            builder.ToTable(nameof(ConversationDocument), "Core");

            builder.HasKey(x => x.Id);

            builder.HasOne(x => x.Conversation)
                .WithMany(x => x.Documents)
                .HasForeignKey(x => x.ConversationId);

            builder.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId);

            builder.HasMany(x => x.Chunks)
                .WithOne(x => x.ConversationDocument)
                .HasForeignKey(x => x.ConversationDocumentId);

            // Serves the per-conversation listing: one conversation's documents, filtered to the
            // active ones.
            builder.HasIndex(x => new { x.ConversationId, x.DateDeactivated });

            // Serves the caller-scoped cross-conversation listing, which filters on the owner rather
            // than a conversation.
            builder.HasIndex(x => new { x.UserId, x.DateDeactivated });
        }
    }
}
