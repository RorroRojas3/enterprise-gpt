using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Configures the append-only <see cref="ConversationUsage"/> audit table.
/// </summary>
public sealed class ConversationUsageConfiguration : IEntityTypeConfiguration<ConversationUsage>
{
    public void Configure(EntityTypeBuilder<ConversationUsage> builder)
    {
        builder.ToTable(nameof(ConversationUsage), "Core");

        builder.HasKey(x => x.Id);

        builder.HasOne(x => x.Conversation)
            .WithMany(x => x.Usage)
            .HasForeignKey(x => x.ConversationId);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder.HasOne(x => x.Model)
            .WithMany()
            .HasForeignKey(x => x.ModelId);

        builder.HasOne(x => x.Provider)
            .WithMany()
            .HasForeignKey(x => x.ProviderId);

        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Status).HasConversion<int>();

        // Serves both the per-conversation history and the "newest turn" lookup that restores a
        // conversation's MCP selection. Kind is in the key because that lookup filters on it —
        // without it the engine scans backwards through however many naming rows sit at the tail.
        builder.HasIndex(x => new { x.ConversationId, x.Kind, x.DateCreated });

        // Usage reported for one user over a period.
        builder.HasIndex(x => new { x.UserId, x.DateCreated });

        // Usage reported for one model over a period.
        builder.HasIndex(x => new { x.ModelId, x.DateCreated });
    }
}
