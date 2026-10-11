using Enterprise.Gpt.Common.Constants.Usage;
using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Usage;

/// <summary>
/// Configures the append-only <see cref="ConversationUsage"/> audit table.
/// </summary>
public sealed class ConversationUsageConfiguration : BaseCreatedEntityConfiguration<ConversationUsage, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ConversationUsage> builder)
    {
        builder.ToTable(nameof(ConversationUsage), "Core");

        builder.Property(x => x.DeploymentName).HasMaxLength(ConversationUsageLimits.DeploymentNameMaxLength);
        builder.Property(x => x.InputPricePerMillionTokens).HasColumnType("decimal(18, 6)");
        builder.Property(x => x.OutputPricePerMillionTokens).HasColumnType("decimal(18, 6)");

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

        // History, and the newest-turn lookup that restores the MCP selection, which filters on Kind.
        builder.HasIndex(x => new { x.ConversationId, x.Kind, x.DateCreated });

        // Usage reported for one user over a period.
        builder.HasIndex(x => new { x.UserId, x.DateCreated });

        // Usage reported for one model over a period.
        builder.HasIndex(x => new { x.ModelId, x.DateCreated });

        // GET api/reports/usage reads one window across every dimension, which the indexes above cannot seek.
        // Covering, at roughly double this table's write cost; columnstore is unmeasured under single-row
        // inserts. Building it is offline (see the migration).
        builder.HasIndex(x => x.DateCreated)
            .IncludeProperties(x => new
            {
                x.UserId,
                x.ConversationId,
                x.ModelId,
                x.ProviderId,
                x.Kind,
                x.Status,
                x.InputTokens,
                x.OutputTokens,
                x.ToolInputTokens,
                x.ToolOutputTokens,
                x.ContextTokens,
                x.InputPricePerMillionTokens,
                x.OutputPricePerMillionTokens
            });
    }
}
