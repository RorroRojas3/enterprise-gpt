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

        // Reading a call's turns back in order, which is also every query that attributes a tool's
        // prompt cost: those pair turn N with turn N+1, so the seek and the ordering come off one
        // index. Unique because a call reports each iteration once — and unfiltered, like the other
        // unique index in this trail, because these rows are append-only and no soft-deleted row
        // can ever block a replacement.
        builder.HasIndex(x => new { x.ConversationUsageId, x.Iteration }).IsUnique();
    }
}
