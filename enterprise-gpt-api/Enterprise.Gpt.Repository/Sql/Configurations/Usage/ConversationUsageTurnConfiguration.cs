using Enterprise.Gpt.Common.Constants.Usage;
using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Usage;

/// <summary>
/// Configures the append-only per-model-turn breakdown of a <see cref="ConversationUsage"/> call.
/// </summary>
public sealed class ConversationUsageTurnConfiguration : BaseCreatedEntityConfiguration<ConversationUsageTurn, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ConversationUsageTurn> builder)
    {
        builder.ToTable(nameof(ConversationUsageTurn), "Core");

        builder.Property(x => x.ResponseId).HasMaxLength(ConversationUsageTurnLimits.ResponseIdMaxLength);
        builder.Property(x => x.ReportedModelId).HasMaxLength(ConversationUsageTurnLimits.ReportedModelIdMaxLength);

        builder.HasOne(x => x.ConversationUsage)
            .WithMany(x => x.Turns)
            .HasForeignKey(x => x.ConversationUsageId);

        // A call's turns in order, which tool prompt-cost attribution reads to pair turn N with N+1.
        // Unfiltered because these rows are append-only and never soft-deleted.
        builder.HasIndex(x => new { x.ConversationUsageId, x.Iteration }).IsUnique();
    }
}
