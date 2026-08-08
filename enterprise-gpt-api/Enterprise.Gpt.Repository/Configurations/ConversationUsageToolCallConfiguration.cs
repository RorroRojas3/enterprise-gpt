using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Configures the append-only <see cref="ConversationUsageToolCall"/> table and the
/// self-relationship that carries tool nesting.
/// </summary>
public sealed class ConversationUsageToolCallConfiguration : IEntityTypeConfiguration<ConversationUsageToolCall>
{
    public void Configure(EntityTypeBuilder<ConversationUsageToolCall> builder)
    {
        builder.ToTable(nameof(ConversationUsageToolCall), "Core");

        builder.HasKey(x => x.Id);

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

        // No index on ModelId yet: it is null on every row this application can currently write,
        // so an index would cost writes on the largest table in the schema and serve nothing. Add
        // it with the first agent that reports a model.
    }
}
