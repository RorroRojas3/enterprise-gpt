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

            // Documents are always listed for one conversation at a time, filtered to the active ones.
            builder.HasIndex(x => new { x.ConversationId, x.DateDeactivated });
        }
    }
}
