using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Configures the MCP servers attached to a <see cref="ConversationUsage"/> call.
/// </summary>
public sealed class ConversationUsageMcpServerConfiguration : IEntityTypeConfiguration<ConversationUsageMcpServer>
{
    public void Configure(EntityTypeBuilder<ConversationUsageMcpServer> builder)
    {
        builder.ToTable(nameof(ConversationUsageMcpServer), "Core");

        builder.HasKey(x => x.Id);

        builder.HasOne(x => x.ConversationUsage)
            .WithMany(x => x.McpServers)
            .HasForeignKey(x => x.ConversationUsageId);

        builder.HasOne(x => x.McpServer)
            .WithMany()
            .HasForeignKey(x => x.McpServerId);

        // A server is attached to a call at most once. Unfiltered, unlike the other unique indexes
        // in this model: these rows are append-only, so no soft-deleted row can ever block a
        // replacement.
        builder.HasIndex(x => new { x.ConversationUsageId, x.McpServerId }).IsUnique();

        // Usage reported for one MCP server.
        builder.HasIndex(x => x.McpServerId);
    }
}
