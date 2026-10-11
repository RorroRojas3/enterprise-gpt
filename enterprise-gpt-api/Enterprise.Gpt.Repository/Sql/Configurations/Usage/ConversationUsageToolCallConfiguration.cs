using Enterprise.Gpt.Common.Constants.Usage;
using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Usage;

/// <summary>
/// Configures the append-only <see cref="ConversationUsageToolCall"/> table and the
/// self-relationship that carries tool nesting.
/// </summary>
public sealed class ConversationUsageToolCallConfiguration : BaseCreatedEntityConfiguration<ConversationUsageToolCall, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ConversationUsageToolCall> builder)
    {
        builder.ToTable(nameof(ConversationUsageToolCall), "Core");

        builder.Property(x => x.ToolName).HasMaxLength(ConversationUsageToolCallLimits.ToolNameMaxLength);
        builder.Property(x => x.Source).HasMaxLength(ConversationUsageToolCallLimits.SourceMaxLength);
        builder.Property(x => x.CallId).HasMaxLength(ConversationUsageToolCallLimits.CallIdMaxLength);
        builder.Property(x => x.DeploymentName).HasMaxLength(ConversationUsageToolCallLimits.DeploymentNameMaxLength);

        builder.HasOne(x => x.ConversationUsage)
            .WithMany(x => x.ToolCalls)
            .HasForeignKey(x => x.ConversationUsageId);

        // The nesting edge. Every row also carries ConversationUsageId, including the nested ones,
        // so a turn's whole tree loads in one query instead of walking the chain.
        builder.HasOne(x => x.Parent)
            .WithMany(x => x.Children)
            .HasForeignKey(x => x.ParentId);

        builder.HasOne(x => x.McpServer)
            .WithMany()
            .HasForeignKey(x => x.McpServerId);

        builder.HasOne(x => x.Model)
            .WithMany()
            .HasForeignKey(x => x.ModelId);

        builder.Property(x => x.Kind).HasConversion<int>();

        // Reading one call's tools back in report order.
        builder.HasIndex(x => new { x.ConversationUsageId, x.Sequence });

        // Walking down from a parent, and the lookup the FK constraint itself needs.
        builder.HasIndex(x => x.ParentId);

        // "What did MCP tools cost this month", and the same question per kind.
        builder.HasIndex(x => new { x.Kind, x.DateCreated });

        // Usage reported for one MCP server over a period.
        builder.HasIndex(x => new { x.McpServerId, x.DateCreated });

        // One agent's model cost over a period, and the per-user ceiling. Filtered: only agent rows carry a
        // ModelId, and NULL keys would cost a write per row on the largest table.
        builder.HasIndex(x => new { x.ModelId, x.DateCreated })
            .HasFilter("[ModelId] IS NOT NULL");

        // No Iteration index: attribution filters one call's rows, which (ConversationUsageId, Sequence) seeks.
    }
}
