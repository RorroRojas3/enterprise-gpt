using Enterprise.Gpt.Common.Constants.Usage;
using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Usage;

/// <summary>
/// Configures the MCP servers attached to a <see cref="ConversationUsage"/> call.
/// </summary>
public sealed class ConversationUsageMcpServerConfiguration : BaseCreatedEntityConfiguration<ConversationUsageMcpServer, Guid>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ConversationUsageMcpServer> builder)
    {
        builder.ToTable(nameof(ConversationUsageMcpServer), "Core");

        builder.Property(x => x.McpServerName).HasMaxLength(ConversationUsageMcpServerLimits.McpServerNameMaxLength);

        builder.HasOne(x => x.ConversationUsage)
            .WithMany(x => x.McpServers)
            .HasForeignKey(x => x.ConversationUsageId);

        builder.HasOne(x => x.McpServer)
            .WithMany()
            .HasForeignKey(x => x.McpServerId);

        // Once per call. Unfiltered because these rows are append-only and never soft-deleted.
        builder.HasIndex(x => new { x.ConversationUsageId, x.McpServerId }).IsUnique();

        // Usage reported for one MCP server.
        builder.HasIndex(x => x.McpServerId);
    }
}
